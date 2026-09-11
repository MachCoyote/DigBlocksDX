using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Simulation;
using Unity.Entities;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Hands the entity type registry to both worlds so each builds the same ghost prefabs, in the
    /// same way <see cref="ChunkCompanionService"/> hands the block registry to each chunk store, and
    /// gives the server what it needs to load entities with their chunks.
    /// </summary>
    public sealed class EntityGhostService : IGameService
    {
        private readonly NetCodeSession session;
        private readonly EntityTypeRegistry registry;
        private readonly ChunkCompanionService companion;
        private readonly IEntityChunkStore chunkStore;
        private readonly uint worldId;
        private readonly EntitySimulationDistance simulationDistance;
        private readonly bool allowDebugSpawns;
        private bool started;

        public EntityGhostService(NetCodeSession session, EntityTypeRegistry registry,
            ChunkCompanionService companion = null, bool allowDebugSpawns = false,
            IEntityChunkStore chunkStore = null, uint worldId = 1,
            EntitySimulationDistance? simulationDistance = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            //the companion is what knows which chunks each peer wants. Without one there is nothing
            //to derive entity residency from, so the server keeps whatever has been spawned.
            this.companion = companion;
            //in memory until chunk saving to disk exists; the round trip is real either way.
            this.chunkStore = chunkStore ?? new InMemoryEntityChunkStore();
            this.allowDebugSpawns = allowDebugSpawns;
            this.worldId = worldId == 0 ? 1 : worldId;
            //unbounded means "simulate whatever is streamed", since it is always clamped to the
            //peer's own distance. A caller with an opinion narrows it.
            this.simulationDistance = simulationDistance ?? EntitySimulationDistance.Unbounded;
        }

        public string Name => nameof(EntityGhostService);
        public IEntityChunkStore ChunkStore => chunkStore;

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started) throw new InvalidOperationException("Entity ghost services are one-shot.");
            started = true;
            //both worlds get the same registry instance, which is what makes the prefab collections
            //agree. A peer that compiled different content fails the fingerprint comparison first.
            World server = session.ServerWorld;
            Configure(server);
            Configure(session.ClientWorld);

            if (server is { IsCreated: true })
            {
                server.EntityManager.CreateSingleton(new SimulationWorldId { Value = worldId });
                //only the server decides whether client-issued debug spawns are honoured.
                if (allowDebugSpawns) server.EntityManager.CreateSingleton<DebugSpawnPermitted>();
                if (companion != null)
                {
                    server.GetOrCreateSystemManaged<ServerEntityResidencySystem>()
                        .Configure(chunkStore, companion.CopyPeerInterests, simulationDistance);
                    server.GetOrCreateSystemManaged<ServerGhostRelevancySystem>()
                        .Configure(companion.CopyPeerInterests, simulationDistance);
                }
            }
            return UniTask.CompletedTask;
        }

        public UniTask StopAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        private void Configure(World world)
        {
            if (world is not { IsCreated: true }) return;
            world.GetOrCreateSystemManaged<EntityGhostPrefabSystem>().Configure(registry);
        }
    }
}
