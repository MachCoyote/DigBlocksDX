using System;
using System.Collections.Generic;
using System.IO;
using DigBlocks.Voxels;
using Unity.Mathematics;

namespace DigBlocks.ChunkProtocol
{
    /// <summary>
    /// Bounded, little-endian public chunk projection. The session must verify the registry
    /// fingerprint before decoding. This format contains no private metadata or inventories.
    /// </summary>
    public static class ChunkWireCodec
    {
        public const int MaxSnapshotBytes = ChunkLayout.Volume * 8 + 128;
        public const int MaxDeltaBytes = ChunkLayout.Volume * 12 + 128;
        public const ushort Version = 1;
        private const uint Magic = 0x4b434244;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static byte[] EncodeSnapshot(ChunkImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, 1, image.Address, image.Incarnation, image.Revision);
            WriteChannel(writer, image, false);
            WriteChannel(writer, image, true);
            return Seal(stream);
        }

        public static ChunkImage DecodeSnapshot(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId)
        {
            using var reader = Open(bytes, MaxSnapshotBytes);
            ReadHeader(reader, 1, out var address, out ulong incarnation, out ulong revision);
            uint[] solids = ReadChannel(reader, maxSolidStateId);
            uint[] fluids = ReadChannel(reader, maxFluidStateId);
            RequireEnd(reader);
            return ChunkImage.FromOwnedChannels(address, incarnation, revision, solids, fluids);
        }

        /// <summary>
        /// Decodes a snapshot into caller-owned buffers. Every cell of both is overwritten, so a caller
        /// that applies one chunk at a time can reuse a single pair instead of allocating 128 KiB per
        /// channel per chunk, which is large enough to land on the large object heap.
        /// </summary>
        public static void DecodeSnapshotInto(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId,
            uint[] solids, uint[] fluids, out ChunkAddress address, out ulong incarnation, out ulong revision)
        {
            if (solids == null || solids.Length != ChunkLayout.Volume)
                throw new ArgumentException("Solid destination must hold exactly one chunk.", nameof(solids));
            if (fluids == null || fluids.Length != ChunkLayout.Volume)
                throw new ArgumentException("Fluid destination must hold exactly one chunk.", nameof(fluids));
            using var reader = Open(bytes, MaxSnapshotBytes);
            ReadHeader(reader, 1, out address, out incarnation, out revision);
            ReadChannel(reader, maxSolidStateId, solids);
            ReadChannel(reader, maxFluidStateId, fluids);
            RequireEnd(reader);
        }

