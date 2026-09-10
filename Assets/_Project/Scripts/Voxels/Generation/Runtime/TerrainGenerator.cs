using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One generation worker's workspace: the noise scratch and one column plan per layer.
    /// <para>
    /// Generation runs on pooled threads and the generator itself is shared and immutable, so all
    /// mutable state lives here and a context is used by exactly one thread at a time. Keeping the
    /// plans between calls means a worker that happens to take two chunks from the same column
    /// computes that column's heights once.
    /// </para>
    /// </summary>
    public sealed unsafe class GenerationContext : IDisposable
    {
        internal readonly NoiseScratch Scratch;
        private readonly LayerColumnPlan[] plans;
        private readonly NativeArray<float>[] lattices;
        private bool disposed;

        internal GenerationContext(int slotCount, IReadOnlyList<CompiledLayer> layers)
        {
            Scratch = new NoiseScratch(slotCount, ColumnFillKernel.Columns);
            plans = new LayerColumnPlan[layers.Count];
            lattices = new NativeArray<float>[layers.Count];
            for (int index = 0; index < plans.Length; index++)
            {
                plans[index] = new LayerColumnPlan(math.max(1, layers[index].Bands.Length));
                int lattice = layers[index].LatticeCount;
                if (lattice > 0) lattices[index] = new NativeArray<float>(lattice, Allocator.Persistent);
            }
        }

        internal LayerColumnPlan Plan(int layer) => plans[layer];

        internal unsafe float* Lattice(int layer)
            => lattices[layer].IsCreated ? (float*)lattices[layer].GetUnsafePtr() : null;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Scratch.Dispose();
            foreach (var plan in plans) plan.Dispose();
            foreach (var lattice in lattices) if (lattice.IsCreated) lattice.Dispose();
        }
    }

    /// <summary>
    /// A built world type: a seed, its settings, and its layers with everything compiled and resolved.
    /// Shared across generation workers and safe to call concurrently.
    /// </summary>
    public sealed class TerrainGenerator : IDisposable
    {
        /// <summary>Layers are addressed by a bit each during the bounds query, which sets the ceiling.</summary>
        public const int MaxLayers = 64;

        public string Id { get; }
        public string DisplayName { get; }
        public GenSeed Seed { get; }
        public GeneratorSettings Settings { get; }
        public IReadOnlyList<WorldLayer> Layers { get; }

        private readonly CompiledLayer[] compiled;
        //layer indices ordered by bottom bound, with a running maximum of the top bounds at or below
        //each position. Together they let a chunk rule out every layer without touching most of them.
        private readonly int[] byBottom;
        private readonly int[] prefixMaxTop;
        private readonly object contextGate = new object();
        private readonly Stack<GenerationContext> contextPool = new Stack<GenerationContext>();
        private readonly int slotCount;
        private long plansComputed, plansReused;
        private bool disposed;

        /// <summary>
        /// How many column plans have been computed, and how many were answered by a worker's kept
        /// plan instead. A plan does not depend on the chunk's Y, so a vertical stack of chunks shares
        /// one; these counters are how that is measured rather than assumed.
        /// </summary>
        public long PlansComputed => System.Threading.Interlocked.Read(ref plansComputed);
        public long PlansReused => System.Threading.Interlocked.Read(ref plansReused);

        internal TerrainGenerator(string id, string displayName, GeneratorBuildContext context, IReadOnlyList<WorldLayer> layers)
        {
            if (layers == null || layers.Count == 0)
                throw new GenerationContentException($"World type '{id}' defines no world layers.");
            if (layers.Count > MaxLayers)
                throw new GenerationContentException($"World type '{id}' defines more than {MaxLayers} world layers.");

            Id = id;
            DisplayName = displayName;
            Seed = context.Seed;
            Settings = context.Settings;
            Layers = new List<WorldLayer>(layers).AsReadOnly();

            var names = new HashSet<string>(StringComparer.Ordinal);
            compiled = new CompiledLayer[layers.Count];
            //layers hold native memory, so rejecting the tenth has to release the nine already built.
            try
            {
                for (int index = 0; index < layers.Count; index++)
                {
                    if (!names.Add(layers[index].Name))
                        throw new GenerationContentException($"World type '{id}' has two layers named '{layers[index].Name}'; seeds derive from the name, so they would be identical.");
                    compiled[index] = new CompiledLayer(layers[index], context.Seed, context.Blocks);
                }
            }
            catch
            {
                foreach (var layer in compiled) layer?.Dispose();
                throw;
            }

            int slots = 1;
            foreach (var layer in compiled) slots = math.max(slots, layer.SlotCount);
            slotCount = slots;

            byBottom = new int[layers.Count];
            for (int index = 0; index < byBottom.Length; index++) byBottom[index] = index;
            Array.Sort(byBottom, (left, right) => layers[left].BottomBound.CompareTo(layers[right].BottomBound));

            prefixMaxTop = new int[layers.Count];
            int runningTop = int.MinValue;
            for (int position = 0; position < byBottom.Length; position++)
            {
                runningTop = math.max(runningTop, layers[byBottom[position]].TopBound);
                prefixMaxTop[position] = runningTop;
            }

            //compiling the kernels here means a generation worker never discovers it needs to.
            NoiseKernelDispatch.Warm();
            ColumnFillDispatch.Warm();
            DensityDispatch.Warm();
        }

        /// <summary>
        /// Which layers a chunk's world Y span touches, as one bit per layer in authored order.
        /// <para>
        /// Authored order is the paint order, so the result is a set rather than a sequence: the caller
        /// walks its own layers in the order the author wrote them and skips the ones not in the set.
        /// </para>
        /// </summary>
        public ulong LayersOverlapping(int minY, int maxY)
        {
            ulong mask = 0;
            //everything past this point in the sorted order starts above the chunk entirely.
            int position = UpperBound(maxY) - 1;
            for (; position >= 0; position--)
            {
                //nothing at or below here reaches high enough, so the rest of the walk is wasted.
                if (prefixMaxTop[position] < minY) break;
                int layer = byBottom[position];
                if (Layers[layer].Overlaps(minY, maxY)) mask |= 1UL << layer;
            }
            return mask;
        }

        //the first position in the sorted order whose bottom bound is above maxY.
        private int UpperBound(int maxY)
        {
            int low = 0, high = byBottom.Length;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (Layers[byBottom[middle]].BottomBound <= maxY) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        /// <summary>
        /// Fills one chunk. Runs on a generation worker, touches nothing but the buffers it is given,
        /// and depends on nothing but the seed, the settings and the address, so two calls for the same
        /// chunk always agree and chunks may be generated in any order.
        /// </summary>
        public unsafe void Generate(int chunkX, int chunkY, int chunkZ, uint[] solids, uint[] fluids)
        {
            if (solids == null) throw new ArgumentNullException(nameof(solids));
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            if (disposed) throw new ObjectDisposedException(nameof(TerrainGenerator));

            int minY = chunkY * ColumnFillKernel.Edge;
            int maxY = minY + ColumnFillKernel.Edge - 1;
            ulong mask = LayersOverlapping(minY, maxY);
            //a chunk that misses every layer costs a binary search and nothing else, which is what
            //keeps the world vertically unbounded rather than merely large.
            if (mask == 0) return;

            var context = RentContext();
            try
            {
                fixed (uint* solidBuffer = solids)
                fixed (uint* fluidBuffer = fluids)
                {
                    for (int index = 0; index < compiled.Length; index++)
                    {
                        if ((mask & (1UL << index)) == 0) continue;
                        var layer = compiled[index];
                        var plan = context.Plan(index);
                        if (plan.Describes(chunkX, chunkZ)) System.Threading.Interlocked.Increment(ref plansReused);
                        else
                        {
                            layer.Plan(plan, chunkX, chunkZ, context.Scratch);
                            System.Threading.Interlocked.Increment(ref plansComputed);
                        }
                        //shape, then carve what the shape made, then decide what fills the space that
                        //is left. Reordering any of these changes what the world looks like.
                        layer.Fill(plan, minY, solidBuffer);
                        layer.Carve(context.Lattice(index), chunkX, chunkZ, minY, context.Scratch, solidBuffer);
                        layer.FillSea(minY, solidBuffer, fluidBuffer);
                        layer.FillAquifers(chunkX, chunkZ, minY, solidBuffer, fluidBuffer);
                    }
                }
            }
            finally { ReturnContext(context); }
        }

        /// <summary>The layer a world height falls in, latest-authored first. For presentation, not generation.</summary>
        public WorldLayer LayerAt(int worldY)
        {
            for (int index = Layers.Count - 1; index >= 0; index--)
                if (worldY >= Layers[index].BottomBound && worldY <= Layers[index].TopBound) return Layers[index];
            return null;
        }

        private GenerationContext RentContext()
        {
            lock (contextGate)
                if (contextPool.Count > 0) return contextPool.Pop();
            return new GenerationContext(slotCount, compiled);
        }

        private void ReturnContext(GenerationContext context)
        {
            lock (contextGate)
            {
                if (disposed) { context.Dispose(); return; }
                contextPool.Push(context);
            }
        }

        public void Dispose()
        {
            lock (contextGate)
            {
                if (disposed) return;
                disposed = true;
                while (contextPool.Count > 0) contextPool.Pop().Dispose();
            }
            foreach (var layer in compiled) layer.Dispose();
        }
    }
}
