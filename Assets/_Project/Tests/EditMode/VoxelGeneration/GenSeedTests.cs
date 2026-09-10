using NUnit.Framework;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed class GenSeedTests
    {
        //the published splitmix64 test vector. If this moves, every world in existence has changed,
        //so it is worth pinning against the reference algorithm rather than against ourselves.
        [Test]
        public void SplitMixMatchesTheReferenceVector()
            => Assert.That(GenHash.SplitMix64(0UL), Is.EqualTo(0xE220A8397B1DCDAFUL));

        //these two pin our own composition of that primitive. They exist to fail loudly if the
        //derivation is ever "tidied", which would silently regenerate every saved world.
        [Test]
        public void TextHashIsStable()
            => Assert.That(GenHash.Text("overworld"), Is.EqualTo(0x560BFFBA7CB5BD49UL));

        [Test]
        public void NamedDerivationIsStable()
            => Assert.That(new GenSeed(12345UL).Derive("overworld").Value, Is.EqualTo(0x1D84BBB5AAACE5AFUL));

        [Test]
        public void DerivationIsRepeatable()
        {
            var seed = new GenSeed(987654321UL);
            Assert.That(seed.Derive("continents").Value, Is.EqualTo(seed.Derive("continents").Value));
        }

        [Test]
        public void DifferentNamesGiveDifferentStreams()
        {
            var seed = new GenSeed(42UL);
            Assert.That(seed.Derive("continents").Value, Is.Not.EqualTo(seed.Derive("erosion").Value));
        }

        [Test]
        public void DifferentWorldSeedsGiveDifferentStreams()
        {
            Assert.That(new GenSeed(1UL).Derive("continents").Value,
                Is.Not.EqualTo(new GenSeed(2UL).Derive("continents").Value));
        }

        /// <summary>
        /// The reason derivation is by name and not by index: a generator that gains a node must not
        /// reseed the nodes that were already there, or every existing world silently changes shape.
        /// </summary>
        [Test]
        public void InsertingANodeLeavesItsSiblingsSeededTheSame()
        {
            var world = new GenSeed(77777UL);
            var before = new[] { world.Derive("continents"), world.Derive("hills") };
            var after = new[] { world.Derive("continents"), world.Derive("erosion"), world.Derive("hills") };
            Assert.That(after[0].Value, Is.EqualTo(before[0].Value));
            Assert.That(after[2].Value, Is.EqualTo(before[1].Value));
        }

        [Test]
        public void ZeroResolvesToARandomNonZeroSeed()
        {
            var first = GenSeed.Resolve(0UL);
            Assert.That(first.Value, Is.Not.Zero);
            //a resolved seed is a real seed: resolving it again must give it back unchanged.
            Assert.That(GenSeed.Resolve(first.Value).Value, Is.EqualTo(first.Value));
        }

        [Test]
        public void ZeroResolvesDifferentlyEachTime()
        {
            //two draws colliding would mean the entropy source is not varying at all.
            var values = new System.Collections.Generic.HashSet<ulong>();
            for (int attempt = 0; attempt < 16; attempt++) values.Add(GenSeed.Resolve(0UL).Value);
            Assert.That(values.Count, Is.GreaterThan(1));
        }

        [Test]
        public void NonZeroSeedsResolveToThemselves()
            => Assert.That(GenSeed.Resolve(12345UL).Value, Is.EqualTo(12345UL));

        [Test]
        public void NumericTextParsesAsThatNumber()
        {
            Assert.That(GenSeed.Parse("12345").Value, Is.EqualTo(12345UL));
            Assert.That(GenSeed.Parse("  -1 ").Value, Is.EqualTo(ulong.MaxValue));
        }

        [Test]
        public void NonNumericTextHashesToASeed()
            => Assert.That(GenSeed.Parse("glacier").Value, Is.EqualTo(GenHash.Text("glacier")));

        [Test]
        public void EmptyTextRandomizes()
            => Assert.That(GenSeed.Parse("   ").Value, Is.Not.Zero);
    }
}
