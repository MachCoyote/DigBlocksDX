using System.Runtime.InteropServices;
using Unity.Burst;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// The signature a hand-written noise node must have. It receives the sample coordinate, the seed
    /// derived for this node from the world seed, and the two operand values the expression supplied.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate float NoiseExternal(float x, float y, float z, uint seed, float a, float b);

    /// <summary>
    /// Where an author registers a hand-written Burst function so noise expressions can call it by id
    /// through <see cref="NoiseExpr.External"/>. See <see cref="BurstFunctionRegistry{TDelegate}"/> for
    /// what an implementation must look like.
    /// </summary>
    public static class NoiseExternalRegistry
    {
        private static readonly BurstFunctionRegistry<NoiseExternal> Entries = new BurstFunctionRegistry<NoiseExternal>("noise external");

        public static void Register(string id, NoiseExternal implementation) => Entries.Register(id, implementation);
        public static bool TryResolve(string id, out FunctionPointer<NoiseExternal> pointer) => Entries.TryResolve(id, out pointer);
        public static FunctionPointer<NoiseExternal> Resolve(string id) => Entries.Resolve(id);
        public static bool Contains(string id) => Entries.Contains(id);
        internal static void Clear() => Entries.Clear();
    }
}
