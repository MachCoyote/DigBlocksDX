using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed class DensityStageTests
    {
        private const int Edge = ChunkLayout.Edge;
        private static readonly GenSeed Seed = new GenSeed(0xD3151A5EUL);
        private static readonly IBlockResolver Blocks = new StubBlockResolver();

        private readonly List<IDisposable> owned = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in owned) item.Dispose();
            owned.Clear();
        }

        private TerrainGenerator Build(WorldLayer layer)
        {
            var generator = new InlineGenerator(new[] { layer }).Build(Seed, Blocks);
            owned.Add(generator);
            return generator;
        }

        private static int Index(int x, int y, int z) => ChunkLayout.Index(new int3(x, y, z));
        private static (uint[] solids, uint[] fluids) Chunk() => (new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);

        private static WorldLayer.Builder SolidBlock(string name, int bottom, int top)
            => WorldLayer.Named(name)
                .Bounds(bottom, top)
                .Band("body", BandDirection.Up, BandSurface.At(top), ColumnRecipe.Solid("stone"),
                    extent: BandSurface.At(bottom));

        /// <summary>
        /// A field that is positive outside a sphere and negative inside it, so carving must hollow
        /// out exactly that sphere and nothing else. A known shape is the only way to tell a working
        /// interpolation from one that is merely plausible.
        /// </summary>
        private static NoiseExpr SphereField(float centreX, float centreY, float centreZ, float radius)
        {
            var dx = NoiseExpr.X - centreX;
            var dy = NoiseExpr.Y - centreY;
            var dz = NoiseExpr.Z - centreZ;
            return (dx * dx + dy * dy + dz * dz).Sqrt() - NoiseExpr.Constant(radius);
        }

        [Test]
        public void CarvingHollowsOutTheFieldsInterior()
        {
            var layer = SolidBlock("block", 0, 31)
                .Density(new DensityStage("cave", SphereField(16f, 16f, 16f, 8f),
                    DensityMode.Carve, threshold: 0f, resolution: new int3(1, 1, 1)))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            Assert.That(solids[Index(16, 16, 16)], Is.EqualTo(StubBlockResolver.Air), "The centre of the sphere should be carved.");
            Assert.That(solids[Index(16, 22, 16)], Is.EqualTo(StubBlockResolver.Air), "Just inside the surface should be carved.");
            Assert.That(solids[Index(16, 26, 16)], Is.EqualTo(StubBlockResolver.Stone), "Well outside the sphere must stay solid.");
            Assert.That(solids[Index(0, 0, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(31, 31, 31)], Is.EqualTo(StubBlockResolver.Stone));
        }

        [Test]
        public void ACoarseLatticeApproximatesTheSameShape()
        {
            //the whole point of the lattice is that a coarse one is close enough. Four by eight by four
            //takes 405 samples where per-cell takes 32768.
            var fine = Build(SolidBlock("fine", 0, 31)
                .Density(new DensityStage("cave", SphereField(16f, 16f, 16f, 10f), resolution: new int3(1, 1, 1)))
                .Build());
            var coarse = Build(SolidBlock("coarse", 0, 31)
                .Density(new DensityStage("cave", SphereField(16f, 16f, 16f, 10f), resolution: new int3(4, 8, 4)))
                .Build());

            var (fineSolids, fineFluids) = Chunk();
            var (coarseSolids, coarseFluids) = Chunk();
            fine.Generate(0, 0, 0, fineSolids, fineFluids);
            coarse.Generate(0, 0, 0, coarseSolids, coarseFluids);

            int fineAir = 0, coarseAir = 0, disagreements = 0;
            for (int index = 0; index < ChunkLayout.Volume; index++)
            {
                if (fineSolids[index] == 0u) fineAir++;
                if (coarseSolids[index] == 0u) coarseAir++;
                if ((fineSolids[index] == 0u) != (coarseSolids[index] == 0u)) disagreements++;
            }

            Assert.That(fineAir, Is.GreaterThan(3000));
            Assert.That(coarseAir, Is.GreaterThan(3000));
            //the two differ only in a shell around the boundary, not in bulk.
            Assert.That(disagreements, Is.LessThan(fineAir / 3),
                $"The coarse lattice disagreed on {disagreements} cells against {fineAir} carved.");
        }

        [Test]
        public void CarvingIsSeamlessAcrossAChunkBoundary()
        {
            //the lattice has to land on chunk boundaries, or two chunks interpolate between different
            //samples and the carve steps at the seam.
            var field = NoiseExpr.Perlin3D("caves", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 1f / 24f, 2);
            var generator = Build(SolidBlock("body", 0, 31)
                .Density(new DensityStage("caves", field, threshold: -0.1f, resolution: new int3(4, 4, 4)))
                .Build());

            var (left, leftFluids) = Chunk();
            var (right, rightFluids) = Chunk();
            generator.Generate(0, 0, 0, left, leftFluids);
            generator.Generate(1, 0, 0, right, rightFluids);

            //the two cells either side of the boundary sample a field that is continuous there, so the
            //carve cannot flip on a large fraction of the plane.
            int flips = 0;
            for (int y = 0; y < Edge; y++)
            for (int z = 0; z < Edge; z++)
                if ((left[Index(Edge - 1, y, z)] == 0u) != (right[Index(0, y, z)] == 0u)) flips++;
            Assert.That(flips, Is.LessThan(Edge * Edge / 8), $"{flips} cells flipped across the seam.");
        }

        [Test]
        public void AddingPlacesSolidWhereTheFieldReachesTheThreshold()
        {
            var layer = WorldLayer.Named("islands")
                .Bounds(0, 31)
                .Band("floor", BandDirection.Up, BandSurface.At(2), ColumnRecipe.Solid("stone"))
                .Density(new DensityStage("islands", -SphereField(16f, 20f, 16f, 6f),
                    DensityMode.Add, threshold: 0f, resolution: new int3(1, 1, 1), addBlock: "sand"))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            Assert.That(solids[Index(16, 20, 16)], Is.EqualTo(StubBlockResolver.Sand), "Inside the field, solid should be added.");
            Assert.That(solids[Index(16, 30, 16)], Is.EqualTo(StubBlockResolver.Air), "Outside it, nothing is added.");
            Assert.That(solids[Index(0, 0, 0)], Is.EqualTo(StubBlockResolver.Stone), "The band's own fill survives.");
        }

        /// <summary>
        /// The reason gradients exist: terrain that closes off at the ends of its layer without a hard
        /// clamp, so it can still overhang in the middle.
        /// </summary>
        [Test]
        public void AGradientClosesTheLayerAtItsFloorAndCeiling()
        {
            var field = NoiseExpr.Perlin3D("shape", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 1f / 20f, 2);
            var layer = SolidBlock("body", 0, 31)
                .Density(new DensityStage("shape", field, threshold: 0f, resolution: new int3(4, 4, 4),
                    gradients: new[]
                    {
                        DensityGradient.Floor(y: 0, fade: 8, strength: 4f),
                        DensityGradient.Ceiling(y: 31, fade: 8, strength: 4f)
                    }))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            for (int z = 0; z < Edge; z++)
            for (int x = 0; x < Edge; x++)
            {
                Assert.That(solids[Index(x, 0, z)], Is.EqualTo(StubBlockResolver.Stone), $"The floor at ({x}, {z}) was carved through.");
                Assert.That(solids[Index(x, 31, z)], Is.EqualTo(StubBlockResolver.Air), $"The ceiling at ({x}, {z}) was not opened.");
            }

            //and the middle is still free to be either, which is what separates this from a clamp.
            int middleAir = 0;
            for (int z = 0; z < Edge; z++)
            for (int x = 0; x < Edge; x++)
                if (solids[Index(x, 16, z)] == 0u) middleAir++;
            Assert.That(middleAir, Is.InRange(1, Edge * Edge - 1), "The middle of the layer was decided entirely by the gradient.");
        }

        [Test]
        public void AquifersFillSomeCarvedRegionsAndLeaveOthersDry()
        {
            var layer = SolidBlock("body", 0, 255)
                .Density(new DensityStage("caves", NoiseExpr.Perlin3D("caves", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 1f / 18f, 2),
                    threshold: 0.15f, resolution: new int3(4, 4, 4)))
                .Aquifers(new AquiferStage("water", "water", minY: 0, maxY: 255, baseLevel: 128,
                    levelJitter: 40, dryChance: 0.35f, cellSize: new int3(16, 16, 16)))
                .Build();
            var generator = Build(layer);

            int wet = 0, dry = 0;
            for (int chunkY = 2; chunkY <= 5; chunkY++)
            {
                var (solids, fluids) = Chunk();
                generator.Generate(0, chunkY, 0, solids, fluids);
                for (int index = 0; index < ChunkLayout.Volume; index++)
                {
                    if (solids[index] != 0u) continue;
                    if (fluids[index] == StubBlockResolver.Water) wet++; else dry++;
                }
            }

            Assert.That(wet, Is.GreaterThan(0), "No carved space held water, so aquifers did nothing.");
            Assert.That(dry, Is.GreaterThan(0), "Every carved space held water, so no region was dry.");
        }

        [Test]
        public void AnAquiferRegionHoldsOneFlatLevel()
        {
            //water has to be flat to read as water, so a region's level is one number, not a field.
            var stage = new AquiferStage("water", "water", 0, 255, baseLevel: 100, levelJitter: 20,
                dryChance: 0f, cellSize: new int3(16, 16, 16));
            int level = stage.LevelAt(Seed, 4, 100, 4);
            Assert.That(level, Is.InRange(80, 120));
            for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                Assert.That(stage.LevelAt(Seed, x, 100, z), Is.EqualTo(level), $"({x}, {z}) disagreed with its own region.");
        }

        [Test]
        public void AquiferLevelsAreRepeatableAndSeedDependent()
        {
            var stage = new AquiferStage("water", "water", 0, 255, 100, dryChance: 0f);
            Assert.That(stage.LevelAt(Seed, 40, 60, -80), Is.EqualTo(stage.LevelAt(Seed, 40, 60, -80)));
            var differences = 0;
            for (int cell = 0; cell < 32; cell++)
                if (stage.LevelAt(new GenSeed(1UL), cell * 40, 60, 0) != stage.LevelAt(new GenSeed(2UL), cell * 40, 60, 0))
                    differences++;
            Assert.That(differences, Is.GreaterThan(16));
        }

        /// <summary>
        /// Without this, a blanket sea fill would flood every carved cavern to the layer floor and no
        /// aquifer region could ever be dry.
        /// </summary>
        [Test]
        public void AquifersOwnTheWaterBelowThemAndTheSeaOwnsWhatIsAbove()
        {
            var layer = WorldLayer.Named("world")
                .Bounds(0, 63)
                .Band("ground", BandDirection.Up, BandSurface.At(20), ColumnRecipe.Solid("stone"))
                .Sea(40, "water")
                .Density(new DensityStage("caves", SphereField(16f, 10f, 16f, 6f), threshold: 0f, resolution: new int3(1, 1, 1)))
                .Aquifers(new AquiferStage("water", "water", minY: 0, maxY: 24, baseLevel: 8,
                    levelJitter: 0, dryChance: 0f, cellSize: new int3(64, 64, 64)))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);

            //open sky above the ground, below sea level: the sea's business.
            Assert.That(solids[Index(0, 30, 0)], Is.EqualTo(StubBlockResolver.Air));
            Assert.That(fluids[Index(0, 30, 0)], Is.EqualTo(StubBlockResolver.Water));
            //the carved cavern sits under the aquifer's top, so the sea must not have reached it.
            Assert.That(solids[Index(16, 10, 16)], Is.EqualTo(StubBlockResolver.Air));
            Assert.That(fluids[Index(16, 14, 16)], Is.EqualTo(0u), "Above this region's water table the cavern must be dry.");
            Assert.That(fluids[Index(16, 6, 16)], Is.EqualTo(StubBlockResolver.Water), "Below its water table the cavern must be flooded.");
        }

        [Test]
        public void ALatticeSpacingThatDoesNotDivideTheChunkIsRejected()
            => Assert.Throws<GenerationContentException>(() => new DensityStage("bad",
                NoiseExpr.Perlin3D("f", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 0.1f), resolution: new int3(5, 4, 4)));

        [Test]
        public void AddingWithoutABlockIsRejected()
            => Assert.Throws<GenerationContentException>(() => new DensityStage("bad",
                NoiseExpr.Perlin3D("f", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 0.1f), DensityMode.Add));

        [Test]
        public void AquifersWithoutADensityStageAreRejected()
            => Assert.Throws<GenerationContentException>(() => SolidBlock("bad", 0, 31)
                .Aquifers(new AquiferStage("water", "water", 0, 31, 16))
                .Build());

        [Test]
        public void ADensityOnlyLayerNeedsNoBands()
        {
            var layer = WorldLayer.Named("cloud")
                .Bounds(0, 31)
                .Density(new DensityStage("blob", -SphereField(16f, 16f, 16f, 9f),
                    DensityMode.Add, resolution: new int3(2, 2, 2), addBlock: "stone"))
                .Build();
            var generator = Build(layer);
            var (solids, fluids) = Chunk();
            generator.Generate(0, 0, 0, solids, fluids);
            Assert.That(solids[Index(16, 16, 16)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(0, 0, 0)], Is.EqualTo(StubBlockResolver.Air));
        }

        private sealed class InlineGenerator : TerrainGeneratorDefinition
        {
            private readonly IReadOnlyList<WorldLayer> layers;
            public InlineGenerator(IReadOnlyList<WorldLayer> layers) => this.layers = layers;
            public override string Id => "digblocks.tests:density";
            public override string DisplayName => "Density";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context) => layers;
        }
    }
}
