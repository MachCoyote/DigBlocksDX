using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>What a density field does to the solid a layer's bands already painted.</summary>
    public enum DensityMode : byte
    {
        /// <summary>Cells whose density falls below the threshold become air. Caves, cavities, overhangs.</summary>
        Carve = 0,
        /// <summary>Cells whose density reaches the threshold become solid. Floating ground, spires.</summary>
        Add = 1
    }

    /// <summary>
    /// A height-dependent bias added to a density field.
    /// <para>
    /// This is how terrain is made to close off at the ends of a layer without a hard clamp that would
    /// cut it flat. Bias the field strongly positive near the floor and strongly negative near the
    /// ceiling, and the shape resolves into solid ground and open sky on its own while still being
    /// free to overhang in the middle.
    /// </para>
    /// </summary>
    public sealed class DensityGradient
    {
        public int FromY { get; }
        public int ToY { get; }
        public float FromBias { get; }
        public float ToBias { get; }

        public DensityGradient(int fromY, int toY, float fromBias, float toBias)
        {
            if (fromY >= toY) throw new ArgumentException("A density gradient spans upward, so its top must be above its bottom.", nameof(toY));
            FromY = fromY; ToY = toY; FromBias = fromBias; ToBias = toBias;
        }

        /// <summary>A floor: solid below <paramref name="y"/>, fading out over <paramref name="fade"/> blocks above it.</summary>
        public static DensityGradient Floor(int y, int fade, float strength = 1f)
            => new DensityGradient(y, y + math.max(1, fade), strength, 0f);

        /// <summary>A ceiling: open above <paramref name="y"/>, fading in over <paramref name="fade"/> blocks below it.</summary>
        public static DensityGradient Ceiling(int y, int fade, float strength = 1f)
            => new DensityGradient(y - math.max(1, fade), y, 0f, -strength);
    }

    /// <summary>
    /// Three-dimensional shaping for a layer that wants more than a heightmap can say.
    /// <para>
    /// The field is not sampled per cell. It is sampled on a coarse lattice and interpolated between,
    /// because a chunk holds 32768 cells and a lattice of four by eight by four holds 405 of them. The
    /// cost of a layer opting into three dimensions is therefore roughly a heightmap's, not eighty
    /// times one.
    /// </para>
    /// </summary>
    public sealed class DensityStage
    {
        public string Name { get; }
        public NoiseExpr Field { get; }
        public DensityMode Mode { get; }
        public float Threshold { get; }
        /// <summary>Lattice spacing in blocks. Larger is cheaper and smoother; smaller is finer and dearer.</summary>
        public int3 Resolution { get; }
        public IReadOnlyList<DensityGradient> Gradients { get; }
        /// <summary>What <see cref="DensityMode.Add"/> places. Unused when carving.</summary>
        public string AddBlock { get; }

        public DensityStage(string name, NoiseExpr field, DensityMode mode = DensityMode.Carve,
            float threshold = 0f, int3 resolution = default, IReadOnlyList<DensityGradient> gradients = null,
            string addBlock = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A density stage needs a name.", nameof(name));
            Name = name;
            Field = field ?? throw new ArgumentNullException(nameof(field));
            Mode = mode;
            Threshold = threshold;
            Resolution = math.all(resolution == 0) ? new int3(4, 8, 4) : resolution;
            Gradients = gradients ?? Array.Empty<DensityGradient>();
            AddBlock = addBlock;

            if (math.any(Resolution < 1) || math.any(Resolution > ChunkLayout.Edge))
                throw new GenerationContentException($"Density stage '{name}' needs a lattice spacing between one and {ChunkLayout.Edge} blocks.");
            //the lattice has to land on chunk boundaries, or neighbouring chunks would interpolate
            //between different sample points and the terrain would not meet at the seam.
            if (ChunkLayout.Edge % Resolution.x != 0 || ChunkLayout.Edge % Resolution.y != 0 || ChunkLayout.Edge % Resolution.z != 0)
                throw new GenerationContentException($"Density stage '{name}' needs a lattice spacing that divides {ChunkLayout.Edge} evenly, or chunks would not meet at their seams.");
            if (Mode == DensityMode.Add && string.IsNullOrWhiteSpace(AddBlock))
                throw new GenerationContentException($"Density stage '{name}' adds solid but names no block to add.");
        }
    }
}
