using System;
using DigBlocks.Voxels.Definitions;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing
{
    //96 bits per rectangle: local anchor/extents/direction, surface, and chunk-slot/material identity.
    public readonly struct PackedQuad
    {
        public const int Stride = 12;
        public readonly uint Geometry, Surface, Owner;
        public int3 Anchor => new int3((int)(Geometry & 63), (int)((Geometry >> 6) & 63), (int)((Geometry >> 12) & 63));
        public int Width => (int)((Geometry >> 18) & 31) + 1;
        public int Height => (int)((Geometry >> 23) & 31) + 1;
        public BlockFace Face => (BlockFace)((Geometry >> 28) & 7);
        public ushort Texture => (ushort)Surface;
        public byte Tint => (byte)(Surface >> 16);
        public byte Rotation => (byte)((Surface >> 24) & 3);
        public uint ChunkSlot => Owner & 0xffffff;
        public byte Material => (byte)(Owner >> 24);

        public PackedQuad(int3 anchor, int width, int height, BlockFace face, ushort texture, byte tint, byte rotation, uint slot, byte material)
        {
            if (math.any(anchor < 0) || math.any(anchor > ChunkLayout.Edge) || width < 1 || width > ChunkLayout.Edge ||
                height < 1 || height > ChunkLayout.Edge || (uint)face > 5 || rotation > 3 || slot > 0xffffff)
                throw new ArgumentOutOfRangeException(nameof(anchor), "Quad cannot be represented by the terrain format.");
            Geometry = (uint)anchor.x | (uint)anchor.y << 6 | (uint)anchor.z << 12 |
                (uint)(width - 1) << 18 | (uint)(height - 1) << 23 | (uint)face << 28;
            Surface = texture | (uint)tint << 16 | (uint)rotation << 24;
            Owner = slot | (uint)material << 24;
        }
    }
}
