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
    /// same way <see cref="ChunkCompanionService"/> hands the block registry to each chunk store.
    /// </summary>
    public sealed class EntityGhostService : IGameService
    {
        private readonly NetCodeSession session;
        private readonly EntityTypeRegistry registry;
        private readonly bool allowDebugSpawns;
        private bool started;

        public EntityGhostService(NetCodeSession session, EntityTypeRegistry registry, bool allowDebugSpawns = false)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.allowDebugSpawns = allowDebugSpawns;
        }

        public string Name => nameof(EntityGhostService);

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
            //only the server decides whether client-issued debug spawns are honoured.
            if (allowDebugSpawns && server is { IsCreated: true })
                server.EntityManager.CreateSingleton<DebugSpawnPermitted>();
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
