using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using Unity.Mathematics;

namespace DigBlocks.Networking.NetCode
{
    public enum ChunkFrameKind : byte
    {
        Start = 16, Slice = 17, Acknowledgement = 18, Eviction = 19, Interest = 20, Resync = 21, InterestRequest = 22
    }

    public readonly struct TransferStart
    {
        public readonly ulong TransferId, SubscriptionGeneration, Incarnation, Revision;
        public readonly ChunkAddress Address;
        public readonly int ByteLength;
        public readonly bool IsDelta;
        public TransferStart(ulong transferId, ulong subscriptionGeneration, ChunkAddress address,
            ulong incarnation, ulong revision, int byteLength, bool isDelta)
        {
            TransferId = transferId; SubscriptionGeneration = subscriptionGeneration; Address = address;
            Incarnation = incarnation; Revision = revision; ByteLength = byteLength; IsDelta = isDelta;
        }
    }

    public static class ChunkTransferFrames
    {
        public const int MaxSliceBytes = BulkDriver.MaxPayloadBytes - 20;
        private const ushort Magic = 0x4244;
        private const byte Version = 1;

        public static ChunkFrameKind ReadKind(byte[] packet)
        {
            if (packet == null || packet.Length < 4 || packet.Length > BulkDriver.MaxPayloadBytes ||
                packet[0] != (Magic & 255) || packet[1] != (Magic >> 8) || packet[2] != Version ||
                packet[3] < (byte)ChunkFrameKind.Start || packet[3] > (byte)ChunkFrameKind.InterestRequest)
                throw new FormatException("Invalid chunk frame header.");
            return (ChunkFrameKind)packet[3];
        }

        public static byte[] EncodeStart(TransferStart start)
        {
            if (!ValidStart(start)) throw new ArgumentException("Invalid transfer declaration.", nameof(start));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Start);
            writer.Write(start.TransferId); writer.Write(start.SubscriptionGeneration);
            WriteAddress(writer, start.Address);
            writer.Write(start.Incarnation); writer.Write(start.Revision);
            writer.Write(start.ByteLength); writer.Write(start.IsDelta);
            return stream.ToArray();
        }

        public static TransferStart DecodeStart(byte[] packet)
        {
            using var reader = Open(packet, ChunkFrameKind.Start, 57);
            ulong id = reader.ReadUInt64(), generation = reader.ReadUInt64();
            var address = ReadAddress(reader);
            ulong incarnation = reader.ReadUInt64(), revision = reader.ReadUInt64();
            int length = reader.ReadInt32();
            byte delta = reader.ReadByte();
            var start = new TransferStart(id, generation, address, incarnation, revision, length, delta == 1);
            if (delta > 1 || !ValidStart(start)) throw new FormatException("Invalid transfer declaration.");
            return start;
        }

        public static byte[] EncodeSlice(ulong transferId, int offset, byte[] bytes, int sourceOffset, int count)
        {
            if (transferId == 0 || !ValidSlice(offset, count) || bytes == null || sourceOffset < 0 || sourceOffset > bytes.Length - count)
                throw new ArgumentException("Invalid transfer slice.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Slice);
            writer.Write(transferId); writer.Write(offset); writer.Write(count);
            writer.Write(bytes, sourceOffset, count);
            return stream.ToArray();
        }

        public static void DecodeSlice(byte[] packet, out ulong transferId, out int offset, out byte[] bytes)
        {
            if (ReadKind(packet) != ChunkFrameKind.Slice || packet.Length <= 20)
                throw new FormatException("Invalid transfer slice length or kind.");
            using var reader = Open(packet, ChunkFrameKind.Slice, packet.Length);
            transferId = reader.ReadUInt64(); offset = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (transferId == 0 || !ValidSlice(offset, count) || count != packet.Length - 20)
                throw new FormatException("Invalid transfer slice range.");
            bytes = reader.ReadBytes(count);
        }

        public static byte[] EncodeAcknowledgement(ulong transferId, ulong revision)
        {
            if (transferId == 0 || revision == 0) throw new ArgumentException("Invalid applied acknowledgement.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Acknowledgement);
            writer.Write(transferId); writer.Write(revision);
            return stream.ToArray();
        }

        public static void DecodeAcknowledgement(byte[] packet, out ulong transferId, out ulong revision)
        {
            using var reader = Open(packet, ChunkFrameKind.Acknowledgement, 20);
            transferId = reader.ReadUInt64(); revision = reader.ReadUInt64();
            if (transferId == 0 || revision == 0) throw new FormatException("Invalid applied acknowledgement.");
        }

        public static byte[] EncodeEviction(ChunkAddress address, ulong subscriptionGeneration)
        {
            if (subscriptionGeneration == 0) throw new ArgumentException("Invalid subscription generation.", nameof(subscriptionGeneration));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Eviction);
            WriteAddress(writer, address); writer.Write(subscriptionGeneration);
            return stream.ToArray();
        }

        public static void DecodeEviction(byte[] packet, out ChunkAddress address, out ulong subscriptionGeneration)
        {
            using var reader = Open(packet, ChunkFrameKind.Eviction, 28);
            address = ReadAddress(reader); subscriptionGeneration = reader.ReadUInt64();
            if (subscriptionGeneration == 0) throw new FormatException("Invalid subscription generation.");
        }

        public static byte[] EncodeInterest(ChunkInterest interest)
        {
            if (interest == null) throw new ArgumentNullException(nameof(interest));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Interest);
            writer.Write(interest.Epoch); WriteAddress(writer, interest.Anchor);
            writer.Write(interest.HorizontalRadius); writer.Write(interest.VerticalRadius);
            return stream.ToArray();
        }

