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

    /// <summary>What a layer's sea needs to know to fill the open space below it.</summary>
    public unsafe struct SeaFillData
    {
        public int ChunkMinY, LayerBottom, LayerTop, SeaLevel;
        public uint Fluid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void SeaFill(SeaFillData* sea, uint* solids, uint* fluids);

    /// <summary>
    /// Fills the layer's fluid into whatever solid did not claim, up to its sea level.
    /// <para>
    /// This floods every open cell below the line, which is right while a layer has no caves. Once a
    /// density stage starts carving, a sealed cavern a hundred blocks down would flood too, and that
    /// is what <see cref="AquiferStage"/> exists to answer.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class SeaFillKernel
    {
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(SeaFill))]
        public static void Fill(SeaFillData* sea, uint* solids, uint* fluids)
        {
            int top = sea->SeaLevel < sea->LayerTop ? sea->SeaLevel : sea->LayerTop;
            for (int localY = 0; localY < ColumnFillKernel.Edge; localY++)
            {
                int worldY = sea->ChunkMinY + localY;
                if (worldY < sea->LayerBottom || worldY > top) continue;

                long offset = (long)localY * ColumnFillKernel.Columns;
                uint* solidSlice = solids + offset;
                uint* fluidSlice = fluids + offset;
                for (int column = 0; column < ColumnFillKernel.Columns; column++)
                    if (solidSlice[column] == 0u) fluidSlice[column] = sea->Fluid;
            }
        }
    }

    /// <summary>Holds the one Burst compilation of each fill kernel.</summary>
    public static unsafe class ColumnFillDispatch
    {
        private static readonly BurstEntryPoint<ColumnFill> ColumnEntry = new BurstEntryPoint<ColumnFill>(ColumnFillKernel.Fill);
        private static readonly BurstEntryPoint<SeaFill> SeaEntry = new BurstEntryPoint<SeaFill>(SeaFillKernel.Fill);

        public static FunctionPointer<ColumnFill> Compiled => ColumnEntry.Compiled;
        public static FunctionPointer<SeaFill> CompiledSea => SeaEntry.Compiled;

        public static void Warm() { ColumnEntry.Warm(); SeaEntry.Warm(); }
    }
}
