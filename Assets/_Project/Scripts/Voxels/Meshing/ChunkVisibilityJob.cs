using DigBlocks.Voxels.Definitions;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing
{
    [BurstCompile(CompileSynchronously = true)]
    public struct ChunkVisibilityJob : IJob
    {
        [ReadOnly] public NativeArray<uint> Voxels;
        [ReadOnly] public NativeArray<BlockAttributes>.ReadOnly Attributes;
        public NativeArray<byte> Visited;
        public NativeArray<int> Queue;
        [WriteOnly] public NativeReference<ulong> Result;

        public void Execute()
        {
            for (int i = 0; i < ChunkLayout.Volume; i++) Visited[i] = 0;
            ulong connectivity = 0;

            for (int start = 0; start < ChunkLayout.Volume; start++)
            {
                if (Visited[start] != 0 || !IsPassable(start)) continue;

                int head = 0, tail = 0;
                byte faces = 0;
                Visited[start] = 1;
                Queue[tail++] = start;

                while (head < tail)
                {
                    int cellIndex = Queue[head++];
                    int x = cellIndex & 31;
                    int z = cellIndex >> 5 & 31;
                    int y = cellIndex >> 10;

                    if (y == 0) faces |= 1 << (int)BlockFace.Down;
                    if (y == 31) faces |= 1 << (int)BlockFace.Up;
                    if (z == 31) faces |= 1 << (int)BlockFace.North;
                    if (z == 0) faces |= 1 << (int)BlockFace.South;
                    if (x == 0) faces |= 1 << (int)BlockFace.West;
                    if (x == 31) faces |= 1 << (int)BlockFace.East;

                    if (y > 0) Visit(cellIndex - 1024, ref tail);
                    if (y < 31) Visit(cellIndex + 1024, ref tail);
                    if (z < 31) Visit(cellIndex + 32, ref tail);
                    if (z > 0) Visit(cellIndex - 32, ref tail);
                    if (x > 0) Visit(cellIndex - 1, ref tail);
                    if (x < 31) Visit(cellIndex + 1, ref tail);
                }

                for (int incoming = 0; incoming < ChunkFaceConnectivity.FaceCount; incoming++)
                    if ((faces & (1 << incoming)) != 0)
                        connectivity |= (ulong)faces << (incoming * ChunkFaceConnectivity.FaceCount);
            }

            Result.Value = connectivity;
        }

        private bool IsPassable(int index)
        {
            int x = index & 31;
            int z = index >> 5 & 31;
            int y = index >> 10;
            uint state = Voxels[GreedyMesherJob.Index(new int3(x, y, z))];
            return !Attributes[(int)state].Has(BlockFlags.Opaque | BlockFlags.FullCube);
        }

        private void Visit(int index, ref int tail)
        {
            if (Visited[index] != 0 || !IsPassable(index)) return;
            Visited[index] = 1;
            Queue[tail++] = index;
        }
    }
}
