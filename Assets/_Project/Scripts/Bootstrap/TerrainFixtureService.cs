using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Session;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using Unity.Entities;
using Unity.Mathematics;

namespace DigBlocks.Bootstrap
{
    //bounded visualization content, owned by the authoritative world and sent through ordinary replication.
    //this is intentionally a development fixture, not the world-generation subsystem.
    internal sealed class TerrainFixtureService : IGameService, ISessionReadinessSource
    {
        private readonly Func<World> getWorld;
        private readonly BlockRegistry registry;
        private readonly Func<World> getClientWorld;
        private readonly List<ChunkLease> leases = new();
        public string Name => nameof(TerrainFixtureService);
        public string ReadinessDescription => "Streaming terrain fixture...";
        public TerrainFixtureService(Func<World> getWorld, BlockRegistry registry, Func<World> getClientWorld)
        { this.getWorld = getWorld; this.registry = registry; this.getClientWorld = getClientWorld; }
        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var store = getWorld().GetExistingSystemManaged<ChunkWorldSystem>().Store;
                uint bedrock = registry.LookupSolid("digblocks:bedrock"), stone = registry.LookupSolid("digblocks:stone"),
                    dirt = registry.LookupSolid("digblocks:dirt"), grass = registry.LookupSolid("digblocks:grass_block"), test = registry.LookupSolid("digblocks:testblock");
                for (int cz = -1; cz <= 1; cz++) for (int cx = -1; cx <= 1; cx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lease = store.Acquire(new ChunkAddress(1, new int3(cx, 0, cz))); leases.Add(lease);
                    //do not replace preexisting authoritative content.
                    if (lease.Revision != 1) continue;
                    var edits = new List<CellEdit>();
                    for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++)
                    {
                        int wx = cx * 32 + x, wz = cz * 32 + z;
                        int height = 8 + ((wx + 32) / 16 % 3) * 2 + ((wz + 32) / 32 % 2) * 2;
                        bool wall = wx >= 8 && wx < 28 && wz >= 8 && wz < 12;
                        if (wall) height = 22;
                        bool marker = wx >= -12 && wx < -6 && wz >= -12 && wz < -6;
                        for (int y = 0; y < height; y++)
                        {
                            uint block = y == 0 ? bedrock : wall ? stone : y == height - 1 ? (marker ? test : grass) : y >= height - 3 ? dirt : stone;
                            edits.Add(new CellEdit(ChunkLayout.Index(new int3(x, y, z)), block, 0));
                        }
                    }
                    lease.Apply(edits.ToArray());
                }
                return UniTask.CompletedTask;
            }
            catch
            {
                foreach (var lease in leases) lease.Dispose();
                leases.Clear();
                throw;
            }
        }
        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            foreach (var lease in leases) lease.Dispose(); leases.Clear();
            return UniTask.CompletedTask;
        }
        public async UniTask WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            if (getClientWorld == null) return;
            double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + 60;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replica = getClientWorld()?.GetExistingSystemManaged<ChunkWorldSystem>()?.Store;
                bool ready = replica != null && !replica.IsDisposed;
                if (ready)
                    foreach (var lease in leases)
                        if (!replica.TryGetReplicaStamp(lease.Address, out var stamp) ||
                            stamp.Incarnation != lease.Incarnation || stamp.Revision < lease.Revision) { ready = false; break; }
                if (ready) return;
                if (UnityEngine.Time.realtimeSinceStartupAsDouble > deadline) throw new TimeoutException("Terrain fixture replication did not complete.");
                await UniTask.Yield(cancellationToken);
            }
        }
    }
}
