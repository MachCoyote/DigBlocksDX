using System;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// An authored curve that maps a noise value onto something meaningful, most often a height.
    /// <para>
    /// Tangents are solved once, when the spline is built, using the Fritsch-Carlson conditions so a
    /// smooth curve never overshoots its own control points. Overshoot in a terrain height curve
    /// shows up as spikes the author never asked for.
    /// </para>
    /// </summary>
    public sealed class Spline : IEquatable<Spline>
    {
        public readonly SplineKnot[] Knots;
        public readonly SplineInterpolation Interpolation;

        private Spline(SplineKnot[] knots, SplineInterpolation interpolation)
        {
            Knots = knots; Interpolation = interpolation;
        }

        /// <summary>Straight segments between the knots.</summary>
        public static Spline Linear(params (float input, float output)[] knots)
            => new Spline(Build(knots, false), SplineInterpolation.Linear);

        /// <summary>Monotone cubic through the knots. The usual choice for terrain.</summary>
        public static Spline Smooth(params (float input, float output)[] knots)
            => new Spline(Build(knots, true), SplineInterpolation.Smooth);

        private static SplineKnot[] Build((float input, float output)[] source, bool solveTangents)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Length < 2) throw new ArgumentException("A spline needs at least two knots.", nameof(source));

            var knots = new SplineKnot[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                if (index > 0 && !(source[index].input > source[index - 1].input))
                    throw new ArgumentException("Spline knots must be ordered by strictly increasing input.", nameof(source));
                knots[index] = new SplineKnot { X = source[index].input, Y = source[index].output };
            }
            if (solveTangents) SolveMonotoneTangents(knots);
            return knots;
        }

        private static void SolveMonotoneTangents(SplineKnot[] knots)
        {
            int last = knots.Length - 1;
            var secants = new float[last];
            for (int index = 0; index < last; index++)
                secants[index] = (knots[index + 1].Y - knots[index].Y) / (knots[index + 1].X - knots[index].X);

            knots[0].Tangent = secants[0];
            knots[last].Tangent = secants[last - 1];
            for (int index = 1; index < last; index++)
                knots[index].Tangent = (secants[index - 1] + secants[index]) * 0.5f;

            //where the data is flat the curve must be flat too, and elsewhere the tangents are pulled
            //back inside the circle of radius three that guarantees no overshoot.
            for (int index = 0; index < last; index++)
            {
                if (secants[index] == 0f)
                {
                    knots[index].Tangent = 0f;
                    knots[index + 1].Tangent = 0f;
                    continue;
                }
                float alpha = knots[index].Tangent / secants[index];
                float beta = knots[index + 1].Tangent / secants[index];
                float radius = alpha * alpha + beta * beta;
                if (radius <= 9f) continue;
                float scale = 3f / math.sqrt(radius);
                knots[index].Tangent = scale * alpha * secants[index];
                knots[index + 1].Tangent = scale * beta * secants[index];
            }
        }

        public bool Equals(Spline other)
        {
            if (other == null || other.Interpolation != Interpolation || other.Knots.Length != Knots.Length) return false;
            for (int index = 0; index < Knots.Length; index++)
            {
                if (Knots[index].X != other.Knots[index].X) return false;
                if (Knots[index].Y != other.Knots[index].Y) return false;
                if (Knots[index].Tangent != other.Knots[index].Tangent) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is Spline other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Interpolation * 397 ^ Knots.Length;
                for (int index = 0; index < Knots.Length; index++)
                    hash = hash * 397 ^ Knots[index].X.GetHashCode() ^ Knots[index].Y.GetHashCode();
                return hash;
            }
        }
    }
}
