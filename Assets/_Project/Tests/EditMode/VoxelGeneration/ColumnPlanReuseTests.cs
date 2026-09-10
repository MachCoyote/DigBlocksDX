using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace DigBlocks.Voxels.Generation.Tests
{
    /// <summary>
    /// A column plan does not depend on the chunk's Y, so every chunk in a vertical stack could share
    /// one. These measure how often that actually happens, because the answer decides whether a shared
    /// cache would earn the locking it would need.
    /// </summary>
    public sealed class ColumnPlanReuseTests
    {
        private static readonly GenSeed Seed = new GenSeed(0xC0114B5EUL);
        private static readonly IBlockResolver Blocks = new StubBlockResolver();

        private readonly List<IDisposable> owned = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in owned) item.Dispose();
            owned.Clear();
        }

        private TerrainGenerator Build()
        {
            var layer = WorldLayer.Named("overworld")
                .Bounds(-64, 320)
                .Root(64)
                .Band("ground", BandDirection.Up,
                    64f + NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 64f, 4) * 20f,
                    ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone"))
                .Build();
            var generator = new InlineGenerator(new[] { layer }).Build(Seed, Blocks);
            owned.Add(generator);
            return generator;
        }

        private static void GenerateStack(TerrainGenerator generator, int chunkX, int chunkZ, int low, int high)
        {
            var solids = new uint[ChunkLayout.Volume];
            var fluids = new uint[ChunkLayout.Volume];
            for (int chunkY = low; chunkY <= high; chunkY++)
            {
                Array.Clear(solids, 0, solids.Length);
                Array.Clear(fluids, 0, fluids.Length);
                generator.Generate(chunkX, chunkY, chunkZ, solids, fluids);
            }
        }

        [Test]
        public void AVerticalStackComputesItsColumnsOnce()
        {
            var generator = Build();
            GenerateStack(generator, 3, -4, low: -2, high: 9);

            Assert.That(generator.PlansComputed, Is.EqualTo(1),
                "Twelve chunks of one column should need one plan between them.");
            Assert.That(generator.PlansReused, Is.EqualTo(11));
        }

        [Test]
        public void MovingToANewColumnComputesAgain()
        {
            var generator = Build();
            GenerateStack(generator, 0, 0, 0, 3);
            GenerateStack(generator, 1, 0, 0, 3);
            Assert.That(generator.PlansComputed, Is.EqualTo(2));
            Assert.That(generator.PlansReused, Is.EqualTo(6));
        }

        /// <summary>
        /// The honest limit of keeping the plan on the worker rather than sharing it: several workers
        /// walking the same column each keep their own. This records how much is still left on the
        /// table, so a shared cache can be judged against a number rather than a hunch.
        /// </summary>
        [Test]
        public void ConcurrentWorkersOnOneColumnStillReuseMostOfIt()
        {
            var generator = Build();
            //chunks 0 to 9 span world Y 0 to 319, all of it inside the layer, so every one of them
            //reaches the planner rather than being ruled out by bounds.
            const int stacks = 8, height = 10;

            Parallel.For(0, stacks, index => GenerateStack(generator, 5, 5, 0, height - 1));

            long total = generator.PlansComputed + generator.PlansReused;
            Assert.That(total, Is.EqualTo(stacks * height));
            //worst case is one plan per worker per pass; anything near that would mean the kept plan is
            //not helping and a shared cache would be worth its locking.
            Assert.That(generator.PlansComputed, Is.LessThan(total / 2),
                $"{generator.PlansComputed} of {total} chunks had to compute their column.");
        }

        private sealed class InlineGenerator : TerrainGeneratorDefinition
        {
            private readonly IReadOnlyList<WorldLayer> layers;
            public InlineGenerator(IReadOnlyList<WorldLayer> layers) => this.layers = layers;
            public override string Id => "digblocks.tests:reuse";
            public override string DisplayName => "Reuse";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context) => layers;
        }
    }
}
