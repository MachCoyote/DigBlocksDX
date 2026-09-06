using System;
using NUnit.Framework;

namespace DigBlocks.Voxels.Tests
{
    public sealed class BlockRegistryTests
    {
        private static StateDefinition Define(string key, bool permitsFluid = false, string behavior = "digblocks:static",
            string model = "digblocks:cube", params string[] tags)
            => new StateDefinition(key, behavior, model, tags, permitsFluid);

        [Test]
        public void RegistryReservesEmptyIdsAndAssignsRemainingStatesByOrdinalKey()
        {
            var registry = new BlockRegistry(new[] { Define("digblocks:stone"), Define("digblocks:air", true),
                Define("digblocks:dirt") }, new[] { Define("digblocks:water"), Define("digblocks:empty") });
            Assert.That(registry.LookupSolid("digblocks:air"), Is.Zero);
            Assert.That(registry.LookupFluid("digblocks:empty"), Is.Zero);
            Assert.That(registry.LookupSolid("digblocks:dirt"), Is.EqualTo(1));
            Assert.That(registry.LookupSolid("digblocks:stone"), Is.EqualTo(2));
            Assert.That(registry.MaxSolidStateId, Is.EqualTo(2));
            Assert.That(registry.MaxFluidStateId, Is.EqualTo(1));
            Assert.That(registry.GetSolid(0).PermitsFluid, Is.True);
        }

        [Test]
        public void FiniteStatePropertiesHaveCanonicalIdentityIndependentOfInputOrder()
        {
            var definition = Define("digblocks:stairs[half=bottom,facing=north]");
            Assert.That(definition.Key, Is.EqualTo("digblocks:stairs[facing=north,half=bottom]"));
        }

        [Test]
        public void FingerprintAndIdsIgnoreInputAndTagOrderButCoverDefinitionRules()
        {
            var first = new BlockRegistry(new[] { Define("digblocks:air", true),
                Define("digblocks:stone", false, "digblocks:static", "digblocks:cube", "digblocks:rock", "digblocks:mineable") },
                new[] { Define("digblocks:empty"), Define("digblocks:water") });
            var reordered = new BlockRegistry(new[] {
                Define("digblocks:stone", false, "digblocks:static", "digblocks:cube", "digblocks:mineable", "digblocks:rock"),
                Define("digblocks:air", true) }, new[] { Define("digblocks:water"), Define("digblocks:empty") });
            Assert.That(reordered.Fingerprint, Is.EqualTo(first.Fingerprint));
            Assert.That(first.Fingerprint, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(reordered.LookupSolid("digblocks:stone"), Is.EqualTo(first.LookupSolid("digblocks:stone")));
            foreach (var changedStone in new[] {
                Define("digblocks:stone", true, "digblocks:static", "digblocks:cube", "digblocks:rock", "digblocks:mineable"),
                Define("digblocks:stone", false, "digblocks:falling", "digblocks:cube", "digblocks:rock", "digblocks:mineable"),
                Define("digblocks:stone", false, "digblocks:static", "digblocks:slab", "digblocks:rock", "digblocks:mineable"),
                Define("digblocks:stone", false, "digblocks:static", "digblocks:cube", "digblocks:mineable") })
            {
                var changed = new BlockRegistry(new[] { Define("digblocks:air", true), changedStone },
                    new[] { Define("digblocks:empty"), Define("digblocks:water") });
                Assert.That(changed.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
            }
            var changedFluid = new BlockRegistry(new[] { first.GetSolid(0), first.GetSolid(1) },
                new[] { Define("digblocks:empty"), Define("digblocks:water", false, "digblocks:flow") });
            Assert.That(changedFluid.Fingerprint, Is.Not.EqualTo(first.Fingerprint));
        }

        [Test]
        public void DefinitionsAndRegistryDetachMutableInputCollections()
        {
            var tags = new[] { "digblocks:rock" };
            var definition = new StateDefinition("digblocks:stone", "digblocks:static", "digblocks:cube", tags, false);
            tags[0] = "digblocks:modified";
            var solids = new[] { Define("digblocks:air", true), definition };
            var registry = new BlockRegistry(solids, new[] { Define("digblocks:empty") });
            solids[1] = Define("digblocks:dirt");
            Assert.That(registry.GetSolid(1).Key, Is.EqualTo("digblocks:stone"));
            Assert.That(registry.GetSolid(1).Tags[0], Is.EqualTo("digblocks:rock"));
            Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<string>)definition.Tags)[0] = "digblocks:changed");
        }

        [TestCase("stone")]
        [TestCase("Digblocks:stone")]
        [TestCase("digblocks:stone[]")]
        [TestCase("digblocks:stairs[facing=north,facing=south]")]
        [TestCase("digblocks:stairs[facing=]")]
        [TestCase("digblocks:stairs[facing=north,]")]
        [TestCase("digblocks:stairs[facing=north]extra")]
        [TestCase("digblocks:stairs[ facing=north]")]
        [TestCase(null)]
        public void DefinitionsRejectInvalidKeysAndProperties(string key)
        {
            Assert.Throws<ArgumentException>(() => Define(key));
        }

        [Test]
        public void DefinitionsRejectInvalidBehaviorModelAndTags()
        {
            Assert.Throws<ArgumentException>(() => Define("digblocks:stone", false, "static"));
            Assert.Throws<ArgumentException>(() => Define("digblocks:stone", false, "digblocks:static", "cube"));
            Assert.Throws<ArgumentException>(() => Define("digblocks:stone", false, "digblocks:static", "digblocks:cube", "rock"));
            Assert.Throws<ArgumentException>(() => Define("digblocks:stone", false, "digblocks:static", "digblocks:cube", "digblocks:rock", "digblocks:rock"));
            Assert.Throws<ArgumentNullException>(() => new StateDefinition("digblocks:stone", "digblocks:static", "digblocks:cube", null, false));
        }

        [Test]
        public void RegistryRejectsMissingReservedEntriesDuplicatesAndNullDefinitions()
        {
            Assert.Throws<ArgumentException>(() => new BlockRegistry(new[] { Define("digblocks:stone") },
                new[] { Define("digblocks:empty") }));
            Assert.Throws<ArgumentException>(() => new BlockRegistry(new[] { Define("digblocks:air") },
                new[] { Define("digblocks:water") }));
            Assert.Throws<ArgumentException>(() => new BlockRegistry(new[] { Define("digblocks:air"), Define("digblocks:air") },
                new[] { Define("digblocks:empty") }));
            Assert.Throws<ArgumentException>(() => new BlockRegistry(new[] { Define("digblocks:air"), null },
                new[] { Define("digblocks:empty") }));
            Assert.Throws<ArgumentNullException>(() => new BlockRegistry(null, new[] { Define("digblocks:empty") }));
        }

        [Test]
        public void DummyRegistryExposesSolidFluidRulesAndRejectsUnknownLookups()
        {
            var registry = BlockRegistry.CreateDummy();
            Assert.That(registry.GetSolid(registry.LookupSolid("digblocks:air")).PermitsFluid, Is.True);
            Assert.That(registry.GetSolid(registry.LookupSolid("digblocks:stone")).PermitsFluid, Is.False);
            Assert.That(registry.LookupFluid("digblocks:water"), Is.GreaterThan(0));
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => registry.LookupSolid("digblocks:missing"));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetSolid(registry.MaxSolidStateId + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetFluid(registry.MaxFluidStateId + 1));
        }
    }
}
