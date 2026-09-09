using System;
using DigBlocks.ChunkProtocol;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Runtime.Tests
{
    public sealed class ChunkReplicaTests
    {
        [Test]
        public void PublicationValidatesAtomicallyAndReadinessRequiresTheWholeLiveInterest()
        {
            using var world = new World("Replica publication");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            store.EnableReplicas();
            var interest = new ChunkInterest(1, new ChunkAddress(1, default), 0, 1);
            Assert.That(store.SetReplicaInterest(interest), Is.True);
            Assert.That(store.DataReady, Is.False);
            foreach (var address in interest.Addresses())
                Assert.That(store.PublishReplica(1, Image(address, 1)), Is.True);
            Assert.That(store.DataReady, Is.True);
            var solids = new uint[ChunkLayout.Volume]; var fluids = new uint[ChunkLayout.Volume];
            solids[0] = 1; fluids[0] = 1;
            Assert.Throws<ArgumentException>(() => store.PublishReplica(1, new ChunkImage(interest.Anchor, 1, 2, solids, fluids)));
            Assert.That(store.TryReadReplica(interest.Anchor, out var previous), Is.True);
            Assert.That(previous.Revision, Is.EqualTo(1)); Assert.That(previous.SolidAt(0), Is.Zero);
            fluids[0] = 0;
            Assert.That(store.PublishReplica(1, new ChunkImage(interest.Anchor, 1, 2, solids, fluids)), Is.True);
            Assert.That(store.TryReadReplica(interest.Anchor, out var updated), Is.True);
            Assert.That(updated.Revision, Is.EqualTo(2)); Assert.That(updated.SolidAt(0), Is.EqualTo(1));
            Assert.That(store.PublishReplica(1, Image(interest.Anchor, 1)), Is.False);
            Assert.Throws<ArgumentException>(() => store.PublishReplica(1, Image(interest.Anchor, 2)));
            Assert.Throws<InvalidOperationException>(() => store.Acquire(interest.Anchor));
        }

        [Test]
        public void InterestReplacementEvictsEntitiesAndRejectsOldCompletionsIncludingReentry()
        {
            using var world = new World("Replica eviction");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            store.EnableReplicas();
            var address = new ChunkAddress(1, default);
            store.SetReplicaInterest(new ChunkInterest(1, address, 0, 0));
            store.PublishReplica(1, Image(address, 1));
            using var query = world.EntityManager.CreateEntityQuery(typeof(ResidentChunk));
            var old = query.GetSingletonEntity();
            store.SetReplicaInterest(new ChunkInterest(2, new ChunkAddress(1, new int3(3)), 0, 0));
            Assert.That(world.EntityManager.Exists(old), Is.False);
            Assert.That(store.Count, Is.Zero); Assert.That(store.DataReady, Is.False);
            Assert.That(store.PublishReplica(1, Image(address, 1)), Is.False);
            Assert.That(store.SetReplicaInterest(new ChunkInterest(1, address, 0, 0)), Is.False);
            store.SetReplicaInterest(new ChunkInterest(3, address, 0, 0));
            Assert.That(store.PublishReplica(1, Image(address, 1)), Is.False);
            Assert.That(store.PublishReplica(3, Image(address, 1)), Is.True);
            Assert.That(store.DataReady, Is.True);
        }

        [Test]
        public void InterestReplacementRetainsOverlappingReplicasAndEvictsOnlyDepartedChunks()
        {
            using var world = new World("Replica overlap");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            store.EnableReplicas();
            var first = new ChunkInterest(1, new ChunkAddress(1, default), 1, 0);
            store.SetReplicaInterest(first);
            foreach (var address in first.Addresses())
                store.PublishReplica(first.Epoch, Image(address, 1));

            var retainedAddress = new ChunkAddress(1, new int3(1, 0, 0));
            Assert.That(store.TryGetReplicaStamp(retainedAddress, out var retained), Is.True);
            using var query = world.EntityManager.CreateEntityQuery(typeof(ResidentChunk));
            var retainedEntity = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            Entity entity = Entity.Null;
            foreach (var candidate in retainedEntity)
                if (world.EntityManager.GetComponentData<ResidentChunk>(candidate).Address.Equals(retainedAddress)) entity = candidate;
            retainedEntity.Dispose();

            var second = new ChunkInterest(2, new ChunkAddress(1, new int3(1, 0, 0)), 1, 0);
            Assert.That(store.SetReplicaInterest(second), Is.True);

            Assert.That(store.Count, Is.EqualTo(6));
            Assert.That(store.TryGetReplicaStamp(retainedAddress, out var after), Is.True);
            Assert.That(after.Incarnation, Is.EqualTo(retained.Incarnation));
            Assert.That(world.EntityManager.Exists(entity), Is.True);
            Assert.That(store.TryGetReplicaStamp(new ChunkAddress(1, new int3(-1, 0, 0)), out _), Is.False);
            Assert.That(store.DataReady, Is.False);
        }

        //A neighbour only re-meshes when the plane facing it changes, so publication reports exactly
        //which boundary planes moved. An interior edit must report none.
        [Test]
        public void PublicationReportsOnlyTheBoundaryPlanesThatChanged()
        {
            using var world = new World("Replica face masks");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            store.EnableReplicas();
            var address = new ChunkAddress(1, default);
            Assert.That(store.SetReplicaInterest(new ChunkInterest(1, address, 0, 0)), Is.True);
            byte reported = 0xFF;
            store.ReplicaChanged += (_, faces) => reported = faces;

            //an all-air chunk matches the air padding an absent neighbour already assumed.
            Assert.That(store.PublishReplica(1, Image(address, 1)), Is.True);
            Assert.That(reported, Is.Zero, "An empty chunk cannot change any neighbour.");

            //an interior cell is not on any boundary plane.
            var solids = new uint[ChunkLayout.Volume];
            solids[ChunkLayout.Index(new int3(5, 5, 5))] = 1;
            Assert.That(store.PublishReplica(1, new ChunkImage(address, 1, 2, solids, new uint[ChunkLayout.Volume])), Is.True);
            Assert.That(reported, Is.Zero, "An interior edit cannot change any neighbour.");

            //y = 0 is the plane the chunk below reads, which is face Down.
            solids[ChunkLayout.Index(new int3(5, 0, 5))] = 1;
            Assert.That(store.PublishReplica(1, new ChunkImage(address, 1, 3, solids, new uint[ChunkLayout.Volume])), Is.True);
            Assert.That(reported, Is.EqualTo((byte)(1 << (int)Definitions.BlockFace.Down)));

            //x = 31 is the plane the chunk to the east reads.
            solids[ChunkLayout.Index(new int3(ChunkLayout.Edge - 1, 7, 7))] = 1;
            Assert.That(store.PublishReplica(1, new ChunkImage(address, 1, 4, solids, new uint[ChunkLayout.Volume])), Is.True);
            Assert.That(reported, Is.EqualTo((byte)(1 << (int)Definitions.BlockFace.East)));
        }

        private static ChunkImage Image(ChunkAddress address, ulong revision) =>
            new ChunkImage(address, 1, revision, new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);
    }
}
