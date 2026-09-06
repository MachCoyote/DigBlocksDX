using System;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.ChunkProtocol.Tests
{
    public sealed class ChunkWireCodecTests
    {
        private static readonly ChunkAddress Address = new ChunkAddress(3, new int3(-4, 2, 9));

        [Test]
        public void UniformSnapshotRoundTripsWithoutDenseWirePayload()
        {
            var solids = new uint[ChunkLayout.Volume];
            for (int i = 0; i < solids.Length; i++) solids[i] = 70000;
            var source = new ChunkImage(Address, 9, 42, solids, new uint[ChunkLayout.Volume]);
            byte[] bytes = ChunkWireCodec.EncodeSnapshot(source);
            Assert.That(bytes.Length, Is.LessThan(128));
            var result = ChunkWireCodec.DecodeSnapshot(bytes, 70000, 0);
            Assert.That(result.Address, Is.EqualTo(Address));
            Assert.That(result.Incarnation, Is.EqualTo(9));
            Assert.That(result.Revision, Is.EqualTo(42));
            CollectionAssert.AreEqual(solids, result.CopySolids());
            CollectionAssert.AreEqual(new uint[ChunkLayout.Volume], result.CopyFluids());
        }

        [Test]
        public void ImageDetachesCallerArrays()
        {
            var solids = new uint[ChunkLayout.Volume];
            var image = new ChunkImage(Address, 1, 1, solids, new uint[ChunkLayout.Volume]);
            solids[0] = 8;
            Assert.That(image.SolidAt(0), Is.Zero);
        }

        [TestCase(3)]
        [TestCase(257)]
        [TestCase(1024)]
        [TestCase(32768)]
        public void MixedChannelsRoundTripAcrossPaletteWidthsAndDirectFallback(int stateCount)
        {
            var solids = new uint[ChunkLayout.Volume];
            var fluids = new uint[ChunkLayout.Volume];
            for (int i = 0; i < solids.Length; i++)
            {
                solids[i] = (uint)(i % stateCount + 100000);
                fluids[i] = (uint)(i % 3);
            }
            var image = new ChunkImage(Address, 1, 2, solids, fluids);
            byte[] bytes = ChunkWireCodec.EncodeSnapshot(image);
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(ChunkWireCodec.MaxSnapshotBytes));
            var copy = ChunkWireCodec.DecodeSnapshot(bytes, uint.MaxValue, 2);
            CollectionAssert.AreEqual(solids, copy.CopySolids());
            CollectionAssert.AreEqual(fluids, copy.CopyFluids());
        }

        [Test]
        public void SnapshotRejectsTruncationCorruptionWrongKindAndUnknownStates()
        {
            var image = new ChunkImage(Address, 1, 2, new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);
            byte[] bytes = ChunkWireCodec.EncodeSnapshot(image);
            for (int length = 0; length < bytes.Length; length++)
            {
                byte[] truncated = new byte[length];
                Array.Copy(bytes, truncated, length);
                Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(truncated, 0, 0));
            }
            bytes[20] ^= 1;
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(bytes, 0, 0));
            byte[] delta = ChunkWireCodec.EncodeDelta(new ChunkDelta(Address, 1, 2, 3,
                new[] { new ChunkCellUpdate(0, 1, 0) }));
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(delta, 1, 0));
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(delta, 0, 0));
        }

        [Test]
        public void SnapshotRejectsExtraBytesEvenWithCorrectChecksum()
        {
            byte[] valid = ChunkWireCodec.EncodeSnapshot(new ChunkImage(Address, 1, 1,
                new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]));
            byte[] extended = new byte[valid.Length + 1];
            Array.Copy(valid, extended, valid.Length - 4);
            SealChecksum(extended);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(extended, 0, 0));
        }

        [Test]
        public void SnapshotRejectsInvalidChannelModeAndLayoutWithCorrectChecksum()
        {
            byte[] bytes = ChunkWireCodec.EncodeSnapshot(new ChunkImage(Address, 1, 1,
                new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]));
            bytes[42] = 99;
            SealChecksum(bytes);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(bytes, 0, 0));
            bytes[42] = 0;
            bytes[8] ^= 1;
            SealChecksum(bytes);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(bytes, 0, 0));
        }

        [Test]
        public void DeltaRoundTripAppliesBothChannelsAtomicallyToMatchingBaseline()
        {
            var baseline = new ChunkImage(Address, 8, 10, new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);
            var updates = new[] { new ChunkCellUpdate(1, 70000, 3), new ChunkCellUpdate(ChunkLayout.Volume - 1, 4, 2) };
            var delta = new ChunkDelta(Address, 8, 10, 15, updates);
            updates[0] = new ChunkCellUpdate(2, 9, 9);
            byte[] bytes = ChunkWireCodec.EncodeDelta(delta);
            var decoded = ChunkWireCodec.DecodeDelta(bytes, 70000, 3);
            var result = decoded.ApplyTo(baseline);
            Assert.That(result.Revision, Is.EqualTo(15));
            Assert.That(result.SolidAt(1), Is.EqualTo(70000));
            Assert.That(result.FluidAt(1), Is.EqualTo(3));
            Assert.That(result.SolidAt(ChunkLayout.Volume - 1), Is.EqualTo(4));
            Assert.That(baseline.SolidAt(1), Is.Zero);
            Assert.Throws<InvalidOperationException>(() => decoded.ApplyTo(result));
            Assert.Throws<InvalidOperationException>(() => decoded.ApplyTo(new ChunkImage(Address, 9, 10,
                baseline.CopySolids(), baseline.CopyFluids())));
            Assert.Throws<InvalidOperationException>(() => decoded.ApplyTo(new ChunkImage(
                new ChunkAddress(4, Address.Position), 8, 10, baseline.CopySolids(), baseline.CopyFluids())));
        }

        [Test]
        public void DeltaRejectsDuplicateCellsInvalidIndicesAndNonadvancingRevision()
        {
            Assert.Throws<ArgumentException>(() => new ChunkDelta(Address, 1, 1, 2,
                new[] { new ChunkCellUpdate(3, 0, 0), new ChunkCellUpdate(3, 1, 0) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkDelta(Address, 1, 1, 2,
                new[] { new ChunkCellUpdate(-1, 0, 0) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkDelta(Address, 1, 1, 2,
                new[] { new ChunkCellUpdate(ChunkLayout.Volume, 0, 0) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkDelta(Address, 1, 2, 2,
                new[] { new ChunkCellUpdate(0, 0, 0) }));
        }

        [Test]
        public void PaletteRejectsOutOfRangeIndicesDuplicateStatesAndExcessiveCounts()
        {
            var solids = new uint[ChunkLayout.Volume];
            for (int i = 0; i < solids.Length; i++) solids[i] = (uint)(i % 3);
            byte[] valid = ChunkWireCodec.EncodeSnapshot(new ChunkImage(Address, 1, 1,
                solids, new uint[ChunkLayout.Volume]));
            var invalidIndex = (byte[])valid.Clone();
            invalidIndex[60] |= 3; //three-entry palette uses two bits; index three is invalid.
            SealChecksum(invalidIndex);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(invalidIndex, 2, 0));
            var duplicateState = (byte[])valid.Clone();
            Array.Copy(duplicateState, 48, duplicateState, 52, 4);
            SealChecksum(duplicateState);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(duplicateState, 2, 0));
            var excessiveCount = (byte[])valid.Clone();
            for (int i = 44; i < 48; i++) excessiveCount[i] = 255;
            SealChecksum(excessiveCount);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(excessiveCount, 2, 0));
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(valid, 1, 0));
        }

        [Test]
        public void DeltaDecoderRejectsDuplicateCellsBadIndicesAndInvalidRevision()
        {
            byte[] valid = ChunkWireCodec.EncodeDelta(new ChunkDelta(Address, 1, 2, 3,
                new[] { new ChunkCellUpdate(1, 0, 0), new ChunkCellUpdate(2, 0, 0) }));
            byte[] duplicates = (byte[])valid.Clone();
            Array.Copy(duplicates, 54, duplicates, 66, 4);
            SealChecksum(duplicates);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(duplicates, 0, 0));
            byte[] badIndex = (byte[])valid.Clone();
            for (int i = 54; i < 58; i++) badIndex[i] = 255;
            SealChecksum(badIndex);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(badIndex, 0, 0));
            byte[] revision = (byte[])valid.Clone();
            revision[42] = 2;
            SealChecksum(revision);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(revision, 0, 0));
        }

        [Test]
        public void DecoderRejectsOversizedPayloadsBeforeAllocatingChannels()
        {
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(
                new byte[ChunkWireCodec.MaxSnapshotBytes + 1], uint.MaxValue, uint.MaxValue));
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(
                new byte[ChunkWireCodec.MaxDeltaBytes + 1], uint.MaxValue, uint.MaxValue));
        }

        [Test]
        public void ImageRejectsWrongChannelLengthsAndZeroIncarnation()
        {
            Assert.Throws<ArgumentException>(() => new ChunkImage(Address, 1, 1, new uint[1], new uint[ChunkLayout.Volume]));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkImage(Address, 0, 1,
                new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]));
        }

        [Test]
        public void ZeroRevisionsAreRejectedConsistentlyBeforePublication()
        {
            var cells = new uint[ChunkLayout.Volume];
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkImage(default, 1, 0, cells, cells));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkDelta(default, 1, 0, 1,
                new[] { new ChunkCellUpdate(0, 1, 0) }));
            byte[] snapshot = ChunkWireCodec.EncodeSnapshot(new ChunkImage(default, 1, 1, cells, cells));
            Array.Clear(snapshot, 34, 8); SealChecksum(snapshot);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeSnapshot(snapshot, 1, 1));
            byte[] delta = ChunkWireCodec.EncodeDelta(new ChunkDelta(default, 1, 1, 2,
                new[] { new ChunkCellUpdate(0, 1, 0) }));
            Array.Clear(delta, 34, 8); SealChecksum(delta);
            Assert.Throws<FormatException>(() => ChunkWireCodec.DecodeDelta(delta, 1, 1));
        }

        //deliberately reseal malformed fixtures so tests reach structural validation, not only checksum rejection.
        private static void SealChecksum(byte[] bytes)
        {
            uint crc = uint.MaxValue;
            for (int i = 0; i < bytes.Length - 4; i++)
            {
                crc ^= bytes[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            crc = ~crc;
            for (int i = 0; i < 4; i++) bytes[bytes.Length - 4 + i] = (byte)(crc >> (8 * i));
        }
    }
}
