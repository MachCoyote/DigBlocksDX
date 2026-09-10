using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation.Tests
{
    public sealed class NoiseProgramTests
    {
        private static readonly GenSeed Seed = new GenSeed(0xC0FFEEUL);
        private readonly List<NoiseProgram> programs = new List<NoiseProgram>();

        private NoiseProgram Compile(NoiseExpr expression)
        {
            var program = NoiseProgram.Compile(expression, Seed);
            programs.Add(program);
            return program;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var program in programs) program.Dispose();
            programs.Clear();
        }

        [Test]
        public void ArithmeticEvaluatesAsWritten()
        {
            var program = Compile((NoiseExpr.X * 2f + NoiseExpr.Z) / 4f);
            Assert.That(program.Sample(3f, 5f), Is.EqualTo((3f * 2f + 5f) / 4f));
        }

        [Test]
        public void CoordinatesReachTheirOwnAxes()
        {
            var program = Compile(NoiseExpr.X * 100f + NoiseExpr.Y * 10f + NoiseExpr.Z);
            Assert.That(program.Sample(1f, 2f, 3f), Is.EqualTo(123f));
        }

        [Test]
        public void UnaryOperationsEvaluateAsWritten()
        {
            Assert.That(Compile(NoiseExpr.X.Abs()).Sample(-7f, 0f), Is.EqualTo(7f));
            Assert.That(Compile((-NoiseExpr.X)).Sample(7f, 0f), Is.EqualTo(-7f));
            Assert.That(Compile(NoiseExpr.X.Floor()).Sample(-2.25f, 0f), Is.EqualTo(-3f));
            Assert.That(Compile(NoiseExpr.X.Sqrt()).Sample(9f, 0f), Is.EqualTo(3f));
            Assert.That(Compile(NoiseExpr.X.Min(NoiseExpr.Z)).Sample(4f, 9f), Is.EqualTo(4f));
            Assert.That(Compile(NoiseExpr.X.Max(NoiseExpr.Z)).Sample(4f, 9f), Is.EqualTo(9f));
            Assert.That(Compile(NoiseExpr.X.Clamp(-1f, 1f)).Sample(5f, 0f), Is.EqualTo(1f));
            Assert.That(Compile(NoiseExpr.X.Lerp(NoiseExpr.Z, 0.25f)).Sample(0f, 8f), Is.EqualTo(2f));
        }

        [Test]
        public void RemapRescalesTheRange()
        {
            var program = Compile(NoiseExpr.X.Remap(-1f, 1f, 0f, 64f));
            Assert.That(program.Sample(-1f, 0f), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(program.Sample(0f, 0f), Is.EqualTo(32f).Within(1e-4f));
            Assert.That(program.Sample(1f, 0f), Is.EqualTo(64f).Within(1e-4f));
        }

        [Test]
        public void SelectChoosesOnTheThreshold()
        {
            var program = Compile(NoiseExpr.Select(NoiseExpr.X, 0f, -5f, 5f));
            Assert.That(program.Sample(-0.001f, 0f), Is.EqualTo(-5f));
            Assert.That(program.Sample(0f, 0f), Is.EqualTo(5f));
        }

        [Test]
        public void TerraceQuantizesAndSmoothingReturnsTheField()
        {
            var stairs = Compile(NoiseExpr.X.Terrace(4f));
            Assert.That(stairs.Sample(0.6f, 0f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(stairs.Sample(0.9f, 0f), Is.EqualTo(0.75f).Within(1e-5f));

            var untouched = Compile(NoiseExpr.X.Terrace(4f, 1f));
            Assert.That(untouched.Sample(0.6f, 0f), Is.EqualTo(0.6f).Within(1e-5f));
        }

        [Test]
        public void ASplinePassesThroughItsKnots()
        {
            var program = Compile(NoiseExpr.X.Spline((-1f, -20f), (0f, 4f), (0.5f, 30f), (1f, 90f)));
            Assert.That(program.Sample(-1f, 0f), Is.EqualTo(-20f).Within(1e-3f));
            Assert.That(program.Sample(0f, 0f), Is.EqualTo(4f).Within(1e-3f));
            Assert.That(program.Sample(0.5f, 0f), Is.EqualTo(30f).Within(1e-3f));
            Assert.That(program.Sample(1f, 0f), Is.EqualTo(90f).Within(1e-3f));
        }

        [Test]
        public void ASplineHoldsItsEndsOutsideTheAuthoredDomain()
        {
            var program = Compile(NoiseExpr.X.Spline((-1f, -20f), (1f, 90f)));
            Assert.That(program.Sample(-40f, 0f), Is.EqualTo(-20f));
            Assert.That(program.Sample(40f, 0f), Is.EqualTo(90f));
        }

        /// <summary>
        /// Overshoot in a height curve is a spike the author never asked for, which is the whole reason
        /// the tangents are solved for monotonicity rather than taken as plain finite differences.
        /// </summary>
        [Test]
        public void ASmoothSplineNeverOvershootsItsKnots()
        {
            var program = Compile(NoiseExpr.X.Spline((-1f, 0f), (-0.2f, 2f), (0f, 2f), (0.4f, 40f), (1f, 42f)));
            for (int step = 0; step <= 400; step++)
            {
                float input = -1f + step * (2f / 400f);
                Assert.That(program.Sample(input, 0f), Is.InRange(-0.01f, 42.01f));
            }
        }

        [Test]
        public void ALinearSplineInterpolatesStraight()
        {
            var program = Compile(NoiseExpr.X.Spline(Spline.Linear((0f, 0f), (1f, 10f))));
            Assert.That(program.Sample(0.25f, 0f), Is.EqualTo(2.5f).Within(1e-4f));
            Assert.That(program.Sample(0.75f, 0f), Is.EqualTo(7.5f).Within(1e-4f));
        }

        [Test]
        public void ASplineIsMonotoneWhereItsKnotsAre()
        {
            var program = Compile(NoiseExpr.X.Spline((-1f, 0f), (0f, 10f), (1f, 12f)));
            float previous = float.NegativeInfinity;
            for (int step = 0; step <= 200; step++)
            {
                float value = program.Sample(-1f + step * 0.01f, 0f);
                Assert.That(value, Is.GreaterThanOrEqualTo(previous - 1e-4f));
                previous = value;
            }
        }

        [Test]
        public void NoiseMatchesTheUnderlyingField()
        {
            var program = Compile(NoiseExpr.Perlin2D("field", NoiseExpr.X, NoiseExpr.Z, 1f, 3));
            uint expectedSeed = Seed.Derive("field").Lattice;
            Assert.That(program.Sample(1.25f, -3.5f),
                Is.EqualTo(GenNoise.Fbm2(expectedSeed, 1.25f, -3.5f, 3, 2f, 0.5f, FbmMode.Standard)));
        }

        [Test]
        public void FrequencyScalesTheSampledCoordinates()
        {
            var scaled = Compile(NoiseExpr.Perlin2D("field", NoiseExpr.X, NoiseExpr.Z, 0.25f, 2));
            var manual = Compile(NoiseExpr.Perlin2D("field", NoiseExpr.X * 0.25f, NoiseExpr.Z * 0.25f, 1f, 2));
            Assert.That(scaled.Sample(9f, 5f), Is.EqualTo(manual.Sample(9f, 5f)));
        }

        [Test]
        public void NodeNamesSelectIndependentFields()
        {
            var first = Compile(NoiseExpr.Perlin2D("continents", NoiseExpr.X, NoiseExpr.Z, 0.05f, 2));
            var second = Compile(NoiseExpr.Perlin2D("erosion", NoiseExpr.X, NoiseExpr.Z, 0.05f, 2));
            Assert.That(first.Sample(11f, 13f), Is.Not.EqualTo(second.Sample(11f, 13f)));
        }

        [Test]
        public void TheSameExpressionAndSeedGiveTheSameField()
        {
            NoiseExpr Build() => NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 64f, 4) * 20f + 64f;
            var first = Compile(Build());
            var second = Compile(Build());
            for (int index = 0; index < 64; index++)
            {
                float x = index * 3.7f, z = index * -1.9f;
                Assert.That(first.Sample(x, z), Is.EqualTo(second.Sample(x, z)));
            }
        }

        [Test]
        public void DifferentWorldSeedsGiveDifferentFields()
        {
            var expression = NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 64f, 4);
            using (var first = NoiseProgram.Compile(expression, new GenSeed(1UL)))
            using (var second = NoiseProgram.Compile(expression, new GenSeed(2UL)))
                Assert.That(first.Sample(17f, 23f), Is.Not.EqualTo(second.Sample(17f, 23f)));
        }

        [Test]
        public void WarpingMovesTheFieldWithoutBreakingIt()
        {
            var plain = Compile(NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 48f, 3));
            var (wx, wz) = NoiseExpr.Warp2D("warp", NoiseExpr.X, NoiseExpr.Z, 1f / 90f, 24f);
            var warped = Compile(NoiseExpr.Perlin2D("hills", wx, wz, 1f / 48f, 3));

            bool moved = false;
            for (int index = 0; index < 128; index++)
            {
                float x = index * 5.3f, z = index * 2.1f;
                float sample = warped.Sample(x, z);
                Assert.That(sample, Is.InRange(-1f, 1f));
                if (sample != plain.Sample(x, z)) moved = true;
            }
            Assert.That(moved, Is.True);
        }

        [Test]
        public void ThreeDimensionalFieldsReportThatTheyNeedY()
        {
            Assert.That(Compile(NoiseExpr.Perlin3D("density", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 0.1f)).UsesY, Is.True);
            Assert.That(Compile(NoiseExpr.Perlin2D("height", NoiseExpr.X, NoiseExpr.Z, 0.1f)).UsesY, Is.False);
        }

        [Test]
        public void ValueNoiseVariantsEvaluate()
        {
            Assert.That(Compile(NoiseExpr.Value2D("v2", NoiseExpr.X, NoiseExpr.Z, 0.3f, 2)).Sample(4f, 6f), Is.InRange(-1f, 1f));
            Assert.That(Compile(NoiseExpr.Value3D("v3", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 0.3f, 2)).Sample(4f, 5f, 6f), Is.InRange(-1f, 1f));
        }

        /// <summary>A subexpression named once in C# and used many times must cost one instruction, not many.</summary>
        [Test]
        public void SharedSubexpressionsCompileOnce()
        {
            var shared = NoiseExpr.Perlin2D("shared", NoiseExpr.X, NoiseExpr.Z, 0.1f, 3);
            var once = Compile(shared * 1f);
            var fourTimes = Compile(shared + shared + shared + shared);
            //three adds and a constant multiply differ, but the expensive sampler appears once either way.
            Assert.That(fourTimes.InstructionCount, Is.LessThan(once.InstructionCount + 4));
        }

        /// <summary>Two structurally identical trees built separately must also collapse to one.</summary>
        [Test]
        public void IdenticalSubexpressionsBuiltSeparatelyCollapse()
        {
            NoiseExpr Field() => NoiseExpr.Perlin2D("shared", NoiseExpr.X, NoiseExpr.Z, 0.1f, 3);
            var duplicated = Compile(Field() + Field());
            var shared = NoiseExpr.Perlin2D("shared", NoiseExpr.X, NoiseExpr.Z, 0.1f, 3);
            var explicitlyShared = Compile(shared + shared);
            Assert.That(duplicated.InstructionCount, Is.EqualTo(explicitlyShared.InstructionCount));
            Assert.That(duplicated.Sample(3f, 7f), Is.EqualTo(explicitlyShared.Sample(3f, 7f)));
        }

        /// <summary>Slots are recycled, so a long chain must not need one buffer per instruction.</summary>
        [Test]
        public void SlotsAreRecycledAlongAChain()
        {
            var expression = NoiseExpr.X;
            for (int step = 0; step < 40; step++) expression = expression * 1.01f + 0.5f;
            var program = Compile(expression);
            Assert.That(program.InstructionCount, Is.GreaterThan(60));
            Assert.That(program.SlotCount, Is.LessThan(8));
        }

        [Test]
        public void RecyclingSlotsDoesNotChangeResults()
        {
            var chain = NoiseExpr.Perlin2D("base", NoiseExpr.X, NoiseExpr.Z, 0.05f, 3);
            for (int step = 0; step < 12; step++) chain = (chain * 1.1f + 0.25f).Clamp(-4f, 4f);
            var program = Compile(chain);

            float expected = GenNoise.Fbm2(Seed.Derive("base").Lattice, 2f * 0.05f, 3f * 0.05f, 3, 2f, 0.5f, FbmMode.Standard);
            for (int step = 0; step < 12; step++) expected = math.min(math.max(expected * 1.1f + 0.25f, -4f), 4f);
            Assert.That(program.Sample(2f, 3f), Is.EqualTo(expected));
        }

        /// <summary>
        /// The Burst kernel and the managed kernel are the same source, so any difference here is a
        /// codegen difference, which is precisely the thing cross-platform seed reproducibility cannot
        /// tolerate.
        /// </summary>
        [Test]
        public void TheBurstKernelAgreesWithTheManagedKernel()
        {
            var (wx, wz) = NoiseExpr.Warp2D("warp", NoiseExpr.X, NoiseExpr.Z, 1f / 130f, 20f);
            var expression = 64f
                + NoiseExpr.Perlin2D("continents", wx, wz, 1f / 400f, 4)
                    .Spline((-1f, -24f), (-0.1f, 2f), (0.3f, 18f), (1f, 70f))
                + NoiseExpr.Perlin2D("hills", wx, wz, 1f / 70f, 3, mode: FbmMode.Ridged) * 12f
                + NoiseExpr.Value2D("detail", NoiseExpr.X, NoiseExpr.Z, 1f / 17f, 2).Terrace(3f, 0.4f);
            var program = Compile(expression);

            for (int index = 0; index < 512; index++)
            {
                float x = index * 7.31f - 900f, z = index * -3.17f + 400f;
                Assert.That(program.Sample(x, z, burst: true), Is.EqualTo(program.Sample(x, z, burst: false)),
                    $"Burst and managed evaluation diverged at ({x}, {z}).");
            }
        }

        [Test]
        public void BatchEvaluationMatchesSampling()
        {
            var expression = NoiseExpr.Perlin2D("hills", NoiseExpr.X, NoiseExpr.Z, 1f / 40f, 3) * 18f + 64f;
            var program = Compile(expression);
            const int count = 256;

            using (var scratch = NoiseScratch.For(program, count))
            {
                unsafe
                {
                    for (int index = 0; index < count; index++)
                    {
                        scratch.X[index] = index * 1.7f - 100f;
                        scratch.Z[index] = index * -0.9f + 30f;
                    }
                    var batch = scratch.Batch(count, withY: false);
                    program.Evaluate(ref batch);
                    for (int index = 0; index < count; index++)
                        Assert.That(scratch.Result[index], Is.EqualTo(program.Sample(scratch.X[index], scratch.Z[index])));
                }
            }
        }

        [Test]
        public void AProgramThatSamplesYRejectsABatchWithoutIt()
        {
            var program = Compile(NoiseExpr.Perlin3D("density", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 0.1f));
            using (var scratch = NoiseScratch.For(program, 8))
            {
                var batch = scratch.Batch(8, withY: false);
                Assert.Throws<InvalidOperationException>(() => program.Evaluate(ref batch));
            }
        }

        [Test]
        public void SeededNodesRequireAName()
        {
            Assert.Throws<ArgumentException>(() => NoiseExpr.Perlin2D(" ", NoiseExpr.X, NoiseExpr.Z, 0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => NoiseExpr.Perlin2D("f", NoiseExpr.X, NoiseExpr.Z, 0.1f, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => NoiseExpr.Perlin2D("f", NoiseExpr.X, NoiseExpr.Z, 0.1f, 1, gain: 2f));
            Assert.Throws<ArgumentOutOfRangeException>(() => NoiseExpr.Perlin2D("f", NoiseExpr.X, NoiseExpr.Z, 0f));
        }

        [Test]
        public void SplinesRejectUnorderedKnots()
            => Assert.Throws<ArgumentException>(() => Spline.Smooth((1f, 0f), (0f, 1f)));

        [Test]
        public void SplinesRejectASingleKnot()
            => Assert.Throws<ArgumentException>(() => Spline.Smooth((0f, 1f)));
    }

    /// <summary>
    /// The hand-written escape hatch: a Burst function an expression can call by id, for anything the
    /// instruction set does not express.
    /// </summary>
    [BurstCompile]
    public sealed class NoiseExternalTests
    {
        private const string Id = "digblocks.tests:ramp";

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(NoiseExternal))]
        private static float Ramp(float x, float y, float z, uint seed, float a, float b)
            => x * a + z * b + (seed & 1u);

        [Test]
        public void ARegisteredBurstFunctionIsCallableFromAnExpression()
        {
            NoiseExternalRegistry.Register(Id, Ramp);
            Assert.That(NoiseExternalRegistry.Contains(Id), Is.True);

            var expression = NoiseExpr.External(Id, "ramp", 2f, 3f);
            using (var program = NoiseProgram.Compile(expression, new GenSeed(99UL)))
            {
                uint seed = new GenSeed(99UL).Derive("ramp").Lattice;
                float expected = 5f * 2f + 7f * 3f + (seed & 1u);
                Assert.That(program.Sample(5f, 0f, 7f), Is.EqualTo(expected));
                Assert.That(program.Sample(5f, 0f, 7f, burst: false), Is.EqualTo(expected));
            }
        }

        [Test]
        public void AnExternalComposesWithOrdinaryOperations()
        {
            NoiseExternalRegistry.Register(Id, Ramp);
            var expression = NoiseExpr.External(Id, "ramp", 1f, 0f) * 10f;
            using (var program = NoiseProgram.Compile(expression, new GenSeed(4UL)))
            {
                uint seed = new GenSeed(4UL).Derive("ramp").Lattice;
                Assert.That(program.Sample(3f, 0f, 0f), Is.EqualTo((3f + (seed & 1u)) * 10f));
            }
        }

        [Test]
        public void AnUnregisteredExternalFailsWhenTheProgramIsBuilt()
        {
            var expression = NoiseExpr.External("digblocks.tests:missing", "absent");
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (NoiseProgram.Compile(expression, new GenSeed(1UL))) { }
            });
        }
    }
}
