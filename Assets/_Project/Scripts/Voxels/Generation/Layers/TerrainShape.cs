using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// The three control fields a layer's character is usually built from, sampled once and shared.
    /// <para>
    /// The point of separating them is that each answers a different question, and a spline turns each
    /// answer into height independently. Continentalness decides whether somewhere is ocean, coast or
    /// inland. Erosion decides whether inland means plateau or valley. Peaks and valleys decides how
    /// sharp the result is. One noise field scaled by an amplitude cannot express that a place is both
    /// far inland and heavily eroded; three can.
    /// </para>
    /// <para>
    /// These are also the fields a biome source will read, so terrain shape and biome agree by
    /// construction rather than by two separately tuned stacks happening to line up.
    /// </para>
    /// </summary>
    public sealed class TerrainShape
    {
        /// <summary>Low is ocean, high is deep inland. The broadest field, and the slowest to change.</summary>
        public NoiseExpr Continentalness { get; }
        /// <summary>High is worn flat, low is rugged. What separates a plateau from a mountain range.</summary>
        public NoiseExpr Erosion { get; }
        /// <summary>Local sharpness, folded so its crests are lines rather than blobs.</summary>
        public NoiseExpr PeaksAndValleys { get; }
        /// <summary>The warped coordinates the fields were sampled at, for anything sampled alongside them.</summary>
        public NoiseExpr X { get; }
        public NoiseExpr Z { get; }

        private TerrainShape(NoiseExpr continentalness, NoiseExpr erosion, NoiseExpr peaksAndValleys,
            NoiseExpr x, NoiseExpr z)
        {
            Continentalness = continentalness; Erosion = erosion; PeaksAndValleys = peaksAndValleys;
            X = x; Z = z;
        }

        /// <summary>
        /// Builds the three fields over warped coordinates. <paramref name="scale"/> multiplies every
        /// feature size at once, so a generator can expose one knob that makes a world broader or
        /// tighter without retuning each field.
        /// </summary>
        public static TerrainShape Create(string name, float scale = 1f, float warpStrength = 30f)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A terrain shape needs a name.", nameof(name));
            if (!(scale > 0f)) throw new ArgumentOutOfRangeException(nameof(scale));

            //warping is applied to the shared coordinates rather than to each field, so the three stay
            //registered with one another; warping them separately would decorrelate the very
            //relationships the splines are authored around.
            var (x, z) = warpStrength == 0f
                ? (NoiseExpr.X, NoiseExpr.Z)
                : NoiseExpr.Warp2D(name + ".warp", NoiseExpr.X, NoiseExpr.Z, 1f / (260f * scale), warpStrength * scale);

            //every field is spread before it is handed out, so all three genuinely span [-1, 1] and a
            //spline authored across that range is asked about all of it rather than only its middle.
            //Peaks is folded after spreading rather than sampled with a ridged fractal mode, because
            //folding a field that still crowds around zero puts nearly every column on a crest.
            return new TerrainShape(
                NoiseExpr.Perlin2D(name + ".continentalness", x, z, 1f / (900f * scale), 4).Spread(0.30f),
                NoiseExpr.Perlin2D(name + ".erosion", x, z, 1f / (420f * scale), 3).Spread(0.34f),
                NoiseExpr.Perlin2D(name + ".peaks", x, z, 1f / (110f * scale), 3).Spread(0.34f).Ridge(),
                x, z);
        }
    }
}
