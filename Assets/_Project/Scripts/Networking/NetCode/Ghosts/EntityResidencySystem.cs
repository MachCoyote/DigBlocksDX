using System;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Simulation;
using DigBlocks.Voxels;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Loads and unloads entities with the chunks they live in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Server chunk residency is the union of what the peers are interested in, not a copy per
    /// player, and entity residency is derived from exactly that same union. Deriving it rather than
    /// keeping a second opinion is what stops entities and chunks from disagreeing about what is
    /// loaded.
    /// </para>
    /// <para>
    /// Rebuilding the union costs one pass over each peer's interest, so it is done only when some
    /// peer's interest actually changed, which happens when a player crosses a chunk boundary. The
    /// interest offsets are already cached and radially sorted by <see cref="ChunkInterest"/>.
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ChunkResidencySystem))]
    public partial class ServerEntityResidencySystem : SystemBase
    {
        private readonly HashSet<ChunkAddress> resident = new HashSet<ChunkAddress>();
        private readonly List<ChunkStreamingServer.PeerInterest> interests = new List<ChunkStreamingServer.PeerInterest>();
        private readonly List<StoredEntity> departing = new List<StoredEntity>();
        private readonly Dictionary<ChunkAddress, List<StoredEntity>> byChunk = new Dictionary<ChunkAddress, List<StoredEntity>>();

        private IEntityChunkStore store;
        private Action<List<ChunkStreamingServer.PeerInterest>> readInterests;
        private EntityGhostPrefabSystem prefabs;
        private EntitySimulationDistance distance;
        private EntityStateCodecs codecs;
        private ulong signature;
        private bool configured;

        public int ResidentChunkCount => resident.Count;
        public IEntityChunkStore Store => store;

        protected override void OnCreate() => Enabled = false;

        internal void Configure(IEntityChunkStore entityStore, Action<List<ChunkStreamingServer.PeerInterest>> interestReader,
            EntitySimulationDistance simulationDistance, EntityStateCodecs stateCodecs = null)
        {
            store = entityStore ?? throw new ArgumentNullException(nameof(entityStore));
            readInterests = interestReader ?? throw new ArgumentNullException(nameof(interestReader));
            distance = simulationDistance;
            codecs = stateCodecs ?? EntityStateCodecs.Default;
            configured = true;
            Enabled = true;
        }

        protected override void OnUpdate()
        {
            if (!configured) return;
            prefabs ??= World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            if (prefabs is not { Built: true }) return;

            readInterests(interests);

            //a cheap signature over peer and epoch: interest only changes by advancing an epoch, so
            //this catches every change without comparing thousands of addresses. No peers hashes to
            //zero, which is the right answer rather than a missing one: with nobody interested in
            //anything, nothing should be alive, and losing the last peer is a change like any other.
            //A server that treated it as "no opinion yet" would tick every mob in the world forever
            //with nobody connected to see any of it.
            ulong next = 0;
            foreach (var peer in interests) next = unchecked(next * 31 + peer.PeerId * 1000003 + peer.Interest.Epoch);
            if (next != signature)
            {
                signature = next;
                //rebuilding enumerates every simulated chunk of every peer, so it is worth gating on
                //the interest actually having changed. Taking entities back out of the store is only
                //meaningful when the resident set grew, which is the same moment.
                Rebuild();
                LoadArrived();
            }

            //unloading, by contrast, has to run every tick: an entity can leave the simulated set by
            //moving or by being spawned outside it, neither of which touches any peer's interest.
            UnloadDeparted();
        }

        private void Rebuild()
        {
            resident.Clear();
            foreach (var peer in interests)
            {
                //entities live inside simulation distance, which is normally shorter than the distance
                //chunks are streamed at. A player can see much further than the world should be alive.
                var simulated = peer.Interest.Narrowed(distance.Horizontal, distance.Vertical);
                foreach (var address in simulated.Addresses()) resident.Add(address);
            }
        }

        private void UnloadDeparted()
        {
            byChunk.Clear();
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (residency, type, position, entity) in
                SystemAPI.Query<RefRO<ChunkResidency>, RefRO<EntityTypeId>, RefRO<WorldPosition>>().WithEntityAccess())
            {
                var address = residency.ValueRO.Address;
                if (resident.Contains(address)) continue;
                commands.DestroyEntity(entity);

                //a type that does not persist is not saved with its chunk, so unloading is the end of
                //it rather than a round trip. That is what stops a world filling up with every
                //wandering mob and dropped item it has ever produced.
                if (!prefabs.Registry[type.ValueRO.Value].Attributes.Has(EntityFlags.Persists)) continue;

                if (!byChunk.TryGetValue(address, out var list)) byChunk.Add(address, list = new List<StoredEntity>());
                list.Add(new StoredEntity(type.ValueRO.Value, position.ValueRO, codecs.Capture(EntityManager, entity)));
            }
            commands.Playback(EntityManager);

            //handing the records over and destroying the entities is one transfer of ownership, so
            //nothing is ever live and stored at the same time.
            foreach (var pair in byChunk) store.Store(pair.Key, pair.Value);
        }

        private void LoadArrived()
        {
            foreach (var address in resident)
            {
                if (!store.TryTake(address, out var stored)) continue;
                for (int i = 0; i < stored.Count; i++) Respawn(stored[i]);
            }
        }

        private void Respawn(StoredEntity stored)
        {
            Entity entity = EntitySpawn.Spawn(EntityManager, prefabs, stored.TypeId, stored.Position);
            if (entity == Entity.Null) return;
            //restore behaviour state rather than restarting it, so a reloaded mob resumes its circle
            //where it left off instead of snapping to a fresh phase.
            codecs.Restore(EntityManager, entity, stored.State);
        }

        /// <summary>Forces the next update to rebuild, for tests that move interest directly.</summary>
        internal void InvalidateSignature() => signature = 0;
    }
}
