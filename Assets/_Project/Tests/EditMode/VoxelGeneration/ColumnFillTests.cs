using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation.Tests
{
    /// <summary>Resolves the handful of block keys these tests use to small, obvious ids.</summary>
    internal sealed class StubBlockResolver : IBlockResolver
    {
        public const uint Air = 0, Grass = 1, Dirt = 2, Stone = 3, Deepslate = 4, Sand = 5, Snow = 6, Bedrock = 7, Water = 1;

        private static readonly Dictionary<string, uint> Solids = new Dictionary<string, uint>
        {
            { "grass", Grass }, { "dirt", Dirt }, { "stone", Stone }, { "deepslate", Deepslate },
            { "sand", Sand }, { "snow", Snow }, { "bedrock", Bedrock }
        };

        public uint Solid(string key) => Solids.TryGetValue(key, out uint id)
            ? id : throw new KeyNotFoundException(key);

        public uint Fluid(string key) => key == "water" ? Water : throw new KeyNotFoundException(key);
    }

    public sealed class ColumnFillTests
    {
        private const int Edge = ChunkLayout.Edge;
        private const int Columns = Edge * Edge;

        private readonly List<IDisposable> owned = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in owned) item.Dispose();
            owned.Clear();
        }

        private CompiledRecipe Compile(ColumnRecipe recipe)
        {
            var compiled = recipe.Compile(new StubBlockResolver());
            owned.Add(compiled);
            return compiled;
        }

        private LayerColumnPlan Plan(int bandCount = 1)
        {
            var plan = new LayerColumnPlan(bandCount);
            owned.Add(plan);
            return plan;
        }

        private static int Index(int x, int y, int z) => ChunkLayout.Index(new int3(x, y, z));

        /// <summary>Runs the fill through both the Burst kernel and the managed one and requires they agree.</summary>
        private static unsafe uint[] Fill(BandFillData band, uint[] existing = null)
        {
            var solids = existing ?? new uint[ChunkLayout.Volume];
            var managed = (uint[])solids.Clone();

            fixed (uint* target = solids) ColumnFillDispatch.Compiled.Invoke(&band, target);
            fixed (uint* target = managed) ColumnFillKernel.Fill(&band, target);
            CollectionAssert.AreEqual(managed, solids, "The Burst and managed fills disagreed.");
            return solids;
        }

        private unsafe BandFillData Band(LayerColumnPlan plan, CompiledRecipe recipe, BandDirection direction,
            int chunkMinY, int fallbackExtent, int layerBottom = int.MinValue, int layerTop = int.MaxValue,
            int seaLevel = 0, bool hasSea = false, bool hasExtent = false, int band = 0)
            => new BandFillData
            {
                SurfaceY = plan.Surface(band),
                ExtentY = plan.Extent(band),
                Recipe = recipe.Data,
                ChunkMinY = chunkMinY,
                LayerBottom = layerBottom,
                LayerTop = layerTop,
                SeaLevel = seaLevel,
                FallbackExtentY = fallbackExtent,
                Direction = (byte)direction,
                HasSea = (byte)(hasSea ? 1 : 0),
                HasExtent = (byte)(hasExtent ? 1 : 0)
            };

        private static unsafe void SetSurface(LayerColumnPlan plan, int band, Func<int, int, int> height)
        {
            int* surface = plan.Surface(band);
            for (int z = 0; z < Edge; z++)
            for (int x = 0; x < Edge; x++)
                surface[x + Edge * z] = height(x, z);
        }

        private static unsafe void SetExtent(LayerColumnPlan plan, int band, Func<int, int, int> height)
        {
            int* extent = plan.Extent(band);
            for (int z = 0; z < Edge; z++)
            for (int x = 0; x < Edge; x++)
                extent[x + Edge * z] = height(x, z);
        }

        [Test]
        public void AnUpBandPaintsDownwardFromItsSurface()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 20);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0));

            Assert.That(solids[Index(5, 21, 5)], Is.EqualTo(StubBlockResolver.Air), "Above the surface must stay air.");
            Assert.That(solids[Index(5, 20, 5)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(solids[Index(5, 19, 5)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(5, 17, 5)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(5, 16, 5)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(5, 0, 5)], Is.EqualTo(StubBlockResolver.Stone));
        }

        /// <summary>The author's stated case: rolling ground hanging from the underside of a layer.</summary>
        [Test]
        public void ADownBandPaintsUpwardFromItsSurface()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 8);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Layer("dirt", 2).Deep("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Down, chunkMinY: 0, fallbackExtent: 31));

            Assert.That(solids[Index(3, 7, 3)], Is.EqualTo(StubBlockResolver.Air), "Below the free face must stay air.");
            Assert.That(solids[Index(3, 8, 3)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(solids[Index(3, 9, 3)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(3, 10, 3)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(solids[Index(3, 11, 3)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(3, 31, 3)], Is.EqualTo(StubBlockResolver.Stone));
        }

        [Test]
        public void ASlopedSurfaceFollowsTheColumnHeights()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => x);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Deep("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0));

            for (int x = 0; x < Edge; x++)
            {
                Assert.That(solids[Index(x, x, 7)], Is.EqualTo(StubBlockResolver.Grass), $"Column {x} should crest at {x}.");
                if (x + 1 < Edge)
                    Assert.That(solids[Index(x, x + 1, 7)], Is.EqualTo(StubBlockResolver.Air), $"Column {x} should be open above {x}.");
            }
        }

        [Test]
        public void AnExtentStopsTheFill()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 24);
            SetExtent(plan, 0, (x, z) => 10);
            var recipe = Compile(ColumnRecipe.Solid("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0, hasExtent: true));

            Assert.That(solids[Index(1, 10, 1)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(1, 9, 1)], Is.EqualTo(StubBlockResolver.Air), "Below the extent must stay air.");
            Assert.That(solids[Index(1, 24, 1)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(1, 25, 1)], Is.EqualTo(StubBlockResolver.Air));
        }

        [Test]
        public void AnInvertedIntervalWritesNothing()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 4);
            SetExtent(plan, 0, (x, z) => 20);
            var recipe = Compile(ColumnRecipe.Solid("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0, hasExtent: true));
            Assert.That(solids, Is.All.EqualTo(StubBlockResolver.Air));
        }

        [Test]
        public void LaterBandsPaintOverEarlierOnes()
        {
            var plan = Plan(2);
            SetSurface(plan, 0, (x, z) => 20);
            SetSurface(plan, 1, (x, z) => 12);
            var ground = Compile(ColumnRecipe.Solid("stone"));
            var capping = Compile(ColumnRecipe.Solid("snow"));

            var solids = Fill(Band(plan, ground, BandDirection.Up, 0, fallbackExtent: 0));
            solids = Fill(Band(plan, capping, BandDirection.Up, 0, fallbackExtent: 8, hasExtent: false, band: 1), solids);

            Assert.That(solids[Index(0, 20, 0)], Is.EqualTo(StubBlockResolver.Stone), "Above the second band the first survives.");
            Assert.That(solids[Index(0, 12, 0)], Is.EqualTo(StubBlockResolver.Snow), "Where they overlap the later band wins.");
            Assert.That(solids[Index(0, 8, 0)], Is.EqualTo(StubBlockResolver.Snow));
            Assert.That(solids[Index(0, 7, 0)], Is.EqualTo(StubBlockResolver.Stone), "Below the second band the first survives.");
        }

        [Test]
        public void ChunksAtOtherHeightsSeeTheSameColumn()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 40);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone"));

            var upper = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 32, fallbackExtent: -64));
            var lower = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: -64));

            //the surface falls in the upper chunk, so the lower chunk is solid stone throughout.
            Assert.That(upper[Index(2, 40 - 32, 2)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(upper[Index(2, 37 - 32, 2)], Is.EqualTo(StubBlockResolver.Dirt));
            Assert.That(upper[Index(2, 36 - 32, 2)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(lower, Is.All.EqualTo(StubBlockResolver.Stone));
        }

        [Test]
        public void LayerBoundsClipTheFill()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 30);
            var recipe = Compile(ColumnRecipe.Solid("stone"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0,
                layerBottom: 10, layerTop: 20));

            Assert.That(solids[Index(0, 21, 0)], Is.EqualTo(StubBlockResolver.Air), "Above the layer top must stay air.");
            Assert.That(solids[Index(0, 20, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(0, 10, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(0, 9, 0)], Is.EqualTo(StubBlockResolver.Air), "Below the layer bottom must stay air.");
        }

        [Test]
        public void DeepBelowSwapsTheDeepFill()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 30);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Deep("stone").DeepBelow(16, "deepslate"));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0));

            Assert.That(solids[Index(0, 30, 0)], Is.EqualTo(StubBlockResolver.Grass));
            Assert.That(solids[Index(0, 16, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(0, 15, 0)], Is.EqualTo(StubBlockResolver.Deepslate));
            Assert.That(solids[Index(0, 0, 0)], Is.EqualTo(StubBlockResolver.Deepslate));
        }

        [Test]
        public void SubmergedColumnsTakeTheirShoreMaterial()
        {
            var plan = Plan();
            //one half of the chunk sits below the water line, the other above it.
            SetSurface(plan, 0, (x, z) => x < 16 ? 8 : 20);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Layer("dirt", 3).Deep("stone").Submerged("sand", 2));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0,
                seaLevel: 12, hasSea: true));

            Assert.That(solids[Index(4, 8, 0)], Is.EqualTo(StubBlockResolver.Sand));
            Assert.That(solids[Index(4, 7, 0)], Is.EqualTo(StubBlockResolver.Sand));
            Assert.That(solids[Index(4, 6, 0)], Is.EqualTo(StubBlockResolver.Dirt), "Past the shore depth the ordinary strata resume.");
            Assert.That(solids[Index(20, 20, 0)], Is.EqualTo(StubBlockResolver.Grass), "Dry land keeps its surface.");
        }

        [Test]
        public void ALayerWithNoSeaIgnoresItsShoreRule()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => 8);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Deep("stone").Submerged("sand", 2));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0,
                seaLevel: 12, hasSea: false));
            Assert.That(solids[Index(4, 8, 0)], Is.EqualTo(StubBlockResolver.Grass));
        }

        [Test]
        public void CrestsTakeTheirAltitudeMaterial()
        {
            var plan = Plan();
            SetSurface(plan, 0, (x, z) => x < 16 ? 10 : 28);
            var recipe = Compile(ColumnRecipe.Create().Layer("grass").Deep("stone").Crest(24, "snow", 2));
            var solids = Fill(Band(plan, recipe, BandDirection.Up, chunkMinY: 0, fallbackExtent: 0));

            Assert.That(solids[Index(20, 28, 0)], Is.EqualTo(StubBlockResolver.Snow));
            Assert.That(solids[Index(20, 27, 0)], Is.EqualTo(StubBlockResolver.Snow));
            Assert.That(solids[Index(20, 26, 0)], Is.EqualTo(StubBlockResolver.Stone));
            Assert.That(solids[Index(4, 10, 0)], Is.EqualTo(StubBlockResolver.Grass), "Low ground is unaffected.");
        }

        [Test]
        public void ARecipeWithoutADeepFillIsRejected()
            => Assert.Throws<InvalidOperationException>(
                () => ColumnRecipe.Create().Layer("grass").Compile(new StubBlockResolver()));

        [Test]
        public void RecipeEntriesValidateTheirArguments()
        {
            Assert.Throws<ArgumentException>(() => ColumnRecipe.Create().Layer(" "));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColumnRecipe.Create().Layer("grass", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColumnRecipe.Create().Submerged("sand", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColumnRecipe.Create().Crest(4, "snow", 0));
        }
    }
}
