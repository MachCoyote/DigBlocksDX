using System;
using System.Collections.Generic;
using Unity.Burst;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// Where authored Burst functions are registered by id so generation content can name them.
    /// <para>
    /// Compilation happens on the registering thread rather than at first use, because first use is on
    /// a chunk-generation worker and a compile stall there shows up as a hitch in terrain streaming.
    /// </para>
    /// <para>
    /// A registered implementation must be a static method carrying
    /// <c>[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]</c> on a
    /// type carrying <c>[BurstCompile]</c>, plus <c>[AOT.MonoPInvokeCallback]</c> so it survives
    /// IL2CPP, and it must obey the same determinism rules as the rest of generation.
    /// </para>
    /// </summary>
    public sealed class BurstFunctionRegistry<TDelegate> where TDelegate : Delegate
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, FunctionPointer<TDelegate>> entries = new Dictionary<string, FunctionPointer<TDelegate>>(StringComparer.Ordinal);
        private readonly string description;

        public BurstFunctionRegistry(string description) => this.description = description;

        public void Register(string id, TDelegate implementation)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException($"A {description} needs an id.", nameof(id));
            if (implementation == null) throw new ArgumentNullException(nameof(implementation));
            var compiled = BurstCompiler.CompileFunctionPointer(implementation);
            lock (gate) entries[id] = compiled;
        }

        public bool TryResolve(string id, out FunctionPointer<TDelegate> pointer)
        {
            if (id == null) { pointer = default; return false; }
            lock (gate) return entries.TryGetValue(id, out pointer);
        }

        public FunctionPointer<TDelegate> Resolve(string id)
        {
            if (TryResolve(id, out var pointer)) return pointer;
            throw new InvalidOperationException($"No {description} is registered under '{id}'.");
        }

        public bool Contains(string id) => TryResolve(id, out _);

        public void Clear()
        {
            lock (gate) entries.Clear();
        }
    }
}
