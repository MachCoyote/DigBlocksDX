using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// What a band's solid interval is made of, described as depth from its free face rather than as
    /// absolute heights. Depth is the axis that stays meaningful when the surface moves, and it is the
    /// same axis biome surface rules will key on later.
    /// </summary>
    public sealed class ColumnRecipe
    {
        internal readonly List<(string block, int thickness)> Strata = new List<(string, int)>();
        internal string DeepBlock;
        internal string DeepBelowBlock;
        internal int DeepBelowY = int.MinValue;
        internal string CrestBlock;
        internal int CrestY = int.MaxValue, CrestDepth;
        internal string SubmergedBlock;
        internal int SubmergedDepth;

        private ColumnRecipe() { }

        public static ColumnRecipe Create() => new ColumnRecipe();

        /// <summary>A band of <paramref name="thickness"/> cells at the current depth, measured from the free face.</summary>
        public ColumnRecipe Layer(string block, int thickness = 1)
        {
            RequireBlock(block);
            if (thickness < 1) throw new ArgumentOutOfRangeException(nameof(thickness));
            if (Strata.Count >= 15) throw new InvalidOperationException("A recipe may have at most 15 strata.");
            Strata.Add((block, thickness));
            return this;
        }

        /// <summary>Everything past the named strata. Every recipe needs one.</summary>
        public ColumnRecipe Deep(string block)
        {
            RequireBlock(block);
            DeepBlock = block;
            return this;
        }

        /// <summary>
        /// Swaps the deep fill below a world height. The usual reason is a second stone type further
        /// down, so depth reads as depth rather than as one undifferentiated mass.
        /// </summary>
        public ColumnRecipe DeepBelow(int worldY, string block)
        {
            RequireBlock(block);
            DeepBelowY = worldY;
            DeepBelowBlock = block;
            return this;
        }

        /// <summary>
        /// Replaces the topmost cells where the free face sits at or above a world height. Snow caps
        /// and bare rock peaks.
        /// </summary>
        public ColumnRecipe Crest(int worldY, string block, int depth = 1)
        {
            RequireBlock(block);
            if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
            CrestY = worldY;
            CrestBlock = block;
            CrestDepth = depth;
            return this;
        }

        /// <summary>
        /// Replaces the topmost cells where the free face sits at or below the layer's sea level.
        /// Beaches, lake shores and riverbeds. Ignored by a layer with no sea.
        /// </summary>
        public ColumnRecipe Submerged(string block, int depth = 3)
        {
            RequireBlock(block);
            if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth));
            SubmergedBlock = block;
            SubmergedDepth = depth;
            return this;
        }

        /// <summary>A recipe that is one block all the way down. Bedrock shells and debug worlds.</summary>
        public static ColumnRecipe Solid(string block) => Create().Deep(block);

        /// <summary>Resolves the block keys and lays the recipe out for the fill kernel.</summary>
        public CompiledRecipe Compile(IBlockResolver resolver)
        {
            if (DeepBlock == null) throw new InvalidOperationException("A column recipe needs a deep fill block.");
            return new CompiledRecipe(this, resolver);
        }

        private static void RequireBlock(string block)
        {
            if (string.IsNullOrWhiteSpace(block)) throw new ArgumentException("A recipe entry needs a block key.", nameof(block));
        }
    }

    /// <summary>Turns authored block keys into the state ids generation writes. Implemented over the block registry.</summary>
    public interface IBlockResolver
    {
        uint Solid(string key);
        uint Fluid(string key);
    }

    /// <summary>
    /// A recipe with its block keys resolved and its depths accumulated, held in native memory so the
    /// fill kernel can read it without touching anything managed.
    /// </summary>
    public sealed class CompiledRecipe : IDisposable
    {
        private NativeArray<uint> strataBlocks;
        private NativeArray<int> strataEnds;
        private readonly int strataCount;
        private bool disposed;

        private readonly uint deep, deepBelow, crest, submerged;
        private readonly int deepBelowY, crestY, crestDepth, submergedDepth;

        internal CompiledRecipe(ColumnRecipe source, IBlockResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            strataCount = source.Strata.Count;
            strataBlocks = new NativeArray<uint>(math.max(1, strataCount), Allocator.Persistent);
            strataEnds = new NativeArray<int>(math.max(1, strataCount), Allocator.Persistent);

            //depths accumulate here so the fill loop compares against a boundary instead of subtracting
            //its way down the strata for every cell it writes.
            int cumulative = 0;
            for (int index = 0; index < strataCount; index++)
            {
                cumulative += source.Strata[index].thickness;
                strataBlocks[index] = resolver.Solid(source.Strata[index].block);
                strataEnds[index] = cumulative;
            }

            deep = resolver.Solid(source.DeepBlock);
            deepBelowY = source.DeepBelowY;
            deepBelow = source.DeepBelowBlock == null ? deep : resolver.Solid(source.DeepBelowBlock);
            crestY = source.CrestY;
            crestDepth = source.CrestBlock == null ? 0 : source.CrestDepth;
            crest = source.CrestBlock == null ? 0u : resolver.Solid(source.CrestBlock);
            submergedDepth = source.SubmergedBlock == null ? 0 : source.SubmergedDepth;
            submerged = source.SubmergedBlock == null ? 0u : resolver.Solid(source.SubmergedBlock);
        }

        public unsafe RecipeData Data
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(CompiledRecipe));
                return new RecipeData
                {
                    StrataBlocks = (uint*)strataBlocks.GetUnsafeReadOnlyPtr(),
                    StrataEnds = (int*)strataEnds.GetUnsafeReadOnlyPtr(),
                    StrataCount = strataCount,
                    Deep = deep,
                    DeepBelow = deepBelow,
                    DeepBelowY = deepBelowY,
                    Crest = crest,
                    CrestY = crestY,
                    CrestDepth = crestDepth,
                    Submerged = submerged,
                    SubmergedDepth = submergedDepth
                };
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (strataBlocks.IsCreated) strataBlocks.Dispose();
            if (strataEnds.IsCreated) strataEnds.Dispose();
        }
    }

    /// <summary>The unmanaged view of a compiled recipe.</summary>
    public unsafe struct RecipeData
    {
        public uint* StrataBlocks;
        public int* StrataEnds;
        public int StrataCount;
        public uint Deep, DeepBelow, Crest, Submerged;
        public int DeepBelowY, CrestY, CrestDepth, SubmergedDepth;
    }
}
