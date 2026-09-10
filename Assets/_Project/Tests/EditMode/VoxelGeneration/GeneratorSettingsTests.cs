using System.Collections.Generic;
using NUnit.Framework;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed class GeneratorSettingsTests
    {
        private static readonly GeneratorParameterSchema Schema = new GeneratorParameterSchema(
            GeneratorParameter.Number("amplification", "Amplification", 0.1f, 8f, 1f, "Scales terrain height."),
            GeneratorParameter.Integer("seaLevel", "Sea level", -64, 320, 62),
            GeneratorParameter.Toggle("caves", "Caves", true),
            GeneratorParameter.Choice("size", "World size", new[] { "small", "normal", "large" }, 1));

        [Test]
        public void DefaultsComeFromTheSchema()
        {
            var settings = GeneratorSettings.Defaults(Schema);
            Assert.That(settings.Number("amplification"), Is.EqualTo(1f));
            Assert.That(settings.Integer("seaLevel"), Is.EqualTo(62));
            Assert.That(settings.Toggle("caves"), Is.True);
            Assert.That(settings.Choice("size"), Is.EqualTo("normal"));
        }

        [Test]
        public void SettingAValueLeavesTheOriginalAlone()
        {
            var defaults = GeneratorSettings.Defaults(Schema);
            var amplified = defaults.With("amplification", 4f);
            Assert.That(amplified.Number("amplification"), Is.EqualTo(4f));
            Assert.That(defaults.Number("amplification"), Is.EqualTo(1f));
        }

        /// <summary>A setting saved before a range changed must still load, not fail the world.</summary>
        [Test]
        public void OutOfRangeValuesAreBroughtIntoRange()
        {
            var settings = GeneratorSettings.Defaults(Schema).With("amplification", 900f).With("seaLevel", -9000);
            Assert.That(settings.Number("amplification"), Is.EqualTo(8f));
            Assert.That(settings.Integer("seaLevel"), Is.EqualTo(-64));
        }

        [Test]
        public void IntegerParametersRoundRatherThanTruncate()
            => Assert.That(GeneratorSettings.Defaults(Schema).With("seaLevel", 61.7f).Integer("seaLevel"), Is.EqualTo(62));

        [Test]
        public void ChoicesCanBeSetByName()
        {
            var settings = GeneratorSettings.Defaults(Schema).With("size", "large");
            Assert.That(settings.Choice("size"), Is.EqualTo("large"));
            Assert.That(settings.ChoiceIndex("size"), Is.EqualTo(2));
        }

        [Test]
        public void ReadingTheWrongKindIsRejected()
            => Assert.Throws<GenerationContentException>(() => GeneratorSettings.Defaults(Schema).Number("caves"));

        [Test]
        public void UnknownKeysAreRejected()
        {
            Assert.Throws<GenerationContentException>(() => GeneratorSettings.Defaults(Schema).Number("nonsense"));
            Assert.Throws<GenerationContentException>(() => GeneratorSettings.Defaults(Schema).With("nonsense", 1f));
        }

        [Test]
        public void UnknownChoiceOptionsAreRejected()
            => Assert.Throws<GenerationContentException>(() => GeneratorSettings.Defaults(Schema).With("size", "enormous"));

        [Test]
        public void ADuplicatedParameterKeyIsRejected()
            => Assert.Throws<System.ArgumentException>(() => new GeneratorParameterSchema(
                GeneratorParameter.Number("a", "A", 0f, 1f, 0f),
                GeneratorParameter.Number("a", "A again", 0f, 1f, 0f)));

        [Test]
        public void ADefaultOutsideItsOwnRangeIsRejected()
            => Assert.Throws<System.ArgumentOutOfRangeException>(() => GeneratorParameter.Number("a", "A", 0f, 1f, 5f));

        [Test]
        public void TheSchemaEnumeratesInAuthoredOrder()
        {
            var keys = new List<string>();
            foreach (var parameter in Schema) keys.Add(parameter.Key);
            CollectionAssert.AreEqual(new[] { "amplification", "seaLevel", "caves", "size" }, keys);
        }

        [Test]
        public void RegisteringAWorldTypeMakesItResolvable()
        {
            TerrainGeneratorRegistry.Register(new SampleType());
            Assert.That(TerrainGeneratorRegistry.Resolve("digblocks.tests:sample").DisplayName, Is.EqualTo("Sample"));
            Assert.That(TerrainGeneratorRegistry.TryResolve("digblocks.tests:absent", out _), Is.False);
        }

        [Test]
        public void RegisteringTheSameWorldTypeTwiceIsHarmless()
        {
            TerrainGeneratorRegistry.Register(new SampleType());
            Assert.DoesNotThrow(() => TerrainGeneratorRegistry.Register(new SampleType()));
        }

        [Test]
        public void TwoWorldTypesClaimingOneIdAreRejected()
        {
            TerrainGeneratorRegistry.Register(new SampleType());
            Assert.Throws<GenerationContentException>(() => TerrainGeneratorRegistry.Register(new ImposterType()));
        }

        private sealed class SampleType : TerrainGeneratorDefinition
        {
            public override string Id => "digblocks.tests:sample";
            public override string DisplayName => "Sample";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
                => new[] { WorldLayer.Named("only").Bounds(0, 31)
                    .Band("ground", BandDirection.Up, BandSurface.At(4), ColumnRecipe.Solid("stone")).Build() };
        }

        private sealed class ImposterType : TerrainGeneratorDefinition
        {
            public override string Id => "digblocks.tests:sample";
            public override string DisplayName => "Imposter";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
                => System.Array.Empty<WorldLayer>();
        }
    }

    public sealed class RegionGridTests
    {
        private static readonly GenSeed Seed = new GenSeed(0x5150UL);

        [Test]
        public void APlacementIsTheSameEveryTimeItIsAsked()
        {
            var grid = new RegionGrid("villages", spacing: 64);
            Assert.That(grid.TryPlacement(Seed, 3, -7, out var first), Is.True);
            Assert.That(grid.TryPlacement(Seed, 3, -7, out var second), Is.True);
            Assert.That(second.WorldX, Is.EqualTo(first.WorldX));
            Assert.That(second.WorldZ, Is.EqualTo(first.WorldZ));
            Assert.That(second.Seed.Value, Is.EqualTo(first.Seed.Value));
        }

        /// <summary>Bounded separation is the point of a region grid, so a placement may never leave its own region.</summary>
        [Test]
        public void APlacementStaysInsideItsRegion()
        {
            var grid = new RegionGrid("villages", spacing: 48);
            for (int regionZ = -8; regionZ <= 8; regionZ++)
            for (int regionX = -8; regionX <= 8; regionX++)
            {
                Assert.That(grid.TryPlacement(Seed, regionX, regionZ, out var placement), Is.True);
                Assert.That(placement.WorldX, Is.InRange(regionX * 48, regionX * 48 + 47));
                Assert.That(placement.WorldZ, Is.InRange(regionZ * 48, regionZ * 48 + 47));
                Assert.That(grid.RegionOf(placement.WorldX), Is.EqualTo(regionX));
                Assert.That(grid.RegionOf(placement.WorldZ), Is.EqualTo(regionZ));
            }
        }

        [Test]
        public void DensityThinsTheGridWithoutMovingWhatSurvives()
        {
            var full = new RegionGrid("ruins", spacing: 32);
            var sparse = new RegionGrid("ruins", spacing: 32, density: 0.3f);
            int kept = 0, total = 0;

            for (int regionZ = -12; regionZ <= 12; regionZ++)
            for (int regionX = -12; regionX <= 12; regionX++)
            {
                total++;
                if (!sparse.TryPlacement(Seed, regionX, regionZ, out var thinned)) continue;
                kept++;
                Assert.That(full.TryPlacement(Seed, regionX, regionZ, out var dense), Is.True);
                Assert.That(thinned.WorldX, Is.EqualTo(dense.WorldX));
                Assert.That(thinned.WorldZ, Is.EqualTo(dense.WorldZ));
            }

            float rate = kept / (float)total;
            Assert.That(rate, Is.InRange(0.2f, 0.4f), $"Roughly three in ten regions should survive, but {rate:P0} did.");
        }

        [Test]
        public void NamedGridsScatterIndependently()
        {
            var villages = new RegionGrid("villages", spacing: 64);
            var ruins = new RegionGrid("ruins", spacing: 64);
            int differences = 0;
            for (int region = 0; region < 64; region++)
            {
                villages.TryPlacement(Seed, region, 0, out var village);
                ruins.TryPlacement(Seed, region, 0, out var ruin);
                if (village.WorldX != ruin.WorldX) differences++;
            }
            Assert.That(differences, Is.GreaterThan(32));
        }

        /// <summary>
        /// The property that lets a chunk draw a neighbour's structure without generating it: asking
        /// around a point finds every placement that could reach it.
        /// </summary>
        [Test]
        public void AskingAroundAPointFindsEveryPlacementInRange()
        {
            var grid = new RegionGrid("villages", spacing: 32);
            var found = new List<RegionPlacement>();
            grid.Around(Seed, worldX: 100, worldZ: 100, blockRadius: 64, placement => found.Add(placement));

            Assert.That(found.Count, Is.GreaterThan(0));
            foreach (var placement in found)
            {
                Assert.That(placement.RegionX, Is.InRange(grid.RegionOf(36), grid.RegionOf(164)));
                Assert.That(placement.RegionZ, Is.InRange(grid.RegionOf(36), grid.RegionOf(164)));
            }

            //every region in the window is represented, so nothing that could reach the point is missed.
            int expected = (grid.RegionOf(164) - grid.RegionOf(36) + 1);
            Assert.That(found.Count, Is.EqualTo(expected * expected));
        }

        [Test]
        public void JitterBeyondTheRegionSizeIsRejected()
            => Assert.Throws<System.ArgumentOutOfRangeException>(() => new RegionGrid("bad", spacing: 16, jitter: 32));
    }
}
