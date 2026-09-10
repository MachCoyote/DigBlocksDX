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
    /// <para>
    /// Codec 1 carries the same packed layout <see cref="PaletteChannel"/> stores, so encoding and
    /// decoding a snapshot are a palette copy and a word copy rather than a re-pack. The cost is that
    /// the storage layout and the protocol move together; the codec byte in the header is what a
    /// future layout change versions against.
    /// </para>
    /// </summary>
    public static class ChunkWireCodec
    {
        public const int MaxSnapshotBytes = ChunkLayout.Volume * 8 + 512;
        public const int MaxDeltaBytes = ChunkLayout.Volume * 12 + 128;
        public const ushort Version = 1;
        public const byte Codec = 1;
        private const int HeaderBytes = 42;
        private const uint Magic = 0x4b434244;
        private static readonly uint[] CrcTable = CreateCrcTable();
        [ThreadStatic] private static HashSet<uint> scratchDistinct;
        [ThreadStatic] private static PackedChannelData scratchSolids, scratchFluids;

        //---------------------------------------------------------------- snapshots

        public static byte[] EncodeSnapshot(ChunkAddress address, ulong incarnation, ulong revision,
            PackedChannelData solids, PackedChannelData fluids)
        {
            if (solids == null) throw new ArgumentNullException(nameof(solids));
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            int total = HeaderBytes + ChannelBytes(solids) + ChannelBytes(fluids) + 4;
            var payload = new byte[total];
            int at = 0;
            WriteHeader(payload, ref at, 1, address, incarnation, revision);
            WriteChannel(payload, ref at, solids);
            WriteChannel(payload, ref at, fluids);
            if (at != total - 4) throw new InvalidOperationException("Snapshot length disagreed with its channels.");
            WriteUInt32(payload, ref at, Checksum(payload, total - 4));
            return payload;
        }

        /// <summary>Convenience for callers holding expanded cells; packs both channels first.</summary>
        public static byte[] EncodeSnapshot(ChunkImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var solids = scratchSolids ??= new PackedChannelData();
            var fluids = scratchFluids ??= new PackedChannelData();
            Pack(image.Solids, solids);
            Pack(image.Fluids, fluids);
            return EncodeSnapshot(image.Address, image.Incarnation, image.Revision, solids, fluids);
        }

        /// <summary>
        /// Decodes into caller-owned buffers. A caller applying one chunk at a time reuses a single
        /// pair rather than allocating per chunk.
        /// </summary>
        public static void DecodeSnapshotInto(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId,
            PackedChannelData solids, PackedChannelData fluids, out ChunkAddress address,
            out ulong incarnation, out ulong revision)
        {
            if (solids == null) throw new ArgumentNullException(nameof(solids));
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            int end = Open(bytes, MaxSnapshotBytes);
            int at = 0;
            ReadHeader(bytes, ref at, end, 1, out address, out incarnation, out revision);
            ReadChannel(bytes, ref at, end, maxSolidStateId, solids);
            ReadChannel(bytes, ref at, end, maxFluidStateId, fluids);
            if (at != end) throw Invalid("Trailing chunk payload bytes.");
        }

        public static ChunkImage DecodeSnapshot(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId)
        {
            var solids = scratchSolids ??= new PackedChannelData();
            var fluids = scratchFluids ??= new PackedChannelData();
            DecodeSnapshotInto(bytes, maxSolidStateId, maxFluidStateId, solids, fluids,
                out var address, out ulong incarnation, out ulong revision);
            return ChunkImage.FromOwnedChannels(address, incarnation, revision, Expand(solids), Expand(fluids));
        }

        /// <summary>Packs expanded cells into the shared layout, for callers that do not hold one already.</summary>
        public static void Pack(ReadOnlySpan<uint> values, PackedChannelData destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (values.Length != ChunkLayout.Volume) throw new ArgumentException("A channel holds exactly one chunk.", nameof(values));
            PackedChannelData.Pack(values, destination);
        }

        /// <summary>Expands a packed channel back to one value per cell.</summary>
        public static uint[] Expand(PackedChannelData source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var values = new uint[ChunkLayout.Volume];
            Expand(source, values);
            return values;
        }

        public static void Expand(PackedChannelData source, uint[] destination)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null || destination.Length != ChunkLayout.Volume)
                throw new ArgumentException("A channel holds exactly one chunk.", nameof(destination));
            if (source.Storage == ChannelStorage.Uniform)
            {
                uint value = source.Palette[0];
                for (int i = 0; i < destination.Length; i++) destination[i] = value;
                return;
            }
            int bits = source.BitsPerEntry, perWord = PaletteChannel.EntriesPerWord(bits);
            ulong mask = (1ul << bits) - 1;
            bool indirect = source.Storage == ChannelStorage.Indirect;
            for (int i = 0; i < destination.Length; i++)
            {
                int word = i / perWord;
                uint raw = (uint)(source.Words[word] >> (i - word * perWord) * bits & mask);
                destination[i] = indirect ? source.Palette[(int)raw] : raw;
            }
        }

        //---------------------------------------------------------------- channel framing

        private static int ChannelBytes(PackedChannelData channel) => channel.Storage switch
        {
            ChannelStorage.Uniform => 5,
            ChannelStorage.Direct => 1 + channel.WordCount * 8,
            _ => 5 + channel.PaletteCount * 4 + channel.WordCount * 8
        };

        private static void WriteChannel(byte[] payload, ref int at, PackedChannelData channel)
        {
            if (channel.Storage == ChannelStorage.Uniform)
            {
                payload[at++] = 0;
                WriteUInt32(payload, ref at, channel.Palette[0]);
                return;
            }
            payload[at++] = (byte)channel.BitsPerEntry;
            if (channel.Storage == ChannelStorage.Indirect)
            {
                WriteUInt32(payload, ref at, (uint)channel.PaletteCount);
                for (int i = 0; i < channel.PaletteCount; i++) WriteUInt32(payload, ref at, channel.Palette[i]);
            }
            int wordBytes = channel.WordCount * 8;
            //the words go across verbatim; this is the copy the whole format exists to make possible.
            Buffer.BlockCopy(channel.Words, 0, payload, at, wordBytes);
            at += wordBytes;
        }

        private static void ReadChannel(byte[] bytes, ref int at, int end, uint maxState, PackedChannelData channel)
        {
            Require(at, end, 1);
            int bits = bytes[at++];
            if (bits == 0)
            {
                Require(at, end, 4);
                uint value = ReadUInt32(bytes, ref at);
                if (value > maxState) throw Invalid("Unknown uniform state.");
                channel.SetUniform(value);
                return;
            }
            bool indirect = bits <= PaletteChannel.MaxIndirectBits;
            if (!indirect && bits != PaletteChannel.DirectBits) throw Invalid("Unknown channel width.");
            int paletteCount = 0;
            if (indirect)
            {
                Require(at, end, 4);
                uint declared = ReadUInt32(bytes, ref at);
                //the width must be the narrowest one that indexes the palette, so the encoding is canonical.
                if (declared < 2 || declared > PaletteChannel.MaxPaletteEntries ||
                    PaletteChannel.BitsFor((int)declared) != bits) throw Invalid("Invalid palette size or bit width.");
                paletteCount = (int)declared;
                Require(at, end, paletteCount * 4);
                var distinct = scratchDistinct ??= new HashSet<uint>();
                distinct.Clear();
                for (int i = 0; i < paletteCount; i++)
                {
                    uint value = ReadUInt32(bytes, ref at);
                    if (value > maxState || !distinct.Add(value)) throw Invalid("Unknown or duplicate palette state.");
                    channel.Palette[i] = value;
                }
            }
            int wordCount = PaletteChannel.WordCount(bits);
            Require(at, end, wordCount * 8);
            Buffer.BlockCopy(bytes, at, channel.Words, 0, wordCount * 8);
            at += wordCount * 8;
            channel.Storage = indirect ? ChannelStorage.Indirect : ChannelStorage.Direct;
            channel.BitsPerEntry = bits;
            channel.PaletteCount = paletteCount;
            ValidateCells(channel, maxState);
        }

        //every cell has to be in range before anything reads the channel through a palette, and a
        //word's unused high bits have to be zero or two equal chunks would not encode alike.
        private static void ValidateCells(PackedChannelData channel, uint maxState)
        {
            int bits = channel.BitsPerEntry, perWord = PaletteChannel.EntriesPerWord(bits);
            ulong mask = (1ul << bits) - 1;
            ulong ceiling = channel.Storage == ChannelStorage.Indirect ? (ulong)channel.PaletteCount : maxState + 1ul;
            int remaining = ChunkLayout.Volume;
            for (int word = 0; word < channel.WordCount; word++)
            {
                ulong value = channel.Words[word];
                int carried = Math.Min(perWord, remaining);
                for (int entry = 0; entry < carried; entry++)
                    if ((value >> entry * bits & mask) >= ceiling)
                        throw Invalid(channel.Storage == ChannelStorage.Indirect
                            ? "Palette index outside palette." : "Unknown direct state.");
                int used = carried * bits;
                if (used < 64 && value >> used != 0) throw Invalid("Nonzero packed padding.");
                remaining -= carried;
            }
        }

        //---------------------------------------------------------------- deltas

        public static byte[] EncodeDelta(ChunkDelta delta)
        {
            if (delta == null) throw new ArgumentNullException(nameof(delta));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            var header = new byte[HeaderBytes];
            int at = 0;
            WriteHeader(header, ref at, 2, delta.Address, delta.Incarnation, delta.BaseRevision);
            writer.Write(header);
            writer.Write(delta.ResultRevision);
            writer.Write((uint)delta.Updates.Count);
            foreach (var update in delta.Updates)
            {
                writer.Write((uint)update.Index);
                writer.Write(update.Solid);
                writer.Write(update.Fluid);
            }
            writer.Flush();
            byte[] body = stream.ToArray();
            var result = new byte[body.Length + 4];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            int tail = body.Length;
            WriteUInt32(result, ref tail, Checksum(result, body.Length));
            return result;
        }

        public static ChunkDelta DecodeDelta(byte[] bytes, uint maxSolidStateId, uint maxFluidStateId)
        {
            int end = Open(bytes, MaxDeltaBytes);
            int at = 0;
            ReadHeader(bytes, ref at, end, 2, out var address, out ulong incarnation, out ulong revision);
            Require(at, end, 12);
            ulong resultRevision = ReadUInt64(bytes, ref at);
            uint count = ReadUInt32(bytes, ref at);
            if (resultRevision <= revision || count == 0 || count > ChunkLayout.Volume)
                throw Invalid("Invalid delta revision or cell count.");
            Require(at, end, checked((int)count * 12));
            var updates = new ChunkCellUpdate[count];
            var seen = scratchDistinct ??= new HashSet<uint>();
            seen.Clear();
            for (int i = 0; i < count; i++)
            {
                uint index = ReadUInt32(bytes, ref at);
                uint solid = ReadUInt32(bytes, ref at);
                uint fluid = ReadUInt32(bytes, ref at);
                if (index >= ChunkLayout.Volume || !seen.Add(index) || solid > maxSolidStateId || fluid > maxFluidStateId)
                    throw Invalid("Invalid delta cell or state.");
                updates[i] = new ChunkCellUpdate((int)index, solid, fluid);
            }
            if (at != end) throw Invalid("Trailing chunk payload bytes.");
            return new ChunkDelta(address, incarnation, revision, resultRevision, updates);
        }

        //---------------------------------------------------------------- header and primitives

        private static void WriteHeader(byte[] payload, ref int at, byte kind, ChunkAddress address, ulong incarnation, ulong revision)
        {
            WriteUInt32(payload, ref at, Magic);
            payload[at++] = (byte)Version; payload[at++] = (byte)(Version >> 8);
            payload[at++] = kind;
            payload[at++] = Codec;
            payload[at++] = (byte)ChunkLayout.Edge; payload[at++] = (byte)(ChunkLayout.Edge >> 8);
            WriteUInt32(payload, ref at, address.World);
            WriteUInt32(payload, ref at, (uint)address.Position.x);
            WriteUInt32(payload, ref at, (uint)address.Position.y);
            WriteUInt32(payload, ref at, (uint)address.Position.z);
            WriteUInt64(payload, ref at, incarnation);
            WriteUInt64(payload, ref at, revision);
        }

        private static void ReadHeader(byte[] bytes, ref int at, int end, byte kind, out ChunkAddress address,
            out ulong incarnation, out ulong revision)
        {
            Require(at, end, HeaderBytes);
            if (ReadUInt32(bytes, ref at) != Magic) throw Invalid("Unsupported chunk format.");
            ushort version = (ushort)(bytes[at] | bytes[at + 1] << 8); at += 2;
            if (version != Version || bytes[at++] != kind || bytes[at++] != Codec ||
                (ushort)(bytes[at] | bytes[at + 1] << 8) != ChunkLayout.Edge)
                throw Invalid("Unsupported chunk format, codec, kind, or layout.");
            at += 2;
            uint world = ReadUInt32(bytes, ref at);
            address = new ChunkAddress(world, new int3((int)ReadUInt32(bytes, ref at),
                (int)ReadUInt32(bytes, ref at), (int)ReadUInt32(bytes, ref at)));
            incarnation = ReadUInt64(bytes, ref at);
            revision = ReadUInt64(bytes, ref at);
            if (incarnation == 0 || revision == 0) throw Invalid("Zero chunk incarnation or revision.");
        }

        private static int Open(byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length < HeaderBytes + 4 || bytes.Length > maximum)
                throw Invalid("Chunk payload length outside bounds.");
            int end = bytes.Length - 4;
            uint expected = (uint)bytes[end] | (uint)bytes[end + 1] << 8 | (uint)bytes[end + 2] << 16 | (uint)bytes[end + 3] << 24;
            if (Checksum(bytes, end) != expected) throw Invalid("Chunk checksum mismatch.");
            return end;
        }

        private static void WriteUInt32(byte[] payload, ref int at, uint value)
        {
            payload[at] = (byte)value; payload[at + 1] = (byte)(value >> 8);
            payload[at + 2] = (byte)(value >> 16); payload[at + 3] = (byte)(value >> 24);
            at += 4;
        }

        private static void WriteUInt64(byte[] payload, ref int at, ulong value)
        {
            WriteUInt32(payload, ref at, (uint)value);
            WriteUInt32(payload, ref at, (uint)(value >> 32));
        }

        private static uint ReadUInt32(byte[] bytes, ref int at)
        {
            uint value = (uint)bytes[at] | (uint)bytes[at + 1] << 8 | (uint)bytes[at + 2] << 16 | (uint)bytes[at + 3] << 24;
            at += 4;
            return value;
        }

        private static ulong ReadUInt64(byte[] bytes, ref int at) => ReadUInt32(bytes, ref at) | (ulong)ReadUInt32(bytes, ref at) << 32;

        private static void Require(int at, int end, int count)
        {
            if (count < 0 || end - at < count) throw Invalid("Truncated chunk payload.");
        }

        //slice-by-eight CRC32. The byte-at-a-time form was fine when decoding a chunk cost milliseconds;
        //now that it is a copy, walking the payload one byte at a time would be the dominant cost.
        private static uint Checksum(byte[] bytes, int count)
        {
            uint crc = uint.MaxValue;
            int at = 0;
            var table = CrcTable;
            for (; count - at >= 8; at += 8)
            {
                crc ^= (uint)bytes[at] | (uint)bytes[at + 1] << 8 | (uint)bytes[at + 2] << 16 | (uint)bytes[at + 3] << 24;
                crc = table[1792 + (crc & 255)] ^ table[1536 + (crc >> 8 & 255)] ^ table[1280 + (crc >> 16 & 255)]
                    ^ table[1024 + (crc >> 24)] ^ table[768 + bytes[at + 4]] ^ table[512 + bytes[at + 5]]
                    ^ table[256 + bytes[at + 6]] ^ table[bytes[at + 7]];
            }
            for (; at < count; at++) crc = table[(crc ^ bytes[at]) & 255] ^ crc >> 8;
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[8 * 256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
                table[i] = value;
            }
            for (int slice = 1; slice < 8; slice++)
            for (int i = 0; i < 256; i++)
            {
                uint previous = table[(slice - 1) * 256 + i];
                table[slice * 256 + i] = previous >> 8 ^ table[previous & 255];
            }
            return table;
        }

        private static FormatException Invalid(string message) => new FormatException(message);
    }
}
