using DigBlocks.Voxels.Definitions;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing
{
    public static class FaceBasis
    {
        //cross(V,U) matches the clockwise triangle order; all side V axes point up.
        public static void Get(BlockFace face, out int3 normal, out int3 u, out int3 v)
        {
            switch (face)
            {
                case BlockFace.Down: normal = new int3(0,-1,0); u = new int3(1,0,0); v = new int3(0,0,-1); break;
                case BlockFace.Up: normal = new int3(0,1,0); u = new int3(1,0,0); v = new int3(0,0,1); break;
                case BlockFace.North: normal = new int3(0,0,1); u = new int3(-1,0,0); v = new int3(0,1,0); break;
                case BlockFace.South: normal = new int3(0,0,-1); u = new int3(1,0,0); v = new int3(0,1,0); break;
                case BlockFace.West: normal = new int3(-1,0,0); u = new int3(0,0,-1); v = new int3(0,1,0); break;
                default: normal = new int3(1,0,0); u = new int3(0,0,1); v = new int3(0,1,0); break;
            }
        }

        public static int3 Cell(int slice, int x, int y, int3 normal, int3 u, int3 v) =>
            math.abs(normal) * slice + math.abs(u) * math.select(x, 31 - x, math.any(u < 0)) +
            math.abs(v) * math.select(y, 31 - y, math.any(v < 0));
        public static int3 AnchorOffset(int3 normal, int3 u, int3 v) => math.select(new int3(0), new int3(1), (normal > 0) | (u < 0) | (v < 0));
    }
}
