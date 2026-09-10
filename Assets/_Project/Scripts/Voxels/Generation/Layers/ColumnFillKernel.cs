using System.Runtime.InteropServices;
using Unity.Burst;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>One band's planned columns and everything the fill needs to turn them into blocks.</summary>
    public unsafe struct BandFillData
    {
        /// <summary>World Y of the free face, one per column, indexed x + 32 * z.</summary>
        public int* SurfaceY;
        /// <summary>World Y the fill runs to. Read only when <see cref="HasExtent"/> is set.</summary>
        public int* ExtentY;
        public RecipeData Recipe;
        /// <summary>World Y of this chunk's local y = 0.</summary>
        public int ChunkMinY;
        public int LayerBottom, LayerTop, SeaLevel, FallbackExtentY;
        public byte Direction, HasSea, HasExtent;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void ColumnFill(BandFillData* band, uint* solids);

    /// <summary>
    /// Turns one band's planned columns into blocks inside one chunk.
    /// <para>
    /// The loop runs a horizontal slice at a time rather than a column at a time. A chunk indexes as
    /// <c>x + 32 * (z + 32 * y)</c>, so a column strides by 1024 while a slice is 1024 contiguous
    /// cells; walking columns would touch every cache line in the chunk once per column instead of
    /// once per slice.
    /// </para>
    /// <para>
    /// Cells outside the band's interval are left exactly as they were, so an earlier band's work
    /// survives and anything no band claims stays air.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class ColumnFillKernel
    {
        public const int Edge = ChunkLayout.Edge;
        public const int Columns = Edge * Edge;

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(ColumnFill))]
        public static void Fill(BandFillData* band, uint* solids)
        {
            RecipeData recipe = band->Recipe;
            bool up = band->Direction == (byte)BandDirection.Up;
            bool hasSea = band->HasSea != 0;
            bool hasExtent = band->HasExtent != 0;
            int seaLevel = band->SeaLevel;
            int fallback = band->FallbackExtentY;

            for (int localY = 0; localY < Edge; localY++)
            {
                int worldY = band->ChunkMinY + localY;
                if (worldY < band->LayerBottom || worldY > band->LayerTop) continue;

                uint* slice = solids + (long)localY * Columns;
                for (int column = 0; column < Columns; column++)
                {
                    int face = band->SurfaceY[column];
                    int extent = hasExtent ? band->ExtentY[column] : fallback;

                    int depth;
                    if (up)
                    {
                        if (worldY > face || worldY < extent) continue;
                        depth = face - worldY;
                    }
                    else
                    {
                        if (worldY < face || worldY > extent) continue;
                        depth = worldY - face;
                    }

                    slice[column] = Resolve(&recipe, depth, worldY, face, seaLevel, hasSea);
                }
            }
        }

        //overrides are tested before the strata so a beach or a snow cap replaces the surface material
        //rather than having to be woven into every recipe that might meet water or altitude.
        private static uint Resolve(RecipeData* recipe, int depth, int worldY, int faceY, int seaLevel, bool hasSea)
        {
            if (hasSea && recipe->SubmergedDepth > 0 && depth < recipe->SubmergedDepth && faceY <= seaLevel)
                return recipe->Submerged;
            if (recipe->CrestDepth > 0 && depth < recipe->CrestDepth && faceY >= recipe->CrestY)
                return recipe->Crest;

            for (int index = 0; index < recipe->StrataCount; index++)
                if (depth < recipe->StrataEnds[index]) return recipe->StrataBlocks[index];

            return worldY < recipe->DeepBelowY ? recipe->DeepBelow : recipe->Deep;
        }
    }

    /// <summary>Holds the one Burst compilation of the column fill kernel.</summary>
    public static unsafe class ColumnFillDispatch
    {
        private static readonly BurstEntryPoint<ColumnFill> Entry = new BurstEntryPoint<ColumnFill>(ColumnFillKernel.Fill);

        public static FunctionPointer<ColumnFill> Compiled => Entry.Compiled;
        public static void Warm() => Entry.Warm();
    }
}
