using System;
using DigBlocks.Voxels;
using Unity.Mathematics;

namespace DigBlocks.ChunkProtocol
{
    //server-selected development interest; wire consumers must validate before allocating.
    public sealed class ChunkInterest
    {
        public const int MaximumChunks = 256;
        public ChunkAddress Anchor { get; }
        public int HorizontalRadius { get; }
        public int VerticalRadius { get; }
        public int Count { get; }
        public ulong Epoch { get; }

        public ChunkInterest(ulong epoch, ChunkAddress anchor, int horizontalRadius, int verticalRadius)
        {
            if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            if (horizontalRadius < 0 || horizontalRadius > MaximumChunks) throw new ArgumentOutOfRangeException(nameof(horizontalRadius));
            if (verticalRadius < 0 || verticalRadius > MaximumChunks) throw new ArgumentOutOfRangeException(nameof(verticalRadius));
            long width = 2L * horizontalRadius + 1;
            long count = width * width * (2L * verticalRadius + 1);
            if (count > MaximumChunks) throw new ArgumentException("Interest exceeds the development chunk limit.");
            CheckAxis(anchor.Position.x, horizontalRadius);
            CheckAxis(anchor.Position.y, verticalRadius);
            CheckAxis(anchor.Position.z, horizontalRadius);
            Epoch = epoch; Anchor = anchor; HorizontalRadius = horizontalRadius;
            VerticalRadius = verticalRadius; Count = (int)count;
        }

        private static void CheckAxis(int position, int radius)
        {
            if ((long)position - radius < int.MinValue || (long)position + radius > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(position), "Interest crosses the address range.");
        }

        public bool Contains(ChunkAddress address) => address.World == Anchor.World &&
            Math.Abs((long)address.Position.x - Anchor.Position.x) <= HorizontalRadius &&
            Math.Abs((long)address.Position.y - Anchor.Position.y) <= VerticalRadius &&
            Math.Abs((long)address.Position.z - Anchor.Position.z) <= HorizontalRadius;

        public ChunkAddress[] Addresses()
        {
            var result = new ChunkAddress[Count];
            int index = 0;
            for (int y = -VerticalRadius; y <= VerticalRadius; y++)
            for (int z = -HorizontalRadius; z <= HorizontalRadius; z++)
            for (int x = -HorizontalRadius; x <= HorizontalRadius; x++)
                result[index++] = new ChunkAddress(Anchor.World, Anchor.Position + new int3(x, y, z));
            Array.Sort(result, CompareDistance);
            return result;
        }

        private int CompareDistance(ChunkAddress left, ChunkAddress right)
        {
            long leftDistance = SquaredDistance(left.Position);
            long rightDistance = SquaredDistance(right.Position);
            int order = leftDistance.CompareTo(rightDistance);
            if (order != 0) return order;
            order = left.Position.y.CompareTo(right.Position.y);
            if (order != 0) return order;
            order = left.Position.z.CompareTo(right.Position.z);
            return order != 0 ? order : left.Position.x.CompareTo(right.Position.x);
        }

        private long SquaredDistance(int3 position)
        {
            long x = (long)position.x - Anchor.Position.x;
            long y = (long)position.y - Anchor.Position.y;
            long z = (long)position.z - Anchor.Position.z;
            return x * x + y * y + z * z;
        }
    }
}
