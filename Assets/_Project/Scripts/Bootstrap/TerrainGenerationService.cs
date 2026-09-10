using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Generation;
using DigBlocks.Voxels.Runtime;

namespace DigBlocks.Bootstrap
{
    /// <summary>
    /// Binds a built world generator to the authoritative chunk source the streaming server pulls
    /// from.
    /// <para>
    /// It is a service rather than a bare object because the generator owns native memory for its
    /// compiled noise programs and recipes, so its lifetime has to end with the session's.
    /// </para>
    /// </summary>
    public sealed class GeneratedTerrainChunkSource : IAuthoritativeChunkSource, IGameService, IDisposable
    {
        private readonly uint worldId;
        private readonly TerrainGenerator generator;
        private bool disposed;

        public string Name => "Terrain generation";
        public string GeneratorId => generator.Id;
        public GenSeed Seed => generator.Seed;

        public GeneratedTerrainChunkSource(TerrainGeneratorDefinition definition, GenSeed seed,
            GeneratorSettings settings, BlockRegistry registry, uint worldId)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            this.worldId = worldId;
            generator = definition.Build(new GeneratorBuildContext(seed,
                settings ?? GeneratorSettings.Defaults(definition.Schema), new RegistryBlockResolver(registry)));
        }

        //runs on a worker thread; the buffers arrive cleared to air, so a chunk this world does not
        //own simply stays empty.
        public void Generate(ChunkAddress address, uint[] solids, uint[] fluids)
        {
            if (disposed || address.World != worldId) return;
            generator.Generate(address.Position.x, address.Position.y, address.Position.z, solids, fluids);
        }

        public UniTask StartAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return UniTask.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            generator.Dispose();
        }
    }
}
