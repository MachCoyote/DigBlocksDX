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
            //origin first makes the dummy anchor usable before its neighbors finish.
            result[index++] = Anchor;
            for (int y = -VerticalRadius; y <= VerticalRadius; y++)
            for (int z = -HorizontalRadius; z <= HorizontalRadius; z++)
            for (int x = -HorizontalRadius; x <= HorizontalRadius; x++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                result[index++] = new ChunkAddress(Anchor.World, Anchor.Position + new int3(x, y, z));
            }
            return result;
        }
    }
}