        public static byte[] EncodeDelta(ChunkDelta delta)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, 2, delta.Address, delta.Incarnation, delta.BaseRevision);
            writer.Write(delta.ResultRevision);
            writer.Write((uint)delta.Updates.Count);
            foreach (var update in delta.Updates)
            {
                writer.Write((uint)update.Index);
                writer.Write(update.Solid);
                writer.Write(update.Fluid);
            }
            return Seal(stream);
        }

        public static ChunkDelta DecodeDelta(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId)
        {
            using var reader = Open(bytes, MaxDeltaBytes);
            ReadHeader(reader, 2, out var address, out ulong incarnation, out ulong revision);
            Require(reader, 12);
            ulong resultRevision = reader.ReadUInt64();
            uint count = reader.ReadUInt32();
            if (resultRevision <= revision || count == 0 || count > ChunkLayout.Volume)
                throw Invalid("Invalid delta revision or cell count.");
            Require(reader, checked((int)count * 12));
            var updates = new ChunkCellUpdate[count];
            var seen = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                uint index = reader.ReadUInt32();
                uint solid = reader.ReadUInt32();
                uint fluid = reader.ReadUInt32();
                if (index >= ChunkLayout.Volume || !seen.Add(index) || solid > maxSolidStateId || fluid > maxFluidStateId)
                    throw Invalid("Invalid delta cell or state.");
                updates[i] = new ChunkCellUpdate((int)index, solid, fluid);
            }
            RequireEnd(reader);
            return new ChunkDelta(address, incarnation, revision, resultRevision, updates);
        }

        private static void WriteHeader(BinaryWriter writer, byte kind, ChunkAddress address, ulong incarnation, ulong revision)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(kind);
            writer.Write((byte)0); //codec 0: packed channels without an outer compression envelope.
            writer.Write((ushort)ChunkLayout.Edge);
            writer.Write(address.World);
            writer.Write(address.Position.x);
            writer.Write(address.Position.y);
            writer.Write(address.Position.z);
            writer.Write(incarnation);
            writer.Write(revision);
        }

        private static void ReadHeader(BinaryReader reader, byte kind, out ChunkAddress address,
            out ulong incarnation, out ulong revision)
        {
            Require(reader, 42);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version || reader.ReadByte() != kind ||
                reader.ReadByte() != 0 || reader.ReadUInt16() != ChunkLayout.Edge)
                throw Invalid("Unsupported chunk format, codec, kind, or layout.");
            uint world = reader.ReadUInt32();
            address = new ChunkAddress(world, new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
            incarnation = reader.ReadUInt64();
            revision = reader.ReadUInt64();
            if (incarnation == 0 || revision == 0) throw Invalid("Zero chunk incarnation or revision.");
        }

        private static void WriteChannel(BinaryWriter writer, ChunkImage image, bool fluid)
        {
            var indices = new Dictionary<uint, int>();
            var palette = new List<uint>();
            for (int i = 0; i < ChunkLayout.Volume; i++)
            {
                uint value = fluid ? image.FluidAt(i) : image.SolidAt(i);
                if (indices.ContainsKey(value)) continue;
                indices.Add(value, palette.Count);
                palette.Add(value);
            }
            if (palette.Count == 1)
            {
                writer.Write((byte)0);
                writer.Write(palette[0]);
                return;
            }
            int bits = BitsFor(palette.Count);
            int packedBytes = (ChunkLayout.Volume * bits + 7) / 8;
            if (6L + palette.Count * 4L + packedBytes >= 1L + ChunkLayout.Volume * 4L)
            {
                writer.Write((byte)2);
                for (int i = 0; i < ChunkLayout.Volume; i++) writer.Write(fluid ? image.FluidAt(i) : image.SolidAt(i));
                return;
            }
            writer.Write((byte)1);
            writer.Write((byte)bits);
            writer.Write((uint)palette.Count);
            foreach (uint value in palette) writer.Write(value);
            ulong accumulator = 0;
            int occupied = 0;
            for (int i = 0; i < ChunkLayout.Volume; i++)
            {
                uint value = fluid ? image.FluidAt(i) : image.SolidAt(i);
                accumulator |= (ulong)indices[value] << occupied;
                occupied += bits;
                while (occupied >= 8)
                {
                    writer.Write((byte)accumulator);
                    accumulator >>= 8;
                    occupied -= 8;
                }
            }
            if (occupied != 0) writer.Write((byte)accumulator);
        }

        private static void ReadChannel(BinaryReader reader, uint maxState, uint[] destination)
        {
            Require(reader, 1);
            byte mode = reader.ReadByte();
            if (mode == 0)
            {
                Require(reader, 4);
                uint value = reader.ReadUInt32();
                if (value > maxState) throw Invalid("Unknown uniform state.");
                //the destination may be reused, so a uniform channel must overwrite every cell.
                for (int i = 0; i < destination.Length; i++) destination[i] = value;
                return;
            }
            if (mode == 2)
            {
                Require(reader, ChunkLayout.Volume * 4);
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = reader.ReadUInt32();
                    if (destination[i] > maxState) throw Invalid("Unknown direct state.");
                }
                return;
            }
            if (mode != 1) throw Invalid("Unknown channel encoding.");
            Require(reader, 5);
            int bits = reader.ReadByte();
            uint count = reader.ReadUInt32();
            if (count < 2 || count > ChunkLayout.Volume || bits != BitsFor((int)count))
                throw Invalid("Invalid palette size or bit width.");
            Require(reader, checked((int)count * 4 + (ChunkLayout.Volume * bits + 7) / 8));
            var palette = new uint[count];
            var distinct = new HashSet<uint>();
            for (int i = 0; i < count; i++)
            {
                palette[i] = reader.ReadUInt32();
                if (palette[i] > maxState || !distinct.Add(palette[i])) throw Invalid("Unknown or duplicate palette state.");
            }
            ulong accumulator = 0;
            int occupied = 0;
            uint mask = (1u << bits) - 1;
            for (int i = 0; i < destination.Length; i++)
            {
                while (occupied < bits)
                {
                    accumulator |= (ulong)reader.ReadByte() << occupied;
                    occupied += 8;
                }
                uint index = (uint)accumulator & mask;
                if (index >= count) throw Invalid("Palette index outside palette.");
                destination[i] = palette[index];
                accumulator >>= bits;
                occupied -= bits;
            }
            if (accumulator != 0) throw Invalid("Nonzero packed padding.");
        }

        private static uint[] ReadChannel(BinaryReader reader, uint maxState)
        {
            var values = new uint[ChunkLayout.Volume];
            ReadChannel(reader, maxState, values);
            return values;
        }

        private static int BitsFor(int count)
        {
            int bits = 0;
            for (int value = count - 1; value != 0; value >>= 1) bits++;
            return bits;
        }

        private static BinaryReader Open(byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length < 46 || bytes.Length > maximum) throw Invalid("Chunk payload length outside bounds.");
            int end = bytes.Length - 4;
            uint expected = (uint)bytes[end] | (uint)bytes[end + 1] << 8 | (uint)bytes[end + 2] << 16 | (uint)bytes[end + 3] << 24;
            if (Checksum(bytes, end) != expected) throw Invalid("Chunk checksum mismatch.");
            return new BinaryReader(new MemoryStream(bytes, 0, end, false));
        }

        private static byte[] Seal(MemoryStream stream)
        {
            byte[] payload = stream.ToArray();
            uint crc = Checksum(payload, payload.Length);
            var result = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
            for (int i = 0; i < 4; i++) result[payload.Length + i] = (byte)(crc >> (i * 8));
            return result;
        }

        private static uint Checksum(byte[] bytes, int count)
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
                table[i] = value;
            }
            return table;
        }

        private static void Require(BinaryReader reader, int count)
        {
            if (count < 0 || reader.BaseStream.Length - reader.BaseStream.Position < count) throw Invalid("Truncated chunk payload.");
        }

        private static void RequireEnd(BinaryReader reader)
        {
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw Invalid("Trailing chunk payload bytes.");
        }

        private static FormatException Invalid(string message) => new FormatException(message);
    }
}
