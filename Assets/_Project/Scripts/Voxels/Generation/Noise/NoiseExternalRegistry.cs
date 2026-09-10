using System;
using System.Collections.Generic;
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
    /// Where an author registers a hand-written Burst function so noise expressions can call it by id.
    /// <para>
    /// The implementation must be a static method carrying
    /// <c>[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]</c> on a
    /// type carrying <c>[BurstCompile]</c>, and <c>[AOT.MonoPInvokeCallback(typeof(NoiseExternal))]</c>
    /// so it survives IL2CPP. It must obey the same determinism rules as the rest of generation: no
    /// transcendental functions, and integer-only seeding.
    /// </para>
    /// </summary>
    public static class NoiseExternalRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, FunctionPointer<NoiseExternal>> Entries = new Dictionary<string, FunctionPointer<NoiseExternal>>(StringComparer.Ordinal);

        /// <summary>
        /// Compiles and registers one implementation. Compilation happens here, on the calling thread,
        /// rather than at first use on a generation worker.
        /// </summary>
        public static void Register(string id, NoiseExternal implementation)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An external needs an id.", nameof(id));
            if (implementation == null) throw new ArgumentNullException(nameof(implementation));
            var compiled = BurstCompiler.CompileFunctionPointer(implementation);
            lock (Gate) Entries[id] = compiled;
        }

        public static bool TryResolve(string id, out FunctionPointer<NoiseExternal> pointer)
        {
            lock (Gate) return Entries.TryGetValue(id, out pointer);
        }

        public static FunctionPointer<NoiseExternal> Resolve(string id)
        {
            if (TryResolve(id, out var pointer)) return pointer;
            throw new InvalidOperationException($"No noise external is registered under '{id}'.");
        }

        public static bool Contains(string id)
        {
            lock (Gate) return Entries.ContainsKey(id);
        }

        internal static void Clear()
        {
            lock (Gate) Entries.Clear();
        }
    }
}
