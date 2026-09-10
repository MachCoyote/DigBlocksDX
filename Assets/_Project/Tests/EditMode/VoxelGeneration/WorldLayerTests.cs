using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed unsafe class WorldLayerTests
    {
        private const int Edge = ChunkLayout.Edge;
        private static readonly GenSeed Seed = new GenSeed(0xBEEFUL);
        private static readonly IBlockResolver Blocks = new StubBlockResolver();

        private readonly List<IDisposable> owned = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in owned) item.Dispose();
            owned.Clear();
        }

        private TerrainGenerator Build(params WorldLayer[] layers)
        {
            var generator = new InlineGenerator(layers).Build(Seed, Blocks);
            owned.Add(generator);
            return generator;
        }

        private static int Index(int x, int y, int z) => ChunkLayout.Index(new int3(x, y, z));

        private static (uint[] solids, uint[] fluids) Chunk() =>
            (new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);

        private static WorldLayer FlatLayer(string name, int surfaceY, int bottom, int top, ColumnRecipe recipe = null)
            => WorldLayer.Named(name)
                .Bounds(bottom, top)
                .Root(surfaceY)
                .Band("ground", BandDirection.Up, BandSurface.At(surfaceY),
                    recipe ?? ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone"))
                .Build();

        private static WorldLayer RollingLayer(string name, int rootY, int bottom, int top)
            => WorldLayer.Named(name)
                .Bounds(bottom, top)
                .Root(rootY)
                .Band("ground", BandDirection.Up,
                    rootY + NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 40f, 3) * 12f,
                    ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone"))
                .Build();

        [Test]
        public void AFlatLayerFillsItsColumnAsWritten()
        {
            var generator = Build(FlatLayer("overworld", 20, bottom: 0, top: 100));
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            Assert.That(solids[Index(4, 21, 4)], Is.EqualTo(StubBlockResolver.Air));
            Assert.That(solids[Index(4, 20, 4)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(solids[Index(4, 19, 4)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(4, 16, 4)], Is.EqualTo(StubBlockResolver.Stone));
        }

        /// <summary>
        /// The claim that keeps the world vertically unbounded: a layer somewhere far away must cost
        /// nothing to a chunk that does not touch it.
        /// </summary>
        [Test]
        public void AChunkThatMissesEveryLayerProducesNothing()
        {
            var generator = Build(
                FlatLayer("overworld", 20, bottom: 0, top: 100),
                FlatLayer("skyland", 5000, bottom: 4900, top: 5100));

            Assert.That(generator.LayersOverlapping(minY: 2000, maxY: 2031), Is.EqualTo(0UL));

            var (solids, fluids) = Chunk();
            generator.Generate(0, chunkY: 62, chunkZ: 0, solids, fluids);
            Assert.That(solids, Is.All.EqualTo(StubBlockResolver.Air));
            Assert.That(fluids, Is.All.EqualTo(0u));
        }

        [Test]
        public void ALayerFiveThousandBlocksUpGeneratesThere()
        {
            var generator = Build(
                FlatLayer("overworld", 20, bottom: 0, top: 100),
                FlatLayer("skyland", 5000, bottom: 4980, top: 5100));

            //chunk 156 covers world Y 4992 to 5023, so the sky layer's surface falls inside it.
            var (solids, fluids) = Chunk();
            generator.Generate(0, chunkY: 156, chunkZ: 0, solids, fluids);
            Assert.That(solids[Index(1, 5000 - 4992, 1)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(solids[Index(1, 5001 - 4992, 1)], Is.EqualTo(StubBlockResolver.Air));
            Assert.That(solids[Index(1, 4992 - 4992, 1)], Is.EqualTo(StubBlockResolver.Stone));
        }

        [Test]
        public void OverlapQueriesFindExactlyTheTouchedLayers()
        {
            var generator = Build(
                FlatLayer("low", 10, bottom: -64, top: 40),
                FlatLayer("middle", 200, bottom: 150, top: 260),
                FlatLayer("high", 5000, bottom: 4900, top: 5100));

            Assert.That(generator.LayersOverlapping(0, 31), Is.EqualTo(0b001UL));
            Assert.That(generator.LayersOverlapping(160, 191), Is.EqualTo(0b010UL));
            Assert.That(generator.LayersOverlapping(4992, 5023), Is.EqualTo(0b100UL));
            Assert.That(generator.LayersOverlapping(32, 63), Is.EqualTo(0b001UL), "A chunk grazing a layer's top still touches it.");
            Assert.That(generator.LayersOverlapping(64, 95), Is.EqualTo(0UL));
        }

        [Test]
        public void OverlappingLayersPaintInAuthoredOrder()
        {
            //two layers sharing a height. The one written second must win, whichever sits lower.
            var lower = FlatLayer("lower", 20, bottom: 0, top: 31, ColumnRecipe.Solid("stone"));
            var upper = FlatLayer("upper", 20, bottom: 0, top: 31, ColumnRecipe.Solid("sand"));

            var generator = Build(lower, upper);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);
            Assert.That(solids[Index(0, 10, 0)], Is.EqualTo(StubBlockResolver.Sand));

            var reversed = Build(upper, lower);
            var (otherSolids, otherFluids) = Chunk();
            reversed.Generate(0, 0, 0, otherSolids, otherFluids);
            Assert.That(otherSolids[Index(0, 10, 0)], Is.EqualTo(StubBlockResolver.Stone));
        }

        /// <summary>
        /// A column must not depend on which chunk asked for it, or terrain would step at every chunk
        /// boundary. Vertically this also proves the plan is genuinely independent of the chunk's Y.
        /// </summary>
        [Test]
        public void ColumnsAreIdenticalAcrossVerticallyStackedChunks()
        {
            var generator = Build(RollingLayer("overworld", 64, bottom: -64, top: 200));
            var heights = new Dictionary<int, uint>();

            for (int chunkY = 0; chunkY <= 3; chunkY++)
            {
                var (solids, fluids) = Chunk();
                generator.Generate(2, chunkY, -3, solids, fluids);
                for (int localY = 0; localY < Edge; localY++)
                    heights[chunkY * Edge + localY] = solids[Index(7, localY, 11)];
            }

            //the same column, read from a single generator run of each chunk, must be one continuous
            //stack: solid up to a surface, then air, with no gap or repeat at a chunk boundary.
            bool seenAir = false;
            for (int worldY = 0; worldY < Edge * 4; worldY++)
            {
                bool air = heights[worldY] == StubBlockResolver.Air;
                if (air) seenAir = true;
                else Assert.That(seenAir, Is.False, $"Solid reappeared above air at Y {worldY}, so the column is discontinuous.");
            }
        }

        [Test]
        public void ColumnsAreIdenticalAcrossHorizontallyAdjacentChunks()
        {
            var generator = Build(RollingLayer("overworld", 20, bottom: 0, top: 31));

            var (left, leftFluids) = Chunk();
            var (right, rightFluids) = Chunk();
            generator.Generate(0, 0, 0, left, leftFluids);
            generator.Generate(1, 0, 0, right, rightFluids);

            //the world column at x = 32 is the right chunk's local x = 0; the left chunk's x = 31 is
            //world 31. They are neighbours, so their surfaces may differ by at most a step or two.
            for (int z = 0; z < Edge; z++)
            {
                int leftTop = TopSolid(left, Edge - 1, z);
                int rightTop = TopSolid(right, 0, z);
                Assert.That(math.abs(leftTop - rightTop), Is.LessThanOrEqualTo(3),
                    $"Neighbouring columns at z {z} disagree by {math.abs(leftTop - rightTop)} blocks.");
            }
        }

        [Test]
        public void ARegeneratedChunkIsIdentical()
        {
            var generator = Build(RollingLayer("overworld", 20, bottom: 0, top: 31));
            var (first, firstFluids) = Chunk();
            var (second, secondFluids) = Chunk();
            generator.Generate(-4, 0, 9, first, firstFluids);
            generator.Generate(-4, 0, 9, second, secondFluids);
            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void DifferentSeedsGiveDifferentWorlds()
        {
            var layer = RollingLayer("overworld", 20, bottom: 0, top: 31);
            using (var first = new InlineGenerator(new[] { layer }).Build(new GenSeed(1UL), Blocks))
            using (var second = new InlineGenerator(new[] { layer }).Build(new GenSeed(2UL), Blocks))
            {
                var (a, af) = Chunk();
                var (b, bf) = Chunk();
                first.Generate(0, 0, 0, a, af);
                second.Generate(0, 0, 0, b, bf);
                CollectionAssert.AreNotEqual(a, b);
            }
        }

        /// <summary>
        /// Generation runs on pooled workers, so the generator is shared while its scratch is not. If
        /// that were wrong, concurrent chunks would corrupt one another's columns.
        /// </summary>
        [Test]
        public void ConcurrentGenerationOfTheSameChunkAgrees()
        {
            var generator = Build(RollingLayer("overworld", 20, bottom: 0, top: 31));
            var (reference, referenceFluids) = Chunk();
            generator.Generate(3, 0, 5, reference, referenceFluids);

            var results = new uint[16][];
            Parallel.For(0, results.Length, index =>
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                //a different address first, so each worker's pooled context arrives holding another
                //column's plan and has to notice it does not describe the chunk under test.
                generator.Generate(index + 7, 0, index - 4, solids, fluids);
                //buffers reach a source cleared to air, so the second run starts from the same state.
                Array.Clear(solids, 0, solids.Length);
                Array.Clear(fluids, 0, fluids.Length);
                generator.Generate(3, 0, 5, solids, fluids);
                results[index] = solids;
            });

            foreach (var result in results) CollectionAssert.AreEqual(reference, result);
        }

        [Test]
        public void ASeaFillsOpenSpaceBelowItsLevel()
        {
            var layer = WorldLayer.Named("overworld")
                .Bounds(0, 63).Root(20)
                .Sea(24, "water")
                .Band("ground", BandDirection.Up, BandSurface.At(12), ColumnRecipe.Create().Layer("grass").Deep("stone"))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            Assert.That(fluids[Index(2, 24, 2)], Is.EqualTo(StubBlockResolver.Water), "The sea surface itself must hold fluid.");
            Assert.That(fluids[Index(2, 13, 2)], Is.EqualTo(StubBlockResolver.Water));
            Assert.That(fluids[Index(2, 25, 2)], Is.EqualTo(0u), "Nothing above the sea level may be filled.");
            Assert.That(fluids[Index(2, 12, 2)], Is.EqualTo(0u), "A solid cell may not also hold the sea.");
            Assert.That(solids[Index(2, 12, 2)], Is.EqualTo(StubBlockResolver.Grass));
        }

        [Test]
        public void ADryLayerFillsNoFluid()
        {
            var generator = Build(FlatLayer("overworld", 12, bottom: 0, top: 63));
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);
            Assert.That(fluids, Is.All.EqualTo(0u));
        }

        [Test]
        public void ADownBandHangsFromTheLayerUnderside()
        {
            var layer = WorldLayer.Named("roof")
                .Bounds(0, 31).Root(24)
                .Band("body", BandDirection.Up, BandSurface.At(31), ColumnRecipe.Solid("stone"), extent: BandSurface.At(8))
                .Band("underside", BandDirection.Down, BandSurface.At(8),
                    ColumnRecipe.Create().Layer("grass").Layer("dirt", 2).Deep("stone"), extent: BandSurface.At(20))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            Assert.That(solids[Index(0, 7, 0)], Is.EqualTo(StubBlockResolver.Air), "Below the underside must be open.");
            Assert.That(solids[Index(0, 8, 0)], Is.EqualTo(StubBlockResolver.Grass), "The lowest cell is the upside-down surface.");
            Assert.That(solids[Index(0, 9, 0)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(0, 11, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(0, 31, 0)], Is.EqualTo(StubBlockResolver.Stone));
        }

        [Test]
        public void ARegisteredBandFunctionCanProvideASurface()
        {
            BandHeightRegistry.Register(TestBandHeights.Id, TestBandHeights.Sawtooth);
            var layer = WorldLayer.Named("sawtooth")
                .Bounds(0, 31).Root(0)
                .Band("ground", BandDirection.Up, BandSurface.Function(TestBandHeights.Id, "saw"), ColumnRecipe.Solid("stone"))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            for (int x = 0; x < Edge; x++)
                Assert.That(TopSolid(solids, x, 0), Is.EqualTo(x % 8), $"Column {x} should crest at {x % 8}.");
        }

        [Test]
        public void LayersWithTheSameNameAreRejected()
        {
            //seeds derive from the name, so two layers sharing one would be the same world twice.
            var first = FlatLayer("overworld", 10, 0, 31);
            var second = FlatLayer("overworld", 20, 0, 31);
            Assert.Throws<GenerationContentException>(() => new InlineGenerator(new[] { first, second }).Build(Seed, Blocks));
        }

        [Test]
        public void ALayerWithASeaButNoFluidIsRejected()
            => Assert.Throws<GenerationContentException>(() => WorldLayer.Named("bad")
                .Bounds(0, 31)
                .Band("ground", BandDirection.Up, BandSurface.At(4), ColumnRecipe.Solid("stone"))
                .Sea(10, null)
                .Build());

        [Test]
        public void ALayerWithNothingToDrawIsRejected()
            => Assert.Throws<GenerationContentException>(() => WorldLayer.Named("empty").Bounds(0, 31).Build());

        [Test]
        public void ALayerWithInvertedBoundsIsRejected()
            => Assert.Throws<GenerationContentException>(() => WorldLayer.Named("bad")
                .Bounds(40, 10)
                .Band("ground", BandDirection.Up, BandSurface.At(4), ColumnRecipe.Solid("stone"))
                .Build());

        [Test]
        public void AGeneratorWithNoLayersIsRejected()
            => Assert.Throws<GenerationContentException>(() => new InlineGenerator(Array.Empty<WorldLayer>()).Build(Seed, Blocks));

        [Test]
        public void ASurfaceThatSamplesYIsRejected()
        {
            var layer = WorldLayer.Named("bad")
                .Bounds(0, 31)
                .Band("ground", BandDirection.Up, NoiseExpr.Y * 2f, ColumnRecipe.Solid("stone"))
                .Build();
            Assert.Throws<GenerationContentException>(() => new InlineGenerator(new[] { layer }).Build(Seed, Blocks));
        }

        [Test]
        public void LayerLookupReportsTheLatestAuthoredLayerAtAHeight()
        {
            var generator = Build(
                FlatLayer("lower", 10, bottom: 0, top: 40),
                FlatLayer("upper", 30, bottom: 20, top: 60));

            Assert.That(generator.LayerAt(10).Name, Is.EqualTo("lower"));
            Assert.That(generator.LayerAt(30).Name, Is.EqualTo("upper"), "Where they overlap, the later author wins.");
            Assert.That(generator.LayerAt(50).Name, Is.EqualTo("upper"));
            Assert.That(generator.LayerAt(500), Is.Null);
        }

        private static int TopSolid(uint[] solids, int x, int z)
        {
            for (int y = Edge - 1; y >= 0; y--)
                if (solids[Index(x, y, z)] != StubBlockResolver.Air) return y;
            return -1;
        }

        /// <summary>A world type defined by the layers a test hands it.</summary>
        private sealed class InlineGenerator : TerrainGeneratorDefinition
        {
            private readonly IReadOnlyList<WorldLayer> layers;
            public InlineGenerator(IReadOnlyList<WorldLayer> layers) => this.layers = layers;
            public override string Id => "digblocks.tests:inline";
            public override string DisplayName => "Inline";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context) => layers;
        }
    }

    [Unity.Burst.BurstCompile]
    internal static class TestBandHeights
    {
        public const string Id = "digblocks.tests:sawtooth";

        [Unity.Burst.BurstCompile(FloatMode = Unity.Burst.FloatMode.Strict, FloatPrecision = Unity.Burst.FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(BandHeightFunction))]
        public static unsafe void Sawtooth(float* x, float* z, float* result, int count, uint seed)
        {
            for (int index = 0; index < count; index++)
                result[index] = (int)x[index] % 8;
        }
    }
}
