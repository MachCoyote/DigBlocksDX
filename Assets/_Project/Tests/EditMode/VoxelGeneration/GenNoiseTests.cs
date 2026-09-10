using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed class GenNoiseTests
    {
        private const uint Seed = 0x5EEDU;

        /// <summary>
        /// Gradient noise is exactly zero on its integer lattice. This is the cheapest structural
        /// check that the fade, the lattice hashing and the corner offsets all line up.
        /// </summary>
        [Test]
        public void GradientNoiseIsZeroOnTheLattice()
        {
            for (int y = -4; y <= 4; y++)
            for (int x = -4; x <= 4; x++)
                Assert.That(GenNoise.Perlin2(Seed, x, y), Is.EqualTo(0f));

            for (int z = -2; z <= 2; z++)
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
                Assert.That(GenNoise.Perlin3(Seed, x, y, z), Is.EqualTo(0f));
        }

        [Test]
        public void GradientNoiseStaysWithinTheNormalizedRange()
        {
            float widest = 0f;
            for (int y = 0; y < 160; y++)
            for (int x = 0; x < 160; x++)
            {
                float sample = GenNoise.Perlin2(Seed, x * 0.137f - 11f, y * 0.137f + 7f);
                Assert.That(sample, Is.InRange(-1f, 1f));
                widest = math.max(widest, math.abs(sample));
            }
            //a field that never leaves a narrow band would mean the normalization is far too cautious.
            Assert.That(widest, Is.GreaterThan(0.5f));
        }

        [Test]
        public void ThreeDimensionalGradientNoiseStaysWithinTheNormalizedRange()
        {
            for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                Assert.That(GenNoise.Perlin3(Seed, x * 0.213f, y * 0.213f, z * 0.213f), Is.InRange(-1f, 1f));
        }

        [Test]
        public void ValueNoiseStaysWithinTheNormalizedRange()
        {
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                Assert.That(GenNoise.Value2(Seed, x * 0.31f, y * 0.31f), Is.InRange(-1f, 1f));
                Assert.That(GenNoise.Value3(Seed, x * 0.31f, y * 0.31f, x * 0.17f), Is.InRange(-1f, 1f));
            }
        }

        [Test]
        public void SamplingIsRepeatable()
        {
            Assert.That(GenNoise.Perlin2(Seed, 12.25f, -3.5f), Is.EqualTo(GenNoise.Perlin2(Seed, 12.25f, -3.5f)));
            Assert.That(GenNoise.Perlin3(Seed, 1.5f, 2.25f, -8.125f), Is.EqualTo(GenNoise.Perlin3(Seed, 1.5f, 2.25f, -8.125f)));
        }

        [Test]
        public void SeedsProduceIndependentFields()
        {
            float difference = 0f;
            for (int index = 0; index < 512; index++)
            {
                float x = index * 0.37f, y = index * -0.21f;
                difference += math.abs(GenNoise.Perlin2(1u, x, y) - GenNoise.Perlin2(2u, x, y));
            }
            Assert.That(difference / 512f, Is.GreaterThan(0.1f));
        }

        /// <summary>
        /// Neighbouring voxels sample noise a fraction of a lattice cell apart. If the field were not
        /// continuous there, terrain would tear at exactly that scale.
        /// </summary>
        [Test]
        public void TheFieldIsContinuous()
        {
            const float step = 1f / 64f;
            for (int index = 0; index < 400; index++)
            {
                float x = index * 0.093f - 5f, y = index * -0.061f + 3f;
                float here = GenNoise.Perlin2(Seed, x, y);
                Assert.That(math.abs(GenNoise.Perlin2(Seed, x + step, y) - here), Is.LessThan(0.25f));
                Assert.That(math.abs(GenNoise.Perlin2(Seed, x, y + step) - here), Is.LessThan(0.25f));
            }
        }

        [Test]
        public void FractalSumsStayWithinTheNormalizedRange([Values(FbmMode.Standard, FbmMode.Ridged, FbmMode.Billow)] FbmMode mode)
        {
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                Assert.That(GenNoise.Fbm2(Seed, x * 0.11f, y * 0.11f, 5, 2f, 0.5f, mode), Is.InRange(-1f, 1f));
                Assert.That(GenNoise.FbmValue2(Seed, x * 0.11f, y * 0.11f, 4, 2f, 0.5f, mode), Is.InRange(-1f, 1f));
            }

            for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Assert.That(GenNoise.Fbm3(Seed, x * 0.23f, y * 0.23f, z * 0.23f, 4, 2f, 0.5f, mode), Is.InRange(-1f, 1f));
                Assert.That(GenNoise.FbmValue3(Seed, x * 0.23f, y * 0.23f, z * 0.23f, 3, 2f, 0.5f, mode), Is.InRange(-1f, 1f));
            }
        }

        [Test]
        public void ASingleOctaveFractalSumIsThePlainField()
        {
            //one octave divides by an amplitude of one, so it must pass the sample through untouched.
            float direct = GenNoise.Perlin2(GenHash.Mix32(Seed), 3.75f, -1.25f);
            Assert.That(GenNoise.Fbm2(Seed, 3.75f, -1.25f, 1, 2f, 0.5f, FbmMode.Standard), Is.EqualTo(direct));
        }

        [Test]
        public void ZeroOctavesIsFlat()
            => Assert.That(GenNoise.Fbm2(Seed, 4.5f, 9.5f, 0, 2f, 0.5f, FbmMode.Standard), Is.EqualTo(0f));

        [Test]
        public void RidgedSumsCrestWhereThePlainFieldCrossesZero()
        {
            //a ridged octave maps |sample| = 0 to +1, so its highest values sit on the zero contour.
            float atLattice = GenNoise.Fbm2(Seed, 5f, 5f, 1, 2f, 0.5f, FbmMode.Ridged);
            Assert.That(atLattice, Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void MoreOctavesAddDetailWithoutMovingTheBroadShape()
        {
            //the first octave dominates a halving gain, so five octaves must still track one closely.
            float coarse = GenNoise.Fbm2(Seed, 2.3f, 7.1f, 1, 2f, 0.5f, FbmMode.Standard);
            float detailed = GenNoise.Fbm2(Seed, 2.3f, 7.1f, 5, 2f, 0.5f, FbmMode.Standard);
            Assert.That(detailed, Is.Not.EqualTo(coarse));
            Assert.That(math.abs(detailed - coarse), Is.LessThan(0.6f));
        }
    }
}
