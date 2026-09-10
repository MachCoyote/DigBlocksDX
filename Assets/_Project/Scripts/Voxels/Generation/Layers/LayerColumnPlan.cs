using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One layer's surface and extent heights for the 32 by 32 columns of a chunk position, in world Y.
    /// <para>
    /// This is the expensive part of generating a layer and the part that does not depend on the
    /// chunk's Y at all, which is what makes it the unit worth caching across a vertical stack: every
    /// chunk in a column of chunks shares exactly this.
    /// </para>
    /// </summary>
    public sealed class LayerColumnPlan : IDisposable
    {
        public const int Columns = ColumnFillKernel.Columns;

        private NativeArray<int> heights;
        private bool disposed;

        public int BandCount { get; }
        /// <summary>The chunk column this plan was computed for, so a cache can tell entries apart.</summary>
        public int ChunkX { get; private set; }
        public int ChunkZ { get; private set; }
        /// <summary>False until a planner has filled it, so a recycled plan cannot be read as if it were current.</summary>
        public bool IsPlanned { get; private set; }

        public LayerColumnPlan(int bandCount)
        {
            if (bandCount < 1) throw new ArgumentOutOfRangeException(nameof(bandCount));
            BandCount = bandCount;
            heights = new NativeArray<int>(bandCount * 2 * Columns, Allocator.Persistent);
        }

        public unsafe int* Surface(int band) => Plane(band, 0);
        public unsafe int* Extent(int band) => Plane(band, 1);

        private unsafe int* Plane(int band, int which)
        {
            if (disposed) throw new ObjectDisposedException(nameof(LayerColumnPlan));
            if ((uint)band >= (uint)BandCount) throw new ArgumentOutOfRangeException(nameof(band));
            return (int*)heights.GetUnsafePtr() + (long)(band * 2 + which) * Columns;
        }

        /// <summary>Marks the plan as describing this chunk column. Planners call this once they have filled it.</summary>
        public void MarkPlanned(int chunkX, int chunkZ)
        {
            ChunkX = chunkX; ChunkZ = chunkZ; IsPlanned = true;
        }

        public void Invalidate() => IsPlanned = false;

        public bool Describes(int chunkX, int chunkZ) => IsPlanned && ChunkX == chunkX && ChunkZ == chunkZ;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (heights.IsCreated) heights.Dispose();
        }
    }
}
