using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One node of an authored noise field. Immutable, so a subexpression can be given a name in C#
    /// and reused freely; the compiler evaluates each distinct node once however many times it is
    /// referenced.
    /// <para>
    /// Coordinates are ordinary expressions here rather than an implicit input, which is what lets
    /// domain warping compose without a special case: a warped field is just a field sampled at
    /// coordinates that are themselves noise.
    /// </para>
    /// </summary>
    public sealed class NoiseExpr
    {
        internal readonly NoiseOpCode Code;
        internal readonly NoiseExpr A, B, C, D;
        internal readonly float P0, P1;
        internal readonly int Count;
        internal readonly byte Mode;
        internal readonly string Name;
        internal readonly Spline Curve;
        internal readonly string ExternalId;

        private NoiseExpr(NoiseOpCode code, NoiseExpr a = null, NoiseExpr b = null, NoiseExpr c = null,
            NoiseExpr d = null, float p0 = 0f, float p1 = 0f, int count = 0, byte mode = 0,
            string name = null, Spline curve = null, string externalId = null)
        {
            Code = code; A = a; B = b; C = c; D = d; P0 = p0; P1 = p1;
            Count = count; Mode = mode; Name = name; Curve = curve; ExternalId = externalId;
        }

        public static readonly NoiseExpr X = new NoiseExpr(NoiseOpCode.CoordX);
        public static readonly NoiseExpr Y = new NoiseExpr(NoiseOpCode.CoordY);
        public static readonly NoiseExpr Z = new NoiseExpr(NoiseOpCode.CoordZ);

        public static NoiseExpr Constant(float value) => new NoiseExpr(NoiseOpCode.Const, p0: value);
        public static implicit operator NoiseExpr(float value) => Constant(value);

        public static NoiseExpr operator +(NoiseExpr a, NoiseExpr b) => Binary(NoiseOpCode.Add, a, b);
        public static NoiseExpr operator -(NoiseExpr a, NoiseExpr b) => Binary(NoiseOpCode.Sub, a, b);
        public static NoiseExpr operator *(NoiseExpr a, NoiseExpr b) => Binary(NoiseOpCode.Mul, a, b);
        public static NoiseExpr operator /(NoiseExpr a, NoiseExpr b) => Binary(NoiseOpCode.Div, a, b);
        public static NoiseExpr operator -(NoiseExpr a) => new NoiseExpr(NoiseOpCode.Neg, Require(a, nameof(a)));

        public NoiseExpr Abs() => new NoiseExpr(NoiseOpCode.Abs, this);
        public NoiseExpr Floor() => new NoiseExpr(NoiseOpCode.Floor, this);
        public NoiseExpr Sqrt() => new NoiseExpr(NoiseOpCode.Sqrt, this);
        public NoiseExpr Min(NoiseExpr other) => Binary(NoiseOpCode.Min, this, other);
        public NoiseExpr Max(NoiseExpr other) => Binary(NoiseOpCode.Max, this, other);

        public NoiseExpr Clamp(float low, float high)
        {
            if (!(low <= high)) throw new ArgumentException("A clamp's low bound must not exceed its high bound.", nameof(low));
            return new NoiseExpr(NoiseOpCode.Clamp, this, p0: low, p1: high);
        }

        /// <summary>This blended toward <paramref name="other"/> by <paramref name="t"/>, as a + t * (b - a).</summary>
        public NoiseExpr Lerp(NoiseExpr other, NoiseExpr t)
            => new NoiseExpr(NoiseOpCode.Lerp, this, Require(other, nameof(other)), Require(t, nameof(t)));

        /// <summary>
        /// Rescales one range onto another. Lowered to a multiply and an add when it is built, so it
        /// costs nothing at sample time.
        /// </summary>
        public NoiseExpr Remap(float fromLow, float fromHigh, float toLow, float toHigh)
        {
            if (fromLow == fromHigh) throw new ArgumentException("A remap's source range is empty.", nameof(fromLow));
            float scale = (toHigh - toLow) / (fromHigh - fromLow);
            return (this - Constant(fromLow)) * Constant(scale) + Constant(toLow);
        }

        /// <summary>Noise in [-1, 1] rescaled to [0, 1]. The most common remap by far.</summary>
        public NoiseExpr Unsigned() => Remap(-1f, 1f, 0f, 1f);

        /// <summary>
        /// Spreads a field out over its full range without clipping it.
        /// <para>
        /// Fractal noise is an average of octaves, so its values crowd around zero and only brush the
        /// ends of [-1, 1]. A spline authored across that whole range would therefore only ever be
        /// asked about its middle. This maps the field through <c>x / (|x| + softness)</c>, which is
        /// smooth, monotone and never saturates: smaller <paramref name="softness"/> spreads harder.
        /// Apply it before authoring anything that reads the field as though it spanned [-1, 1].
        /// </para>
        /// </summary>
        public NoiseExpr Spread(float softness = 0.4f)
        {
            if (!(softness > 0f)) throw new ArgumentOutOfRangeException(nameof(softness));
            return this / (Abs() + Constant(softness));
        }

        /// <summary>
        /// Folds a field about zero, so what were its zero crossings become crests: <c>1 - 2|x|</c>.
        /// Ridges rather than blobs, which is what makes a mountain range read as a range.
        /// <para>
        /// Worth applying after <see cref="Spread"/> rather than using a ridged fractal mode. Folding a
        /// field that crowds around zero puts almost everything near the crest, which lifts the whole
        /// world instead of shaping it.
        /// </para>
        /// </summary>
        public NoiseExpr Ridge() => Constant(1f) - Abs() * Constant(2f);

        /// <summary>
        /// Maps this field through an authored curve, so a noise value's <em>meaning</em> is authored
        /// rather than merely scaled. This is what puts genuinely flat plains next to sharp mountains
        /// without needing a second noise field to choose between them.
        /// </summary>
        public NoiseExpr Spline(Spline curve)
            => new NoiseExpr(NoiseOpCode.Spline, this, curve: curve ?? throw new ArgumentNullException(nameof(curve)));

        public NoiseExpr Spline(params (float input, float output)[] knots)
            => Spline(Generation.Spline.Smooth(knots));

        /// <summary>
        /// Quantizes to <paramref name="stepsPerUnit"/> flat steps, softened by <paramref name="smoothing"/>
        /// in [0, 1] where 0 is a hard stair and 1 leaves the field untouched. Mesas and plateaus.
        /// </summary>
        public NoiseExpr Terrace(float stepsPerUnit, float smoothing = 0f)
        {
            if (!(stepsPerUnit > 0f)) throw new ArgumentOutOfRangeException(nameof(stepsPerUnit));
            if (!(smoothing >= 0f && smoothing <= 1f)) throw new ArgumentOutOfRangeException(nameof(smoothing));
            return new NoiseExpr(NoiseOpCode.Terrace, this, p0: stepsPerUnit, p1: smoothing);
        }

        /// <summary><paramref name="whenBelow"/> where this is less than <paramref name="threshold"/>, else <paramref name="whenAbove"/>.</summary>
        public static NoiseExpr Select(NoiseExpr value, NoiseExpr threshold, NoiseExpr whenBelow, NoiseExpr whenAbove)
            => new NoiseExpr(NoiseOpCode.Select, Require(value, nameof(value)), Require(threshold, nameof(threshold)),
                Require(whenBelow, nameof(whenBelow)), Require(whenAbove, nameof(whenAbove)));

        public static NoiseExpr Perlin2D(string name, NoiseExpr x, NoiseExpr z, float frequency,
            int octaves = 1, float lacunarity = 2f, float gain = 0.5f, FbmMode mode = FbmMode.Standard)
            => Fractal(NoiseOpCode.Perlin2, name, Scaled(x, frequency, nameof(x)), null, Scaled(z, frequency, nameof(z)),
                octaves, lacunarity, gain, mode);

        public static NoiseExpr Perlin3D(string name, NoiseExpr x, NoiseExpr y, NoiseExpr z, float frequency,
            int octaves = 1, float lacunarity = 2f, float gain = 0.5f, FbmMode mode = FbmMode.Standard)
            => Fractal(NoiseOpCode.Perlin3, name, Scaled(x, frequency, nameof(x)), Scaled(y, frequency, nameof(y)),
                Scaled(z, frequency, nameof(z)), octaves, lacunarity, gain, mode);

        public static NoiseExpr Value2D(string name, NoiseExpr x, NoiseExpr z, float frequency,
            int octaves = 1, float lacunarity = 2f, float gain = 0.5f, FbmMode mode = FbmMode.Standard)
            => Fractal(NoiseOpCode.Value2, name, Scaled(x, frequency, nameof(x)), null, Scaled(z, frequency, nameof(z)),
                octaves, lacunarity, gain, mode);

        public static NoiseExpr Value3D(string name, NoiseExpr x, NoiseExpr y, NoiseExpr z, float frequency,
            int octaves = 1, float lacunarity = 2f, float gain = 0.5f, FbmMode mode = FbmMode.Standard)
            => Fractal(NoiseOpCode.Value3, name, Scaled(x, frequency, nameof(x)), Scaled(y, frequency, nameof(y)),
                Scaled(z, frequency, nameof(z)), octaves, lacunarity, gain, mode);

        /// <summary>
        /// Offsets a pair of coordinates by their own noise field. Feed the results to any 2D sampler
        /// and its output stops looking like noise: coastlines meander, ridges wind, cliffs overhang.
        /// The cheapest large improvement available to a heightmap.
        /// </summary>
        public static (NoiseExpr x, NoiseExpr z) Warp2D(string name, NoiseExpr x, NoiseExpr z,
            float frequency, float strength, int octaves = 2)
        {
            RequireName(name);
            var offsetX = Perlin2D(name + ".x", x, z, frequency, octaves);
            var offsetZ = Perlin2D(name + ".z", x, z, frequency, octaves);
            return (x + offsetX * Constant(strength), z + offsetZ * Constant(strength));
        }

        /// <summary>The three-dimensional counterpart, for warping a density field.</summary>
        public static (NoiseExpr x, NoiseExpr y, NoiseExpr z) Warp3D(string name, NoiseExpr x, NoiseExpr y,
            NoiseExpr z, float frequency, float strength, int octaves = 2)
        {
            RequireName(name);
            var strengthConstant = Constant(strength);
            return (x + Perlin3D(name + ".x", x, y, z, frequency, octaves) * strengthConstant,
                    y + Perlin3D(name + ".y", x, y, z, frequency, octaves) * strengthConstant,
                    z + Perlin3D(name + ".z", x, y, z, frequency, octaves) * strengthConstant);
        }

        /// <summary>
        /// Calls an author-supplied Burst function registered under <paramref name="externalId"/>. The
        /// escape hatch for anything the instruction set does not express; it receives the sample
        /// coordinate, its own derived seed, and two operand values.
        /// </summary>
        public static NoiseExpr External(string externalId, string name, NoiseExpr a = null, NoiseExpr b = null)
        {
            if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("An external call needs a registry id.", nameof(externalId));
            RequireName(name);
            return new NoiseExpr(NoiseOpCode.CallExternal, a ?? Constant(0f), b ?? Constant(0f), name: name, externalId: externalId);
        }

        private static NoiseExpr Fractal(NoiseOpCode code, string name, NoiseExpr x, NoiseExpr y, NoiseExpr z,
            int octaves, float lacunarity, float gain, FbmMode mode)
        {
            RequireName(name);
            if (octaves < 1 || octaves > 16) throw new ArgumentOutOfRangeException(nameof(octaves), "Octaves must be between 1 and 16.");
            if (!(lacunarity > 0f)) throw new ArgumentOutOfRangeException(nameof(lacunarity));
            if (!(gain > 0f && gain <= 1f)) throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be greater than 0 and at most 1.");
            return new NoiseExpr(code, x, y, z, p0: lacunarity, p1: gain, count: octaves, mode: (byte)mode, name: name);
        }

        //frequency is folded into the coordinates rather than carried as an instruction operand, so a
        //warped and an unwarped sampler are the same op with different inputs.
        private static NoiseExpr Scaled(NoiseExpr coordinate, float frequency, string parameterName)
        {
            Require(coordinate, parameterName);
            if (frequency == 0f) throw new ArgumentOutOfRangeException(nameof(frequency), "A frequency of zero samples one point forever.");
            return frequency == 1f ? coordinate : coordinate * Constant(frequency);
        }

        private static NoiseExpr Binary(NoiseOpCode code, NoiseExpr a, NoiseExpr b)
            => new NoiseExpr(code, Require(a, nameof(a)), Require(b, nameof(b)));

        private static NoiseExpr Require(NoiseExpr value, string parameterName)
            => value ?? throw new ArgumentNullException(parameterName);

        private static void RequireName(string name)
        {
            //nodes are seeded by name so that inserting one leaves its siblings' worlds untouched.
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Every seeded node needs a stable name.", nameof(name));
        }
    }
}
