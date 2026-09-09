using System;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Tests
{
    public class ChunkDataTests
    {
        [Test]
        public void ReusableSolidCopySurvivesMutationAndOwnerDisposal()
        {
            using var before = new Unity.Collections.NativeArray<uint>(ChunkLayout.Volume, Unity.Collections.Allocator.Persistent);
            using var after = new Unity.Collections.NativeArray<uint>(ChunkLayout.Volume, Unity.Collections.Allocator.Persistent);
            using var chunk = new ChunkData(default, 1, 2, 0);
            var first = chunk.ScheduleSolidCopy(before);
            chunk.Apply(new[] { new CellEdit(7, 3, 0) }, 3, 0);
            var second = chunk.ScheduleSolidCopy(after);
            chunk.Dispose();
            first.Complete(); second.Complete();
            Assert.That(before[7], Is.EqualTo(2));
            Assert.That(after[7], Is.EqualTo(3));
            Assert.That(after[8], Is.EqualTo(2));
        }

        [Test]
        public void BatchChangesBothChannelsAtOneRevision()
        {
            using var chunk = new ChunkData(new ChunkAddress(1, new int3(-1, 2, 3)), 7);
            chunk.Apply(new[] { new CellEdit(2, 1, 3), new CellEdit(3, 2, 0) }, 2, 3);
            Assert.That(chunk.SolidAt(2), Is.EqualTo(1u));
            Assert.That(chunk.FluidAt(2), Is.EqualTo(3u));
            Assert.That(chunk.SolidAt(3), Is.EqualTo(2u));
            Assert.That(chunk.Revision, Is.EqualTo(2ul));
        }

        [Test]
        public void InvalidBatchCannotPartiallyMutate()
        {
            using var chunk = new ChunkData(default, 1);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                chunk.Apply(new[] { new CellEdit(0, 1, 0), new CellEdit(1, 99, 0) }, 2, 1));
            Assert.That(chunk.SolidAt(0), Is.Zero);
            Assert.That(chunk.Revision, Is.EqualTo(1ul));
        }

        [Test]
        public void ProvenanceIsIndependentOfPublicRevisionAndClearsOnReplacement()
        {
            using var chunk = new ChunkData(default, 1);
            chunk.SetPlayerPlaced(2, true);
            Assert.That(chunk.IsPlayerPlaced(2), Is.True);
            Assert.That(chunk.Revision, Is.EqualTo(1ul));
            chunk.Apply(new[] { new CellEdit(2, 1, 0) }, 2, 1);
            Assert.That(chunk.IsPlayerPlaced(2), Is.False);
        }

        [Test]
        public void CapturesKeepTheirRevisionAcrossMutationAndOwnerDisposal()
        {
            var address = new ChunkAddress(9, new int3(-3, 1, 8));
            using var chunk = new ChunkData(address, 4, 2, 1);
            using var before = chunk.Capture();
            chunk.Apply(new[] { new CellEdit(0, 3, 0) }, 3, 1);
            using var after = chunk.Capture();
            chunk.Dispose();
            Assert.That(before.Address, Is.EqualTo(address));
            Assert.That(before.Incarnation, Is.EqualTo(4));
            Assert.That(before.Revision, Is.EqualTo(1));
            Assert.That(after.Revision, Is.EqualTo(2));
            Assert.That(before.CopySolids(), Is.All.EqualTo(2));
            Assert.That(before.CopyFluids(), Is.All.EqualTo(1));
            var solids = after.CopySolids(); var fluids = after.CopyFluids();
            Assert.That(solids[0], Is.EqualTo(3)); Assert.That(fluids[0], Is.Zero);
            Assert.That(solids[1], Is.EqualTo(2)); Assert.That(fluids[1], Is.EqualTo(1));
            solids[0] = 99;
            Assert.That(after.CopySolids()[0], Is.EqualTo(3));
            Assert.Throws<ObjectDisposedException>(() => chunk.Capture());
        }

        [Test]
        public void DisposingCaptureCompletesItsJobAndRejectsFurtherReads()
        {
            using var chunk = new ChunkData(default, 1);
            var capture = chunk.Capture();
            capture.Dispose(); capture.Dispose();
            Assert.Throws<ObjectDisposedException>(() => capture.CopySolids());
            Assert.Throws<ObjectDisposedException>(() => capture.CopyFluids());
            chunk.Apply(new[] { new CellEdit(1, 1, 0) }, 1, 0);
            Assert.That(chunk.SolidAt(1), Is.EqualTo(1));
        }

        [Test]
        public void ReplicaImportDetachesChannelsAndAppliesOnlyValidMatchingBatches()
        {
            var solids = new uint[ChunkLayout.Volume]; var fluids = new uint[ChunkLayout.Volume];
            solids[0] = 2; fluids[1] = 1;
            using var chunk = ChunkData.FromChannels(default, 7, 15, solids, fluids);
            solids[0] = 99; fluids[1] = 99;
            Assert.That(chunk.SolidAt(0), Is.EqualTo(2)); Assert.That(chunk.FluidAt(1), Is.EqualTo(1));
            Assert.That(chunk.Revision, Is.EqualTo(15));
            var edits = new[] { new CellEdit(0, 1, 1) };
            Assert.Throws<InvalidOperationException>(() => chunk.ApplyReplica(edits, 14, 16, 2, 1));
            Assert.Throws<InvalidOperationException>(() => chunk.ApplyReplica(edits, 15, 15, 2, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk.ApplyReplica(
                new[] { new CellEdit(0, 1, 0), new CellEdit(1, 3, 0) }, 15, 18, 2, 1));
            Assert.That(chunk.Revision, Is.EqualTo(15)); Assert.That(chunk.SolidAt(0), Is.EqualTo(2));
            chunk.ApplyReplica(edits, 15, 18, 2, 1);
            Assert.That(chunk.Revision, Is.EqualTo(18));
            Assert.That(chunk.SolidAt(0), Is.EqualTo(1)); Assert.That(chunk.FluidAt(0), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => chunk.ApplyReplica(edits, 15, 18, 2, 1));
        }

        [Test]
        public void ImportRejectsInvalidImagesAndPrivateRecordsFollowSolidReplacement()
        {
            var cells = new uint[ChunkLayout.Volume];
            Assert.Throws<ArgumentException>(() => ChunkData.FromChannels(default, 1, 0, cells, cells));
            Assert.Throws<ArgumentException>(() => ChunkData.FromChannels(default, 1, 1, null, cells));
            Assert.Throws<ArgumentException>(() => ChunkData.FromChannels(default, 1, 1, cells, new uint[1]));
            using var chunk = new ChunkData(default, 1);
            byte[] bytes = { 1, 2 };
            var record = new BlockRecord("digblocks:container", 1, bytes);
            bytes[0] = 9;
            chunk.SetRecord(2, record); chunk.SetPlayerPlaced(2, true);
            chunk.Apply(new[] { new CellEdit(2, 0, 1) }, 1, 1);
            Assert.That(chunk.GetRecord(2).CopyPayload(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(chunk.IsPlayerPlaced(2), Is.True);
            chunk.Apply(new[] { new CellEdit(2, 1, 1) }, 1, 1);
            Assert.That(chunk.GetRecord(2), Is.Null); Assert.That(chunk.IsPlayerPlaced(2), Is.False);
            chunk.Dispose();
            Assert.Throws<ObjectDisposedException>(() => chunk.GetRecord(2));
            Assert.Throws<ObjectDisposedException>(() => chunk.SolidAt(0));
        }

        [Test]
        public void DuplicateAndNoOpBatchesPreservePublicRevisionAndContents()
        {
            using var chunk = new ChunkData(default, 1);
            chunk.Apply(Array.Empty<CellEdit>(), 1, 1);
            chunk.Apply(new[] { new CellEdit(0, 0, 0) }, 1, 1);
            Assert.Throws<ArgumentException>(() => chunk.Apply(new[] { new CellEdit(0, 1, 0), new CellEdit(0, 0, 1) }, 1, 1));
            Assert.That(chunk.Revision, Is.EqualTo(1)); Assert.That(chunk.SolidAt(0), Is.Zero);
            Assert.That(chunk.FluidAt(0), Is.Zero);
        }
    }
}
