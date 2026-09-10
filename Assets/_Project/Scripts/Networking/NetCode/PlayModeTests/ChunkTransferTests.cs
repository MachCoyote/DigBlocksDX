using System;
using DigBlocks.Voxels;
using DigBlocks.ChunkProtocol;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class ChunkTransferTests
    {
        private static readonly ChunkAddress Address = new ChunkAddress(7, new int3(-2, 4, -8));
        private static TransferStart Start(ulong id = 1, int length = 6, ulong generation = 3)
            => new TransferStart(id, generation, Address, 9, 12, length, false);

        [Test]
        public void AllFramesRoundTripWithinCarrierLimit()
        {
            byte[] packet = ChunkTransferFrames.EncodeStart(Start());
            Assert.That(ChunkTransferFrames.ReadKind(packet), Is.EqualTo(ChunkFrameKind.Start));
            var start = ChunkTransferFrames.DecodeStart(packet);
            Assert.That(start.Address, Is.EqualTo(Address));
            Assert.That(start.TransferId, Is.EqualTo(1));
            Assert.That(start.SubscriptionGeneration, Is.EqualTo(3));
            Assert.That(start.Incarnation, Is.EqualTo(9));
            Assert.That(start.Revision, Is.EqualTo(12));
            Assert.That(start.ByteLength, Is.EqualTo(6));
            Assert.That(start.IsDelta, Is.False);
            var bytes = new byte[ChunkTransferFrames.MaxSliceBytes];
            bytes[0] = 42; bytes[bytes.Length - 1] = 99;
            packet = ChunkTransferFrames.EncodeSlice(1, 4, bytes, 0, bytes.Length);
            Assert.That(packet.Length, Is.EqualTo(BulkDriver.MaxPayloadBytes));
            ChunkTransferFrames.DecodeSlice(packet, out ulong id, out int offset, out byte[] slice);
            Assert.That(id, Is.EqualTo(1)); Assert.That(offset, Is.EqualTo(4));
            Assert.That(slice, Is.EqualTo(bytes));
            packet = ChunkTransferFrames.EncodeAcknowledgement(1, 12);
            ChunkTransferFrames.DecodeAcknowledgement(packet, out id, out ulong revision);
            Assert.That(id, Is.EqualTo(1)); Assert.That(revision, Is.EqualTo(12));
            packet = ChunkTransferFrames.EncodeEviction(Address, 3);
            ChunkTransferFrames.DecodeEviction(packet, out var address, out ulong generation);
            Assert.That(address, Is.EqualTo(Address)); Assert.That(generation, Is.EqualTo(3));
            packet = ChunkTransferFrames.EncodeInterest(new ChunkInterest(4, Address, 2, 1));
            var interest = ChunkTransferFrames.DecodeInterest(packet);
            Assert.That(interest.Epoch, Is.EqualTo(4)); Assert.That(interest.Anchor, Is.EqualTo(Address));
            Assert.That(interest.Count, Is.EqualTo(ChunkInterest.CountFor(2, 1)));
            Assert.That(ChunkTransferFrames.DecodeResync(ChunkTransferFrames.EncodeResync(91)), Is.EqualTo(91));
            Assert.That(ChunkTransferFrames.DecodeInterestRequest(ChunkTransferFrames.EncodeInterestRequest(Address)), Is.EqualTo(Address));
        }

        [Test]
        public void InterestAndResyncRejectMalformedLengthsAndUnboundedDeclarations()
        {
            var packet = ChunkTransferFrames.EncodeInterest(new ChunkInterest(1, Address, 1, 0));
            for (int length = 0; length < packet.Length; length++)
            {
                var truncated = new byte[length]; Array.Copy(packet, truncated, length);
                Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeInterest(truncated));
            }
            Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, packet, 28, 4);
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeInterest(packet));
            packet = ChunkTransferFrames.EncodeResync(1); Array.Clear(packet, 4, 8);
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeResync(packet));
            packet = ChunkTransferFrames.EncodeInterestRequest(Address);
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeInterestRequest(packet[..^1]));
        }

        [Test]
        public void FramesRejectMalformedLengthHeaderKindAndInvalidValues()
        {
            byte[] valid = ChunkTransferFrames.EncodeStart(Start());
            for (int length = 0; length < valid.Length; length++)
            {
                var truncated = new byte[length];
                Array.Copy(valid, truncated, length);
                Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeStart(truncated));
            }
            var extended = new byte[valid.Length + 1]; Array.Copy(valid, extended, valid.Length);
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeStart(extended));
            valid[0] ^= 1;
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeStart(valid));
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeStart(ChunkTransferFrames.EncodeAcknowledgement(1, 2)));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeStart(Start(0)));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeStart(Start(length: 0)));
        }

        [Test]
        public void ReassemblyHandlesOutOfOrderAndIdenticalDuplicatesWithoutPrematureCompletion()
        {
            var assembler = new ChunkTransferReassembler();
            Assert.That(assembler.Begin(Start()), Is.True);
            Assert.That(assembler.Begin(Start()), Is.True);
            Assert.That(assembler.Count, Is.EqualTo(1));
            byte[] tail = { 4, 5, 6 };
            Assert.That(assembler.AddSlice(1, 3, tail, out _, out _), Is.False);
            tail[0] = 0;
            Assert.That(assembler.AddSlice(1, 3, new byte[] { 4, 5, 6 }, out _, out _), Is.False);
            Assert.That(assembler.AddSlice(1, 0, new byte[] { 1, 2, 3 }, out var start, out var result), Is.True);
            Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
            Assert.That(start.Revision, Is.EqualTo(12));
            Assert.That(assembler.Count, Is.Zero); Assert.That(assembler.BufferedBytes, Is.Zero);
            Assert.That(assembler.AddSlice(1, 0, new byte[] { 9 }, out _, out _), Is.False);
        }

        [Test]
        public void InvalidOrConflictingSlicesCannotCorruptAlreadyReceivedBytes()
        {
            var assembler = new ChunkTransferReassembler(); assembler.Begin(Start());
            assembler.AddSlice(1, 0, new byte[] { 1, 2 }, out _, out _);
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, 0, new byte[] { 8, 2, 3 }, out _, out _));
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, -1, new byte[] { 3 }, out _, out _));
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, 6, new byte[] { 3 }, out _, out _));
            Assert.Throws<FormatException>(() => assembler.Begin(Start(length: 7)));
            Assert.That(assembler.AddSlice(1, 2, new byte[] { 3, 4, 5, 6 }, out _, out var result), Is.True);
            Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void CapacityCancellationAndSubscriptionGenerationAreBounded()
        {
            var assembler = new ChunkTransferReassembler(2, 12);
            Assert.That(assembler.Begin(Start()), Is.True);
            Assert.That(assembler.Begin(Start(2)), Is.True);
            Assert.That(assembler.Begin(Start(3)), Is.False);
            Assert.That(assembler.BufferedBytes, Is.EqualTo(12));
            assembler.Cancel(Address, 2);
            Assert.That(assembler.Count, Is.EqualTo(2));
            assembler.Cancel(1);
            Assert.That(assembler.Begin(Start(3)), Is.True);
            assembler.Cancel(Address, 3);
            Assert.That(assembler.Count, Is.Zero);
            Assert.That(assembler.BufferedBytes, Is.Zero);
            Assert.That(assembler.Begin(Start(length: 13)), Is.False);
            assembler.Begin(Start()); assembler.Clear();
            Assert.That(assembler.Count, Is.Zero);
        }

        [Test]
        public void EveryFrameRejectsTruncationTrailingBytesAndInvalidHeaders()
        {
            byte[][] packets = {
                ChunkTransferFrames.EncodeStart(Start()),
                ChunkTransferFrames.EncodeSlice(1, 0, new byte[] { 7 }, 0, 1),
                ChunkTransferFrames.EncodeAcknowledgement(1, 12),
                ChunkTransferFrames.EncodeEviction(Address, 3)
            };
            Action<byte[]>[] decode = {
                packet => ChunkTransferFrames.DecodeStart(packet),
                packet => ChunkTransferFrames.DecodeSlice(packet, out _, out _, out _),
                packet => ChunkTransferFrames.DecodeAcknowledgement(packet, out _, out _),
                packet => ChunkTransferFrames.DecodeEviction(packet, out _, out _)
            };
            for (int kind = 0; kind < packets.Length; kind++)
            {
                byte[] valid = packets[kind];
                Assert.Throws<FormatException>(() => decode[kind](null));
                for (int length = 0; length < valid.Length; length++)
                {
                    var truncated = new byte[length]; Array.Copy(valid, truncated, length);
                    Assert.Throws<FormatException>(() => decode[kind](truncated));
                }
                var extended = new byte[valid.Length + 1]; Array.Copy(valid, extended, valid.Length);
                Assert.Throws<FormatException>(() => decode[kind](extended));
                for (int header = 0; header < 4; header++)
                {
                    var corrupt = (byte[])valid.Clone(); corrupt[header] = 255;
                    Assert.Throws<FormatException>(() => decode[kind](corrupt));
                }
            }
            Assert.Throws<FormatException>(() => ChunkTransferFrames.ReadKind(new byte[BulkDriver.MaxPayloadBytes + 1]));
        }

        [Test]
        public void FrameValuesRespectProtocolBoundsAndSliceSourceRange()
        {
            var delta = new TransferStart(ulong.MaxValue, ulong.MaxValue, Address,
                ulong.MaxValue, ulong.MaxValue, DigBlocks.ChunkProtocol.ChunkWireCodec.MaxDeltaBytes, true);
            Assert.That(ChunkTransferFrames.DecodeStart(ChunkTransferFrames.EncodeStart(delta)).IsDelta, Is.True);
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeStart(Start(length: DigBlocks.ChunkProtocol.ChunkWireCodec.MaxSnapshotBytes + 1)));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeStart(Start(generation: 0)));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeAcknowledgement(0, 1));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeAcknowledgement(1, 0));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeEviction(Address, 0));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeSlice(1, int.MaxValue, new byte[] { 1 }, 0, 1));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeSlice(1, 0, new byte[] { 1 }, int.MaxValue, 1));
            Assert.Throws<ArgumentException>(() => ChunkTransferFrames.EncodeSlice(1, 0, new byte[ChunkTransferFrames.MaxSliceBytes + 1], 0, ChunkTransferFrames.MaxSliceBytes + 1));
            var packet = ChunkTransferFrames.EncodeSlice(1, 5, new byte[] { 0, 4, 8, 0 }, 1, 2);
            ChunkTransferFrames.DecodeSlice(packet, out _, out _, out var slice);
            Assert.That(slice, Is.EqualTo(new byte[] { 4, 8 }));
            packet = ChunkTransferFrames.EncodeStart(Start());
            packet[packet.Length - 1] = 2;
            Assert.Throws<FormatException>(() => ChunkTransferFrames.DecodeStart(packet));
        }

        [Test]
        public void OverlapValidationIsAtomicAndMatchingOverlapCountsEachByteOnce()
        {
            var assembler = new ChunkTransferReassembler(); assembler.Begin(Start());
            assembler.AddSlice(1, 2, new byte[] { 3, 4 }, out _, out _);
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, 0, new byte[] { 9, 9, 3, 8 }, out _, out _));
            Assert.That(assembler.AddSlice(1, 0, new byte[] { 1, 2, 3 }, out _, out _), Is.False);
            Assert.That(assembler.AddSlice(1, 3, new byte[] { 4, 5, 6 }, out _, out var result), Is.True);
            Assert.That(result, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void InvalidStartsAndSlicesCannotAllocateOrReleaseUnrelatedTransfers()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkTransferReassembler(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkTransferReassembler(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkTransferReassembler(2, -1));
            var assembler = new ChunkTransferReassembler();
            Assert.Throws<FormatException>(() => assembler.Begin(Start(0)));
            Assert.Throws<FormatException>(() => assembler.Begin(Start(length: int.MaxValue)));
            Assert.That(assembler.BufferedBytes, Is.Zero);
            assembler.Begin(Start());
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, 0, null, out _, out _));
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, 0, Array.Empty<byte>(), out _, out _));
            Assert.Throws<FormatException>(() => assembler.AddSlice(1, int.MaxValue, new byte[] { 1 }, out _, out _));
            Assert.Throws<FormatException>(() => assembler.Begin(Start(generation: 4)));
            assembler.Cancel(new ChunkAddress(8, Address.Position), 3);
            Assert.That(assembler.Count, Is.EqualTo(1));
            Assert.That(assembler.BufferedBytes, Is.EqualTo(6));
        }
    }
}