        public static ChunkInterest DecodeInterest(byte[] packet)
        {
            using var reader = Open(packet, ChunkFrameKind.Interest, 36);
            ulong epoch = reader.ReadUInt64(); var anchor = ReadAddress(reader);
            try { return new ChunkInterest(epoch, anchor, reader.ReadInt32(), reader.ReadInt32()); }
            catch (ArgumentException exception) { throw new FormatException("Invalid interest declaration.", exception); }
        }

        public static byte[] EncodeResync(ulong transferId)
        {
            if (transferId == 0) throw new ArgumentOutOfRangeException(nameof(transferId));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.Resync); writer.Write(transferId);
            return stream.ToArray();
        }

        public static ulong DecodeResync(byte[] packet)
        {
            using var reader = Open(packet, ChunkFrameKind.Resync, 12);
            ulong id = reader.ReadUInt64();
            if (id == 0) throw new FormatException("Invalid resync identity.");
            return id;
        }

        public static byte[] EncodeInterestRequest(ChunkAddress anchor)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            WriteHeader(writer, ChunkFrameKind.InterestRequest);
            WriteAddress(writer, anchor);
            return stream.ToArray();
        }

        public static ChunkAddress DecodeInterestRequest(byte[] packet)
        {
            using var reader = Open(packet, ChunkFrameKind.InterestRequest, 20);
            return ReadAddress(reader);
        }

        internal static bool ValidStart(TransferStart start) => start.TransferId != 0 && start.SubscriptionGeneration != 0 &&
            start.Incarnation != 0 && start.Revision != 0 && start.ByteLength > 0 &&
            start.ByteLength <= (start.IsDelta ? ChunkWireCodec.MaxDeltaBytes : ChunkWireCodec.MaxSnapshotBytes);

        private static bool ValidSlice(int offset, int count) => count > 0 && count <= MaxSliceBytes &&
            offset >= 0 && offset <= ChunkWireCodec.MaxDeltaBytes - count;

        private static void WriteHeader(BinaryWriter writer, ChunkFrameKind kind)
        { writer.Write(Magic); writer.Write(Version); writer.Write((byte)kind); }

        private static BinaryReader Open(byte[] packet, ChunkFrameKind kind, int length)
        {
            if (ReadKind(packet) != kind || packet.Length != length) throw new FormatException("Invalid chunk frame length or kind.");
            var stream = new MemoryStream(packet, false);
            stream.Position = 4;
            return new BinaryReader(stream);
        }

        private static void WriteAddress(BinaryWriter writer, ChunkAddress address)
        {
            writer.Write(address.World);
            writer.Write(address.Position.x); writer.Write(address.Position.y); writer.Write(address.Position.z);
        }

        private static ChunkAddress ReadAddress(BinaryReader reader) => new ChunkAddress(reader.ReadUInt32(),
            new int3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
    }

    //connection-local staging only; the caller validates live subscriptions and applies decoded data before ACK.
    public sealed class ChunkTransferReassembler
    {
        //matches the largest payload budget a peer may be given, since that is what bounds how many
        //transfers can be mid-reassembly at once.
        private const int TransferLimit = 256;
        private const int ByteLimit = TransferLimit * ChunkWireCodec.MaxDeltaBytes;
        private readonly int maxTransfers, maxTotalBytes;
        private readonly Dictionary<ulong, Pending> pending = new();
        private readonly List<ulong> matched = new();

        private sealed class Pending
        {
            public readonly TransferStart Start;
            public readonly byte[] Bytes;
            public readonly BitArray Received;
            public int ReceivedCount;
            public Pending(TransferStart start)
            { Start = start; Bytes = new byte[start.ByteLength]; Received = new BitArray(start.ByteLength); }
        }

        public ChunkTransferReassembler(int maxTransfers = 2, int maxTotalBytes = 0)
        {
            if (maxTransfers < 1 || maxTransfers > TransferLimit) throw new ArgumentOutOfRangeException(nameof(maxTransfers));
            if (maxTotalBytes < 0 || maxTotalBytes > ByteLimit) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
            this.maxTransfers = maxTransfers;
            this.maxTotalBytes = maxTotalBytes == 0 ? ByteLimit : maxTotalBytes;
        }

        public int Count => pending.Count;
        public int BufferedBytes { get; private set; }

        public bool Begin(TransferStart start)
        {
            if (!ChunkTransferFrames.ValidStart(start)) throw new FormatException("Invalid transfer declaration.");
            if (pending.TryGetValue(start.TransferId, out var existing))
            {
                var previous = existing.Start;
                if (previous.SubscriptionGeneration != start.SubscriptionGeneration || !previous.Address.Equals(start.Address) ||
                    previous.Incarnation != start.Incarnation || previous.Revision != start.Revision ||
                    previous.ByteLength != start.ByteLength || previous.IsDelta != start.IsDelta)
                    throw new FormatException("Conflicting transfer declaration.");
                return true;
            }
            if (pending.Count >= maxTransfers || start.ByteLength > maxTotalBytes - BufferedBytes) return false;
            pending.Add(start.TransferId, new Pending(start));
            BufferedBytes += start.ByteLength;
            return true;
        }

        public bool AddSlice(ulong transferId, int offset, byte[] bytes, out TransferStart start, out byte[] completed)
        {
            start = default; completed = null;
            if (!pending.TryGetValue(transferId, out var transfer)) return false;
            if (bytes == null || bytes.Length == 0 || bytes.Length > ChunkTransferFrames.MaxSliceBytes ||
                offset < 0 || offset > transfer.Bytes.Length - bytes.Length)
                throw new FormatException("Invalid transfer slice range.");
            //validate all overlap before changing bytes or coverage, including new bytes preceding a conflict.
            for (int i = 0; i < bytes.Length; i++)
                if (transfer.Received[offset + i] && transfer.Bytes[offset + i] != bytes[i])
                    throw new FormatException("Conflicting transfer slice.");
            for (int i = 0; i < bytes.Length; i++)
            {
                int index = offset + i;
                if (transfer.Received[index]) continue;
                transfer.Bytes[index] = bytes[i]; transfer.Received[index] = true; transfer.ReceivedCount++;
            }
            if (transfer.ReceivedCount != transfer.Bytes.Length) return false;
            start = transfer.Start; completed = transfer.Bytes;
            Cancel(transferId);
            return true;
        }

        public void Cancel(ulong transferId)
        {
            if (!pending.TryGetValue(transferId, out var transfer)) return;
            BufferedBytes -= transfer.Bytes.Length;
            pending.Remove(transferId);
        }

        public void Cancel(ChunkAddress address, ulong subscriptionGeneration)
        {
            //collect identities before modifying the dictionary; a window can hold several matches.
            matched.Clear();
            foreach (var pair in pending)
                if (pair.Value.Start.Address.Equals(address) && pair.Value.Start.SubscriptionGeneration == subscriptionGeneration)
                    matched.Add(pair.Key);
            foreach (ulong id in matched) Cancel(id);
        }

        public void Clear() { pending.Clear(); BufferedBytes = 0; }
    }
}
