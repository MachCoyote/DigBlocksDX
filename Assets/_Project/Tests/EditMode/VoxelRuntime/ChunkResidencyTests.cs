using System;
using System.Collections;
using DigBlocks.ChunkProtocol;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Voxels.Runtime.Tests
{
    public sealed class ChunkResidencyTests
    {
        [Test]
        public void LeasesShareAnEntityAndFinalReleaseUnloadsWithNewIncarnationOnReentry()
        {
            using var world = new World("Residency test");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 1, 2);
            var address = new ChunkAddress(1, new int3(-2, 3, 7));
            using var first = store.Acquire(address);
            using var second = store.Acquire(address);
            Assert.That(first.Entity, Is.EqualTo(second.Entity));
            Assert.That(store.Count, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => store.Acquire(new ChunkAddress(2, address.Position)));
            var oldEntity = first.Entity; ulong incarnation = first.Incarnation;
            first.Dispose();
            Assert.That(world.EntityManager.Exists(oldEntity), Is.True);
            Assert.Throws<ObjectDisposedException>(() => first.SolidAt(0));
            second.Dispose();
            Assert.That(world.EntityManager.Exists(oldEntity), Is.False); Assert.That(store.Count, Is.Zero);
            using var replacement = store.Acquire(address);
            Assert.That(replacement.Incarnation, Is.GreaterThan(incarnation));
        }

        [Test]
        public void StorePublishesOneCoherentEntityRevisionAndEnforcesRegistryRules()
        {
            using var world = new World("Mutation test");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            using var lease = store.Acquire(default);
            lease.Apply(new[] { new CellEdit(1, 1, 0), new CellEdit(2, 0, 1) });
            var identity = world.EntityManager.GetComponentData<ResidentChunk>(lease.Entity);
            Assert.That(identity.Revision, Is.EqualTo(2)); Assert.That(identity.Incarnation, Is.EqualTo(lease.Incarnation));
            Assert.Throws<ArgumentException>(() => lease.Apply(new[] { new CellEdit(1, 0, 0), new CellEdit(2, 1, 1) }));
            Assert.That(lease.SolidAt(1), Is.EqualTo(1)); Assert.That(lease.Revision, Is.EqualTo(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => lease.Apply(new[] { new CellEdit(1, 99, 0) }));
        }

        [Test]
        public void WorldsDoNotShareResidencyAndDestructionInvalidatesLeases()
        {
            var first = new World("First");
            using var second = new World("Second");
            var a = first.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            var b = second.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            using var lease = a.Acquire(default);
            using var other = b.Acquire(default);
            lease.Apply(new[] { new CellEdit(0, 1, 0) });
            Assert.That(other.SolidAt(0), Is.Zero);
            first.Dispose(); lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => a.Acquire(default));
            Assert.That(other.SolidAt(0), Is.Zero);
        }

        [UnityTest]
        public IEnumerator SnapshotSurvivesEditsButNotUnloadAndCapacityIncludesCompletedWork()
        {
            using var world = new World("Snapshot test");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 2, 1);
            using var lease = store.Acquire(default);
            Assert.That(store.TryRequestSnapshot(lease, out ulong id), Is.True);
            Assert.That(store.TryRequestSnapshot(lease, out _), Is.False);
            lease.Apply(new[] { new CellEdit(0, 1, 0) });
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (store.ReadySnapshots == 0 && Time.realtimeSinceStartupAsDouble < deadline) { store.PumpSnapshots(); yield return null; }
            Assert.That(store.ReadySnapshots, Is.EqualTo(1));
            Assert.That(store.TryRequestSnapshot(lease, out _), Is.False);
            Assert.That(store.TryTakeSnapshot(out var result), Is.True);
            Assert.That(result.RequestId, Is.EqualTo(id)); Assert.That(result.Error, Is.Null);
            var image = ChunkWireCodec.DecodeSnapshot(result.Payload, 1, 1);
            Assert.That(image.Revision, Is.EqualTo(1)); Assert.That(image.SolidAt(0), Is.Zero);
            Assert.That(store.TryRequestSnapshot(lease, out _), Is.True);
            lease.Dispose();
            using var replacement = store.Acquire(default);
            deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (store.PendingSnapshots != 0 && Time.realtimeSinceStartupAsDouble < deadline) { store.PumpSnapshots(); yield return null; }
            Assert.That(store.PendingSnapshots, Is.Zero); Assert.That(store.TryTakeSnapshot(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator CancellationAndWorldDestructionDrainSnapshotWorkers()
        {
            var world = new World("Cancellation test");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 1, 1);
            using var lease = store.Acquire(default);
            store.TryRequestSnapshot(lease, out ulong id); store.CancelSnapshot(id);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (store.PendingSnapshots != 0 && Time.realtimeSinceStartupAsDouble < deadline) { store.PumpSnapshots(); yield return null; }
            Assert.That(store.PendingSnapshots, Is.Zero); Assert.That(store.TryTakeSnapshot(out _), Is.False);
            store.TryRequestSnapshot(lease, out _);
            for (int i = 0; i < 4; i++) { store.PumpSnapshots(); yield return null; }
            world.Dispose();
            Assert.That(store.PendingSnapshots, Is.Zero);
        }
    }
}
