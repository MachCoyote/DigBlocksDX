using System;
using System.Runtime.InteropServices;
using Unity.Burst;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>Which side of a band's solid interval its authored surface describes.</summary>
    public enum BandDirection : byte
    {
        /// <summary>The free face is on top and the fill runs down from it. Ordinary ground.</summary>
        Up = 0,
        /// <summary>The free face is underneath and the fill runs up from it. Ceilings and undersides.</summary>
        Down = 1
    }

    /// <summary>
    /// A hand-written heightmap, filling <paramref name="result"/> for a batch of columns. The whole-band
    /// counterpart to <see cref="NoiseExpr.External"/>, for a surface an author would rather write
    /// outright than assemble from instructions.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void BandHeightFunction(float* x, float* z, float* result, int count, uint seed);

    /// <summary>Where hand-written band heightmaps are registered by id.</summary>
    public static class BandHeightRegistry
    {
        private static readonly BurstFunctionRegistry<BandHeightFunction> Entries =
            new BurstFunctionRegistry<BandHeightFunction>("band height function");

        public static void Register(string id, BandHeightFunction implementation) => Entries.Register(id, implementation);
        public static bool TryResolve(string id, out FunctionPointer<BandHeightFunction> pointer) => Entries.TryResolve(id, out pointer);
        public static FunctionPointer<BandHeightFunction> Resolve(string id) => Entries.Resolve(id);
        public static bool Contains(string id) => Entries.Contains(id);
        internal static void Clear() => Entries.Clear();
    }

    /// <summary>
    /// One of a band's two heights, given either as an authored expression or as a registered
    /// hand-written function. Expressions convert implicitly, so the common case reads as if this type
    /// were not here.
    /// </summary>
    public sealed class BandSurface
    {
        internal readonly NoiseExpr Expression;
        internal readonly string FunctionId;
        internal readonly string SeedName;

        private BandSurface(NoiseExpr expression, string functionId, string seedName)
        {
            Expression = expression; FunctionId = functionId; SeedName = seedName;
        }

        public static implicit operator BandSurface(NoiseExpr expression)
            => new BandSurface(expression ?? throw new ArgumentNullException(nameof(expression)), null, null);

        /// <summary>A flat surface at a fixed world height.</summary>
        public static BandSurface At(float worldY) => NoiseExpr.Constant(worldY);

        /// <summary>A surface produced by a registered Burst function, seeded under <paramref name="seedName"/>.</summary>
        public static BandSurface Function(string functionId, string seedName)
        {
            if (string.IsNullOrWhiteSpace(functionId)) throw new ArgumentException("A band height function needs a registry id.", nameof(functionId));
            if (string.IsNullOrWhiteSpace(seedName)) throw new ArgumentException("A band height function needs a stable seed name.", nameof(seedName));
            return new BandSurface(null, functionId, seedName);
        }
    }

    /// <summary>
    /// One solid interval per column, described by a free face and how far the fill runs from it.
    /// <para>
    /// An <see cref="BandDirection.Up"/> band fills from its extent up to its surface with depth
    /// measured downward; a <see cref="BandDirection.Down"/> band fills from its surface up to its
    /// extent with depth measured upward. A layer's stone body with rolling grass on its underside is
    /// one of each.
    /// </para>
    /// <para>
    /// Bands paint in order and later writes win. That is the contract, not an accident: overlapping
    /// bands are how an author composes shapes, so nothing here guards against them.
    /// </para>
    /// </summary>
    public sealed class SurfaceBand
    {
        public string Name { get; }
        public BandDirection Direction { get; }
        public BandSurface Surface { get; }
        /// <summary>Where the fill stops. Null means the layer's bound on that side.</summary>
        public BandSurface Extent { get; }
        public ColumnRecipe Fill { get; }

        public SurfaceBand(string name, BandDirection direction, BandSurface surface, ColumnRecipe fill,
            BandSurface extent = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A band needs a name.", nameof(name));
            Name = name;
            Direction = direction;
            Surface = surface ?? throw new ArgumentNullException(nameof(surface));
            Fill = fill ?? throw new ArgumentNullException(nameof(fill));
            Extent = extent;
        }
    }
}
