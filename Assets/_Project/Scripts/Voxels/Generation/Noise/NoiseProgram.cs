using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// A compiled, seeded noise field, held in native memory and ready to evaluate from a generation
    /// worker. Build one when a generator is built and keep it for the world's lifetime; compiling is
    /// not something to do per chunk.
    /// </summary>
    public sealed class NoiseProgram : IDisposable
    {
        private NativeArray<NoiseOp> ops;
        private NativeArray<SplineKnot> knots;
        private NativeArray<IntPtr> externals;
        private readonly int opCount, knotCount, externalCount;
        private bool disposed;

        public int SlotCount { get; }
        public int ResultSlot { get; }
        /// <summary>True when the field samples the Y coordinate, so a caller must supply a Y plane.</summary>
        public bool UsesY { get; }
        public int InstructionCount => opCount;

        private NoiseProgram(CompiledNoise compiled)
        {
            opCount = compiled.Ops.Length;
            knotCount = compiled.Knots.Length;
            externalCount = compiled.Externals.Length;
            SlotCount = compiled.SlotCount;
            ResultSlot = compiled.ResultSlot;
            UsesY = compiled.UsesY;

            //externals are resolved before anything is allocated, because an unregistered id is the one
            //failure this constructor can hit and native memory taken before it would simply leak.
            var resolved = new IntPtr[externalCount];
            for (int index = 0; index < externalCount; index++)
                resolved[index] = NoiseExternalRegistry.Resolve(compiled.Externals[index]).Value;

            //a zero-length native array has no address to hand the kernel, and the kernel reads none of
            //these when the matching count is zero, so one spare element keeps the pointers valid.
            ops = new NativeArray<NoiseOp>(math.max(1, opCount), Allocator.Persistent);
            knots = new NativeArray<SplineKnot>(math.max(1, knotCount), Allocator.Persistent);
            externals = new NativeArray<IntPtr>(math.max(1, externalCount), Allocator.Persistent);

            NativeArray<NoiseOp>.Copy(compiled.Ops, ops, opCount);
            if (knotCount > 0) NativeArray<SplineKnot>.Copy(compiled.Knots, knots, knotCount);
            for (int index = 0; index < externalCount; index++) externals[index] = resolved[index];
        }

        /// <summary>Compiles an authored field against a world seed. The expression is not retained.</summary>
        public static NoiseProgram Compile(NoiseExpr root, GenSeed seed)
            => new NoiseProgram(NoiseCompiler.Compile(root, seed));

        public unsafe NoiseProgramData Data
        {
            get
            {
                RequireAlive();
                return new NoiseProgramData
                {
                    Ops = (NoiseOp*)ops.GetUnsafeReadOnlyPtr(),
                    Knots = (SplineKnot*)knots.GetUnsafeReadOnlyPtr(),
                    Externals = (IntPtr*)externals.GetUnsafeReadOnlyPtr(),
                    OpCount = opCount,
                    KnotCount = knotCount,
                    ExternalCount = externalCount,
                    SlotCount = SlotCount,
                    ResultSlot = ResultSlot
                };
            }
        }

        /// <summary>
        /// Evaluates the field over a prepared batch. <paramref name="burst"/> chooses the compiled
        /// kernel; the managed path exists so the same instructions can be run without Burst, which is
        /// what the agreement test compares against.
        /// </summary>
        public unsafe void Evaluate(ref NoiseBatch batch, bool burst = true)
        {
            RequireAlive();
            if (UsesY && batch.Y == null)
                throw new InvalidOperationException("This field samples Y, so the batch needs a Y plane.");
            var data = Data;
            fixed (NoiseBatch* pointer = &batch)
            {
                if (burst) NoiseKernelDispatch.Compiled.Invoke(&data, pointer);
                else NoiseKernel.Evaluate(&data, pointer);
            }
        }

        /// <summary>One sample. For tests, tools and inspection, not for filling chunks.</summary>
        public unsafe float Sample(float x, float y, float z, bool burst = true)
        {
            RequireAlive();
            float* slots = stackalloc float[SlotCount];
            float result = 0f;
            var batch = new NoiseBatch { X = &x, Y = &y, Z = &z, Slots = slots, Result = &result, Count = 1 };
            var data = Data;
            if (burst) NoiseKernelDispatch.Compiled.Invoke(&data, &batch);
            else NoiseKernel.Evaluate(&data, &batch);
            return result;
        }

        public float Sample(float x, float z, bool burst = true) => Sample(x, 0f, z, burst);

        private void RequireAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(NoiseProgram));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ops.IsCreated) ops.Dispose();
            if (knots.IsCreated) knots.Dispose();
            if (externals.IsCreated) externals.Dispose();
        }
    }

    /// <summary>Holds the one Burst compilation of the noise kernel.</summary>
    public static unsafe class NoiseKernelDispatch
    {
        private static readonly BurstEntryPoint<NoiseEvaluate> Entry =
            new BurstEntryPoint<NoiseEvaluate>(NoiseKernel.Evaluate);

        public static FunctionPointer<NoiseEvaluate> Compiled => Entry.Compiled;

        /// <summary>Compiles the kernel now, on the calling thread. Safe to call more than once.</summary>
        public static void Warm() => Entry.Warm();
    }

    /// <summary>
    /// Workspace for evaluating a program over many points at once. Owns the coordinate planes, the
    /// per-slot scratch and the results, so a caller allocates one of these per worker rather than one
    /// per chunk.
    /// </summary>
    public sealed class NoiseScratch : IDisposable
    {
        private NativeArray<float> storage;
        private readonly int capacity, slotCount;
        private bool disposed;

        public int Capacity => capacity;

        public NoiseScratch(int slotCount, int capacity)
        {
            if (slotCount < 1) throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            this.slotCount = slotCount;
            //three coordinate planes, one result plane, then the slots.
            storage = new NativeArray<float>((4 + slotCount) * capacity, Allocator.Persistent);
        }

        /// <summary>A scratch sized for one program, with room for <paramref name="capacity"/> samples.</summary>
        public static NoiseScratch For(NoiseProgram program, int capacity)
            => new NoiseScratch((program ?? throw new ArgumentNullException(nameof(program))).SlotCount, capacity);

        public unsafe float* X => Base;
        public unsafe float* Y => Base + capacity;
        public unsafe float* Z => Base + 2 * capacity;
        public unsafe float* Result => Base + 3 * capacity;

        private unsafe float* Base
        {
            get
            {
                if (disposed) throw new ObjectDisposedException(nameof(NoiseScratch));
                return (float*)storage.GetUnsafePtr();
            }
        }

        /// <summary>A batch over the first <paramref name="count"/> points of this scratch.</summary>
        public unsafe NoiseBatch Batch(int count, bool withY)
        {
            if (count < 1 || count > capacity) throw new ArgumentOutOfRangeException(nameof(count));
            return new NoiseBatch
            {
                X = X, Y = withY ? Y : null, Z = Z,
                Slots = Base + 4 * capacity,
                Result = Result,
                Count = count
            };
        }

        public bool Fits(NoiseProgram program) => program != null && program.SlotCount <= slotCount;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (storage.IsCreated) storage.Dispose();
        }
    }
}
