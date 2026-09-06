using System;
using Unity.Mathematics;

namespace DigBlocks.Voxels
{
    public static class ChunkLayout
    {
        public const int Edge = 32;
        public const int Volume = Edge * Edge * Edge;
        public static int3 ChunkOf(int3 position)
        {
            var remainder = position % Edge;
            return position / Edge - math.select(new int3(0), new int3(1), remainder < 0);
        }
        public static int3 LocalOf(int3 position) => (position % Edge + Edge) % Edge;
        public static int Index(int3 local)
        {
            if (math.any(local < 0) || math.any(local >= Edge)) throw new ArgumentOutOfRangeException(nameof(local));
            return local.x + Edge * (local.z + Edge * local.y);
        }
        public static int3 LocalOfIndex(int index)
        {
            if ((uint)index >= Volume) throw new ArgumentOutOfRangeException(nameof(index));
            return new int3(index % Edge, index / (Edge * Edge), index / Edge % Edge);
        }
    }

    public readonly struct ChunkAddress : IEquatable<ChunkAddress>
    {
        public readonly uint World;
        public readonly int3 Position;
        public ChunkAddress(uint world, int3 position) { World = world; Position = position; }
        public bool Equals(ChunkAddress other) => World == other.World && math.all(Position == other.Position);
        public override bool Equals(object obj) => obj is ChunkAddress other && Equals(other);
        public override int GetHashCode() => unchecked((int)math.hash(new int4(Position, (int)World)));
        public override string ToString() => $"{World}:{Position.x},{Position.y},{Position.z}";
    }
}
