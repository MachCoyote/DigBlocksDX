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
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 1);
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

        [Test]
        public void ReplacementSharesOverlapAndCapacityFailurePreservesExistingLeases()
        {
            using var world = new World("Subscription capacity");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 2);
            var a = new ChunkAddress(1, default); var b = new ChunkAddress(1, new int3(1)); var c = new ChunkAddress(1, new int3(2));
            Assert.That(store.TryReplaceLeases(Array.Empty<ChunkLease>(), new[] { a, b }, out var first), Is.True);
            using var shared = store.Acquire(a);
            ulong incarnation = first[0].Incarnation;
            Assert.That(store.TryReplaceLeases(first, new[] { b, c }, out var rejected), Is.False);
            Assert.That(rejected, Is.Null); Assert.That(first[0].Revision, Is.EqualTo(1));
            shared.Dispose();
            Assert.That(store.TryReplaceLeases(first, new[] { b, c }, out var moved), Is.True);
            Assert.That(moved[0], Is.SameAs(first[1])); Assert.That(store.Count, Is.EqualTo(2));
            Assert.Throws<ObjectDisposedException>(() => first[0].SolidAt(0));
            Assert.That(store.TryReplaceLeases(moved, new[] { a }, out var returned), Is.True);
            Assert.That(returned[0].Incarnation, Is.GreaterThan(incarnation));
            returned[0].Dispose(); Assert.That(store.Count, Is.Zero);
        }

        [Test]
        public void HistoryCoalescesExactBaselinesAndFallsBackAfterRetentionOrUnload()
        {
            using var world = new World("Delta history");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            using var lease = store.Acquire(default);
            lease.Apply(new[] { new CellEdit(0, 1, 0) });
            lease.Apply(new[] { new CellEdit(0, 0, 1), new CellEdit(1, 1, 0) });
            Assert.That(store.TryGetDelta(lease, 1, out var delta), Is.True);
            Assert.That(delta.BaseRevision, Is.EqualTo(1)); Assert.That(delta.ResultRevision, Is.EqualTo(3));
            Assert.That(delta.Updates.Count, Is.EqualTo(2)); Assert.That(delta.Updates[0].Fluid, Is.EqualTo(1));
            for (int i = 0; i < 9; i++) lease.Apply(new[] { new CellEdit(2, (uint)(i % 2), 0) });
            Assert.That(store.TryGetDelta(lease, 1, out _), Is.False);
            Assert.That(store.TryGetDelta(lease, lease.Revision - 1, out _), Is.True);
            lease.Dispose();
            using var replacement = store.Acquire(default);
            Assert.That(store.TryGetDelta(replacement, 1, out _), Is.False);
        }

        [Test]
        public void AuthoritativeSourceLoadsOncePerIncarnationAndExplicitContentWins()
        {
            using var world = new World("Chunk source seam");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            var source = new CountingSource();
            var address = new ChunkAddress(1, default);
            using (var lease = store.Acquire(address))
            {
                store.EnsureLoaded(lease, source);
                store.EnsureLoaded(lease, source);
                Assert.That(source.Calls, Is.EqualTo(1));
                Assert.That(lease.SolidAt(0), Is.EqualTo(1));
            }
            using (var reentered = store.Acquire(address))
            {
                store.EnsureLoaded(reentered, source);
                Assert.That(source.Calls, Is.EqualTo(2));
            }
            using var authored = store.Acquire(new ChunkAddress(1, new int3(1, 0, 0)));
            authored.Apply(new[] { new CellEdit(0, 1, 0) });
            store.EnsureLoaded(authored, source);
            Assert.That(source.Calls, Is.EqualTo(2));
        }

        //Encoding is synchronous now, so a snapshot is the chunk as it stands rather than as it stood
        //when some worker was asked for it. What still has to hold is that it round-trips exactly and
        //that its revision tracks edits.
        [Test]
        public void SnapshotEncodesTheChunkAsItStandsAndTracksEdits()
        {
            using var world = new World("Snapshot test");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 2);
            var lease = store.Acquire(default);
            var before = ChunkWireCodec.DecodeSnapshot(store.EncodeSnapshot(lease), 1, 1);
            Assert.That(before.Revision, Is.EqualTo(1)); Assert.That(before.SolidAt(0), Is.Zero);

            lease.Apply(new[] { new CellEdit(0, 1, 0) });
            var after = ChunkWireCodec.DecodeSnapshot(store.EncodeSnapshot(lease), 1, 1);
            Assert.That(after.Revision, Is.EqualTo(2)); Assert.That(after.SolidAt(0), Is.EqualTo(1));
            for (int i = 1; i < ChunkLayout.Volume; i += 997) Assert.That(after.SolidAt(i), Is.Zero);

            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => store.EncodeSnapshot(lease));
        }

        [Test]
        public void DestroyingTheWorldReleasesTheStore()
        {
            var world = new World("Store teardown");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 1);
            store.Acquire(default);
            world.Dispose();
            Assert.That(store.IsDisposed, Is.True);
        }

        private sealed class CountingSource : IAuthoritativeChunkSource
        {
            public int Calls;
            public CellEdit[] LoadOrGenerate(ChunkAddress address)
            {
                Calls++;
                return new[] { new CellEdit(0, 1, 0) };
            }
        }
    }
}
