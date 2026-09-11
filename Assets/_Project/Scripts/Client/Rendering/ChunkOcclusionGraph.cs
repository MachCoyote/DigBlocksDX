using System;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering
{
    public sealed class ChunkOcclusionGraph
    {
        private readonly ChunkSlotGrid grid;
        private readonly int capacity;
        private readonly int3[] positions;
        private readonly ulong[] connectivity;
        private readonly byte[] active, renderable, incoming, processed, queued, cameraVisible;
        private readonly int[] queue;
        //slot of each face neighbour, precomputed. A slot is a pure function of position, so the map
        //from a slot to its neighbour's slot is a fixed permutation: it never changes as the viewer
        //moves, which is what replaces the per-face dictionary probe this traversal used to do.
        private readonly int[] neighbors;
        private bool ready, aliased;

        public int ResidentCount { get; private set; }
        public int RenderableCount { get; private set; }
        public int CameraVisibleCount { get; private set; }
        public int GraphCulledCount => RenderableCount - CameraVisibleCount;

        public ChunkOcclusionGraph(ChunkSlotGrid grid)
        {
            this.grid = grid;
            capacity = grid.Capacity;
            positions = new int3[capacity];
            connectivity = new ulong[capacity];
            active = new byte[capacity];
            renderable = new byte[capacity];
            incoming = new byte[capacity];
            processed = new byte[capacity];
            queued = new byte[capacity];
            cameraVisible = new byte[capacity];
            queue = new int[capacity];
            neighbors = BuildNeighbors(grid);
        }

        private static int[] BuildNeighbors(ChunkSlotGrid grid)
        {
            int width = grid.Width, height = grid.Height;
            var table = new int[grid.Capacity * ChunkFaceConnectivity.FaceCount];
            for (int y = 0; y < height; y++)
            for (int z = 0; z < width; z++)
            for (int x = 0; x < width; x++)
            {
                var position = new int3(x, y, z);
                int slot = grid.SlotOf(position);
                for (int direction = 0; direction < ChunkFaceConnectivity.FaceCount; direction++)
                {
                    FaceBasis.Get((BlockFace)direction, out var normal, out _, out _);
                    table[slot * ChunkFaceConnectivity.FaceCount + direction] = grid.SlotOf(position + normal);
                }
            }
            return table;
        }

        /// <summary>
        /// Admits a chunk at its own slot. Returns false if the slot already holds a different chunk,
        /// which means more chunks are resident than the grid was built for; the graph then fails open
        /// rather than culling against a view of the world it knows to be wrong.
        /// </summary>
        public bool SetNode(int3 position, ChunkFaceConnectivity faceConnectivity, bool hasGeometry)
        {
            int slot = grid.SlotOf(position);
            if (active[slot] != 0 && !math.all(positions[slot] == position))
            {
                aliased = true;
                return false;
            }
            if (active[slot] == 0) { ResidentCount++; active[slot] = 1; }
            else if (renderable[slot] != 0) RenderableCount--;
            positions[slot] = position;
            connectivity[slot] = faceConnectivity.Bits;
            renderable[slot] = hasGeometry ? (byte)1 : (byte)0;
            if (hasGeometry) RenderableCount++;
            return true;
        }

        /// <summary>Retires a chunk. False if that slot holds someone else, who must be left alone.</summary>
        public bool RemoveNode(int3 position)
        {
            int slot = grid.SlotOf(position);
            if (active[slot] == 0 || !math.all(positions[slot] == position)) return false;
            if (renderable[slot] != 0) RenderableCount--;
            ResidentCount--;
            active[slot] = 0;
            renderable[slot] = 0;
            cameraVisible[slot] = 0;
            return true;
        }

        public int SlotOf(int3 position) => grid.SlotOf(position);

        public void Clear()
        {
            Array.Clear(active, 0, capacity);
            Array.Clear(renderable, 0, capacity);
            Array.Clear(cameraVisible, 0, capacity);
            ResidentCount = 0;
            RenderableCount = 0;
            CameraVisibleCount = 0;
            ready = false;
            aliased = false;
        }

        public void SetReady(bool value)
        {
            ready = value;
            if (!ready) FailOpen();
        }

        public bool IsCameraVisible(int slot) => (uint)slot < capacity && active[slot] != 0 && cameraVisible[slot] != 0;

        public void Cull(float3 cameraPosition, bool enabled)
        {
            Array.Clear(incoming, 0, capacity);
            Array.Clear(processed, 0, capacity);
            Array.Clear(queued, 0, capacity);
            Array.Clear(cameraVisible, 0, capacity);

            int3 origin = (int3)math.floor(cameraPosition / ChunkLayout.Edge);
            int originSlot = grid.SlotOf(origin);
            if (!enabled || !ready || aliased || active[originSlot] == 0 || !math.all(positions[originSlot] == origin))
            {
                FailOpen();
                return;
            }

            int head = 0, tail = 0, count = 0;
            incoming[originSlot] = 0x3f;
            cameraVisible[originSlot] = 1;
            if (!Enqueue(originSlot, ref tail, ref count)) { FailOpen(); return; }

            while (count > 0)
            {
                int slot = queue[head];
                head = (head + 1) % capacity;
                count--;
                queued[slot] = 0;

                byte freshIncoming = (byte)(incoming[slot] & ~processed[slot]);
                processed[slot] |= freshIncoming;
                byte outgoing = new ChunkFaceConnectivity(connectivity[slot]).Outgoing(freshIncoming);
                for (int direction = 0; direction < ChunkFaceConnectivity.FaceCount; direction++)
                {
                    if ((outgoing & (1 << direction)) == 0) continue;
                    int neighbor = neighbors[slot * ChunkFaceConnectivity.FaceCount + direction];
                    if (active[neighbor] == 0) continue;
                    //the slot is right, but it may hold the chunk that wrapped onto it from the far side
                    //of the box rather than this chunk's actual neighbour, so the position decides.
                    FaceBasis.Get((BlockFace)direction, out var normal, out _, out _);
                    if (!math.all(positions[neighbor] == positions[slot] + normal)) continue;

                    byte entry = (byte)(1 << (int)ChunkFaceConnectivity.Opposite((BlockFace)direction));
                    if ((incoming[neighbor] & entry) != 0) continue;
                    incoming[neighbor] |= entry;
                    cameraVisible[neighbor] = 1;
                    if ((processed[neighbor] & entry) == 0 && !Enqueue(neighbor, ref tail, ref count))
                    {
                        FailOpen();
                        return;
                    }
                }
            }

            CountVisibleRenderables();
        }

        private bool Enqueue(int slot, ref int tail, ref int count)
        {
            if (queued[slot] != 0) return true;
            if (count >= capacity) return false;
            queue[tail] = slot;
            tail = (tail + 1) % capacity;
            count++;
            queued[slot] = 1;
            return true;
        }

        private void FailOpen()
        {
            for (int slot = 0; slot < capacity; slot++)
                cameraVisible[slot] = active[slot];
            CameraVisibleCount = RenderableCount;
        }

        private void CountVisibleRenderables()
        {
            CameraVisibleCount = 0;
            for (int slot = 0; slot < capacity; slot++)
                if (renderable[slot] != 0 && cameraVisible[slot] != 0)
                    CameraVisibleCount++;
        }
    }
}
