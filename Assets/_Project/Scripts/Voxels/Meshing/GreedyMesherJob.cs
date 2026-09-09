using DigBlocks.Voxels.Appearance;
using DigBlocks.Voxels.Definitions;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing
{
    [BurstCompile(CompileSynchronously = true)]
    public struct GreedyMesherJob : IJob
    {
        public const int PaddedEdge = ChunkLayout.Edge + 2;
        public const int PaddedVolume = PaddedEdge * PaddedEdge * PaddedEdge;
        public const int MaximumQuads = ChunkLayout.Volume * 6;
        [ReadOnly] public NativeArray<uint> Voxels;
        [ReadOnly] public NativeArray<BlockAttributes>.ReadOnly Attributes;
        [ReadOnly] public BlockAppearanceTable.ReadOnly Appearance;
        public NativeArray<ulong> Mask;
        public NativeList<PackedQuad> Output;
        public int3 ChunkPosition;
        public uint Seed, Slot;

        public static int Index(int3 local) => local.x + 1 + PaddedEdge * (local.z + 1 + PaddedEdge * (local.y + 1));

        public void Execute()
        {
            Output.Clear();
            for (int direction = 0; direction < 6; direction++)
            {
                var face = (BlockFace)direction;
                FaceBasis.Get(face, out var normal, out var u, out var v);
                for (int slice = 0; slice < 32; slice++)
                {
                    for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                    {
                        var cell = FaceBasis.Cell(slice, x, y, normal, u, v);
                        uint state = Voxels[Index(cell)];
                        var render = Appearance.Solid(state);
                        uint adjacent = Voxels[Index(cell + normal)];
                        bool visible = render.IsVisible && Attributes[(int)state].Has(BlockFlags.FullCube) &&
                            !Attributes[(int)adjacent].Has(BlockFlags.Opaque | BlockFlags.FullCube);
                        Mask[x + y * 32] = visible ? Descriptor(state, face, cell) : 0;
                    }
                    for (int y = 0; y < 32; y++) for (int x = 0; x < 32;)
                    {
                        ulong key = Mask[x + y * 32];
                        if (key == 0) { x++; continue; }
                        int width = 1;
                        while (x + width < 32 && Mask[x + width + y * 32] == key) width++;
                        int height = 1;
                        while (y + height < 32)
                        {
                            bool matches = true;
                            for (int dx = 0; dx < width; dx++)
                                if (Mask[x + dx + (y + height) * 32] != key) { matches = false; break; }
                            if (!matches) break;
                            height++;
                        }
                        for (int dy = 0; dy < height; dy++) for (int dx = 0; dx < width; dx++)
                            Mask[x + dx + (y + dy) * 32] = 0;
                        int3 anchor = FaceBasis.Cell(slice, x, y, normal, u, v) + FaceBasis.AnchorOffset(normal, u, v);
                        Output.AddNoResize(new PackedQuad(anchor, width, height, face, (ushort)key, (byte)(key >> 16),
                            (byte)((key >> 24) & 3), Slot, (byte)(key >> 32)));
                        x += width;
                    }
                }
            }
        }

        private ulong Descriptor(uint state, BlockFace face, int3 cell)
        {
            var appearance = Appearance.SolidFace(state, face);
            byte rotation = ResolveRotation(appearance, ChunkPosition, cell, face, Seed);
            return (1ul << 63) | appearance.Texture | (ulong)appearance.Tint << 16 | (ulong)rotation << 24 |
                (ulong)Appearance.Solid(state).Material << 32;
        }

        public static byte ResolveRotation(BlockFaceAppearance appearance, int3 chunk, int3 local, BlockFace face, uint seed)
        {
            if (!appearance.RandomizeRotation) return appearance.Rotation;
            uint3 world = (uint3)chunk * 32u + (uint3)local;
            return (byte)((appearance.Rotation + math.hash(new uint4(world, seed ^ ((uint)face * 0x9e3779b9u)))) & 3);
        }
    }
}
