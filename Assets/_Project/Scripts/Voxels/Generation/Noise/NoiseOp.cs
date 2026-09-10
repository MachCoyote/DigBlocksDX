namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// The instruction set a compiled noise program runs. Deliberately small: every entry is exact or
    /// correctly rounded under IEEE-754, because a world seed has to reproduce bit-identically on
    /// every platform. Nothing transcendental may be added here.
    /// </summary>
    public enum NoiseOpCode : byte
    {
        Const,
        CoordX, CoordY, CoordZ,
        Add, Sub, Mul, Div, Min, Max,
        Abs, Neg, Floor, Sqrt,
        /// <summary>A clamped to the constant range [P0, P1].</summary>
        Clamp,
        /// <summary>A + C * (B - A).</summary>
        Lerp,
        /// <summary>A &lt; B ? C : D. The one branch in the set, and the seam biome selection will use.</summary>
        Select,
        Perlin2, Perlin3, Value2, Value3,
        /// <summary>A mapped through the knot run at Table.</summary>
        Spline,
        /// <summary>A quantized to P0 steps per unit, softened by P1.</summary>
        Terrace,
        /// <summary>An author-supplied Burst function: externals[Table](x, y, z, Seed, A, B).</summary>
        CallExternal
    }

    /// <summary>How a spline interpolates between its knots.</summary>
    public enum SplineInterpolation : byte
    {
        /// <summary>Straight segments. Predictable, but its slope breaks at every knot.</summary>
        Linear = 0,
        /// <summary>Monotone cubic Hermite. Smooth through the knots and never overshoots them.</summary>
        Smooth = 1
    }

    /// <summary>
    /// One instruction. Operands are slot indices into the batch's scratch; <see cref="Dst"/> is where
    /// the result lands. Slots are reused once their last consumer has run, so a large program still
    /// evaluates out of a small working set.
    /// </summary>
    public struct NoiseOp
    {
        public NoiseOpCode Code;
        /// <summary>Fractal mode for a noise op, interpolation for a spline.</summary>
        public byte Mode;
        /// <summary>Octave count for a noise op, knot count for a spline.</summary>
        public ushort Count;
        public int Dst, A, B, C, D;
        /// <summary>Lacunarity, clamp low, terrace steps, or a constant's value.</summary>
        public float P0;
        /// <summary>Gain, clamp high, or terrace smoothing.</summary>
        public float P1;
        /// <summary>The node's derived seed, for noise and external ops.</summary>
        public uint Seed;
        /// <summary>Knot run offset for a spline, registry index for an external call.</summary>
        public int Table;
    }

    /// <summary>One spline control point. Tangents are solved when the program compiles, not per sample.</summary>
    public struct SplineKnot
    {
        public float X, Y, Tangent;
    }
}
