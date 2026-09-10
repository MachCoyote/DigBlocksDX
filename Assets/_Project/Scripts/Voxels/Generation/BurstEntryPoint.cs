using System;
using Unity.Burst;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One Burst-compiled kernel entry point, compiled once and reused.
    /// <para>
    /// Generation runs on pooled worker threads, so the compile has to happen somewhere a stall is
    /// harmless. <see cref="Warm"/> forces it when a generator is built; reaching <see cref="Compiled"/>
    /// without warming still works, it just pays for the compile wherever it happens to be.
    /// </para>
    /// </summary>
    public sealed class BurstEntryPoint<TDelegate> where TDelegate : Delegate
    {
        private readonly object gate = new object();
        private readonly TDelegate implementation;
        private FunctionPointer<TDelegate> compiled;
        private bool ready;

        public BurstEntryPoint(TDelegate implementation)
            => this.implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));

        public FunctionPointer<TDelegate> Compiled
        {
            get
            {
                if (ready) return compiled;
                Warm();
                return compiled;
            }
        }

        /// <summary>Compiles now, on the calling thread. Safe to call more than once.</summary>
        public void Warm()
        {
            lock (gate)
            {
                if (ready) return;
                compiled = BurstCompiler.CompileFunctionPointer(implementation);
                ready = true;
            }
        }
    }
}
