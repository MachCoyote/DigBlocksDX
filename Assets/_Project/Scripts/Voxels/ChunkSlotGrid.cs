using System;
using Unity.Mathematics;

namespace DigBlocks.Voxels
{
    /// <summary>
    /// Wraps a chunk position onto a fixed box of slots, the way a clipmap addresses a moving window.
    /// A slot is a pure function of the position, so a chunk keeps its slot while the viewer moves and
    /// the chunk entering the volume reuses the slot of the one that left. That makes slot exhaustion
    /// impossible rather than merely unlikely: there is nothing to allocate.
    /// </summary>
    /// <remarks>
    /// Correctness needs no knowledge of where the volume is centred. Two positions collide only when
    /// they differ by a whole multiple of a side length, and the box is built one chunk wider than the
    /// streamed radius in every axis, so no two chunks that can be resident together ever do.
    /// The streamed volume is a cylinder inscribed in this box; the corner slots simply stay empty.
    /// </remarks>
    public readonly struct ChunkSlotGrid : IEquatable<ChunkSlotGrid>
    {
        //generous next to the ~5.6k slots a radius of 12 needs, and small enough that Capacity, and the
        //per-slot arrays sized from it, cannot overflow an int.
        public const int MaximumCapacity = 1 << 20;

        public int HorizontalRadius { get; }
        public int VerticalRadius { get; }

        /// <summary>Side of the box on x and z: the streamed diameter plus the chunk the anchor stands in.</summary>
        public int Width => 2 * HorizontalRadius + 1;
        public int Height => 2 * VerticalRadius + 1;
        public int Capacity => Width * Width * Height;

        public ChunkSlotGrid(int horizontalRadius, int verticalRadius)
        {
            if (horizontalRadius < 0) throw new ArgumentOutOfRangeException(nameof(horizontalRadius));
            if (verticalRadius < 0) throw new ArgumentOutOfRangeException(nameof(verticalRadius));
            long width = 2L * horizontalRadius + 1, height = 2L * verticalRadius + 1;
            if (width * width * height > MaximumCapacity)
                throw new ArgumentOutOfRangeException(nameof(horizontalRadius),
                    $"A {horizontalRadius}x{verticalRadius} view distance needs more than {MaximumCapacity} chunk slots.");
            HorizontalRadius = horizontalRadius; VerticalRadius = verticalRadius;
        }

        //x fastest, then z, then y, matching ChunkLayout.Index so both index orders read the same way.
        public int SlotOf(int3 chunkPosition)
        {
            int width = Width;
            return Wrap(chunkPosition.x, width) + width * (Wrap(chunkPosition.z, width) + width * Wrap(chunkPosition.y, Height));
        }

        //a floor modulus rather than the remainder operator, which would fold negative coordinates onto
        //the same slots as positive ones and alias two live chunks together.
        private static int Wrap(int value, int modulus)
        {
            int remainder = value % modulus;
            return remainder < 0 ? remainder + modulus : remainder;
        }

        public bool Equals(ChunkSlotGrid other) =>
            HorizontalRadius == other.HorizontalRadius && VerticalRadius == other.VerticalRadius;
        public override bool Equals(object obj) => obj is ChunkSlotGrid other && Equals(other);
        public override int GetHashCode() => HorizontalRadius * 397 ^ VerticalRadius;
        public override string ToString() => $"{Width}x{Height}x{Width} ({Capacity} slots)";
    }
}
