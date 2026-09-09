using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering
{
    public sealed class ChunkOcclusionGraph
    {
        private readonly int capacity;
        private readonly int3[] positions;
        private readonly ulong[] connectivity;
        private readonly byte[] active, renderable, incoming, processed, queued, cameraVisible;
        private readonly int[] queue;
        private readonly Dictionary<int3, int> positionToSlot;
        private bool ready, consistent = true;

        public int ResidentCount { get; private set; }
        public int RenderableCount { get; private set; }
        public int CameraVisibleCount { get; private set; }
        public int GraphCulledCount => RenderableCount - CameraVisibleCount;

        public ChunkOcclusionGraph(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            positions = new int3[capacity];
            connectivity = new ulong[capacity];
            active = new byte[capacity];
            renderable = new byte[capacity];
            incoming = new byte[capacity];
            processed = new byte[capacity];
            queued = new byte[capacity];
            cameraVisible = new byte[capacity];
            queue = new int[capacity];
            positionToSlot = new Dictionary<int3, int>(capacity);
        }

        public void SetNode(int slot, int3 position, ChunkFaceConnectivity faceConnectivity, bool hasGeometry)
        {
            if ((uint)slot >= capacity) throw new ArgumentOutOfRangeException(nameof(slot));
            positions[slot] = position;
            connectivity[slot] = faceConnectivity.Bits;
            active[slot] = 1;
            renderable[slot] = hasGeometry ? (byte)1 : (byte)0;
            RebuildLookup();
        }

        public void RemoveNode(int slot)
        {
            if ((uint)slot >= capacity) throw new ArgumentOutOfRangeException(nameof(slot));
            active[slot] = 0;
            renderable[slot] = 0;
            cameraVisible[slot] = 0;
            RebuildLookup();
        }

        public void Clear()
        {
            Array.Clear(active, 0, capacity);
            Array.Clear(renderable, 0, capacity);
            Array.Clear(cameraVisible, 0, capacity);
            positionToSlot.Clear();
            ResidentCount = 0;
            RenderableCount = 0;
            CameraVisibleCount = 0;
            ready = false;
            consistent = true;
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
            if (!enabled || !ready || !consistent || !positionToSlot.TryGetValue(origin, out int originSlot))
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
                    FaceBasis.Get((BlockFace)direction, out var normal, out _, out _);
                    if (!positionToSlot.TryGetValue(positions[slot] + normal, out int neighbor)) continue;

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

        private void RebuildLookup()
        {
            positionToSlot.Clear();
            ResidentCount = 0;
            RenderableCount = 0;
            consistent = true;
            for (int slot = 0; slot < capacity; slot++)
            {
                if (active[slot] == 0) continue;
                ResidentCount++;
                if (renderable[slot] != 0) RenderableCount++;
                if (positionToSlot.ContainsKey(positions[slot])) consistent = false;
                else positionToSlot.Add(positions[slot], slot);
            }
            CountVisibleRenderables();
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
