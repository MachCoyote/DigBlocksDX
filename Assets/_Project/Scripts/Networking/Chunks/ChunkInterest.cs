using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using Unity.Mathematics;

namespace DigBlocks.ChunkProtocol
{
    /// <summary>
    /// Server-selected interest volume: a cylinder standing on the anchor. Horizontal distance is a
    /// true radius so the corners of a square, which are further away than the render distance
    /// claims, are never streamed; vertical extent is a straight offset from any column in it.
    /// Wire consumers must validate before allocating.
    /// </summary>
    public sealed class ChunkInterest
    {
        //sanity ceiling for a decoded interest, not a tuning value. The real residency bound is the
        //store's maxResidents, which is local configuration rather than something a peer can choose.
        public const int MaximumChunks = 32768;
        public const int MaximumRadius = 4096;
        public ChunkAddress Anchor { get; }
        public int HorizontalRadius { get; }
        public int VerticalRadius { get; }
        public int Count { get; }
        public ulong Epoch { get; }

        public ChunkInterest(ulong epoch, ChunkAddress anchor, int horizontalRadius, int verticalRadius)
        {
            if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch));
            if (horizontalRadius < 0 || horizontalRadius > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(horizontalRadius));
            if (verticalRadius < 0 || verticalRadius > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(verticalRadius));
            long count = CountFor(horizontalRadius, verticalRadius);
            if (count > MaximumChunks) throw new ArgumentException("Interest exceeds the chunk limit.");
            CheckAxis(anchor.Position.x, horizontalRadius);
            CheckAxis(anchor.Position.y, verticalRadius);
            CheckAxis(anchor.Position.z, horizontalRadius);
            Epoch = epoch; Anchor = anchor; HorizontalRadius = horizontalRadius;
            VerticalRadius = verticalRadius; Count = (int)count;
        }

        /// <summary>Chunks a cylinder of this shape holds, without enumerating it. Linear in the radius.</summary>
        public static long CountFor(int horizontalRadius, int verticalRadius)
        {
            if (horizontalRadius < 0 || horizontalRadius > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(horizontalRadius));
            if (verticalRadius < 0 || verticalRadius > MaximumRadius) throw new ArgumentOutOfRangeException(nameof(verticalRadius));
            return Columns(horizontalRadius) * (2L * verticalRadius + 1);
        }

        //columns whose centre lies inside the horizontal radius. Math.Sqrt only seeds the span; the
        //two corrections make the result exactly agree with the integer test Contains uses.
        private static long Columns(int radius)
        {
            long squared = (long)radius * radius, total = 0;
            for (int x = -radius; x <= radius; x++)
            {
                long offset = (long)x * x;
                long span = (long)Math.Sqrt(squared - offset);
                while (span > 0 && span * span + offset > squared) span--;
                while ((span + 1) * (span + 1) + offset <= squared) span++;
                total += 2 * span + 1;
            }
            return total;
        }

        private static void CheckAxis(int position, int radius)
        {
            if ((long)position - radius < int.MinValue || (long)position + radius > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(position), "Interest crosses the address range.");
        }

        public bool Contains(ChunkAddress address)
        {
            if (address.World != Anchor.World) return false;
            long dy = (long)address.Position.y - Anchor.Position.y;
            if (Math.Abs(dy) > VerticalRadius) return false;
            long dx = (long)address.Position.x - Anchor.Position.x;
            long dz = (long)address.Position.z - Anchor.Position.z;
            return dx * dx + dz * dz <= (long)HorizontalRadius * HorizontalRadius;
        }

        public ChunkAddress[] Addresses()
        {
            var offsets = Offsets(HorizontalRadius, VerticalRadius);
            var result = new ChunkAddress[offsets.Length];
            for (int i = 0; i < offsets.Length; i++) result[i] = new ChunkAddress(Anchor.World, Anchor.Position + offsets[i]);
            return result;
        }

        //radial order depends only on the shape, so each shape is enumerated and sorted once and every
        //interest change after that is a translation. Interest changes whenever the player crosses a
        //chunk boundary, and re-sorting thousands of addresses on each of those would be a hitch.
        private static readonly Dictionary<(int, int), int3[]> OffsetCache = new();

        private static int3[] Offsets(int horizontalRadius, int verticalRadius)
        {
            lock (OffsetCache)
            {
                if (OffsetCache.TryGetValue((horizontalRadius, verticalRadius), out var cached)) return cached;
                long squared = (long)horizontalRadius * horizontalRadius;
                var offsets = new List<int3>((int)CountFor(horizontalRadius, verticalRadius));
                for (int y = -verticalRadius; y <= verticalRadius; y++)
                for (int z = -horizontalRadius; z <= horizontalRadius; z++)
                for (int x = -horizontalRadius; x <= horizontalRadius; x++)
                    if ((long)x * x + (long)z * z <= squared) offsets.Add(new int3(x, y, z));
                var result = offsets.ToArray();
                Array.Sort(result, CompareOffset);
                //bounded so a peer cycling render distances cannot grow this without limit.
                if (OffsetCache.Count >= 8) OffsetCache.Clear();
                OffsetCache[(horizontalRadius, verticalRadius)] = result;
                return result;
            }
        }

        private static int CompareOffset(int3 left, int3 right)
        {
            long leftDistance = SquaredLength(left), rightDistance = SquaredLength(right);
            int order = leftDistance.CompareTo(rightDistance);
            if (order != 0) return order;
            order = left.y.CompareTo(right.y);
            if (order != 0) return order;
            order = left.z.CompareTo(right.z);
            return order != 0 ? order : left.x.CompareTo(right.x);
        }

        private static long SquaredLength(int3 offset) =>
            (long)offset.x * offset.x + (long)offset.y * offset.y + (long)offset.z * offset.z;
    }
}
