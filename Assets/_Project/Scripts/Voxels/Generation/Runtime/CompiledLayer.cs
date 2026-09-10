using System;
using Unity.Burst;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>One of a band's heights, ready to evaluate: either a compiled program or a registered function.</summary>
    internal sealed class CompiledSurface : IDisposable
    {
        private readonly NoiseProgram program;
        private readonly FunctionPointer<BandHeightFunction> function;
        private readonly uint functionSeed;
        private readonly bool isFunction;

        public int SlotCount => isFunction ? 1 : program.SlotCount;

        public CompiledSurface(BandSurface surface, GenSeed seed, string context)
        {
            if (surface.Expression != null)
            {
                var candidate = NoiseProgram.Compile(surface.Expression, seed);
                if (candidate.UsesY)
                {
                    //rejecting it after compiling means the program's native memory is ours to release.
                    candidate.Dispose();
                    throw new GenerationContentException($"{context} samples Y, but a surface height is a function of X and Z alone.");
                }
                program = candidate;
                return;
            }
            isFunction = true;
            function = BandHeightRegistry.Resolve(surface.FunctionId);
            functionSeed = seed.Derive(surface.SeedName).Lattice;
        }

        /// <summary>Writes one height per sample into the scratch's result plane.</summary>
        public unsafe void Evaluate(NoiseScratch scratch, int count)
        {
            if (isFunction) function.Invoke(scratch.X, scratch.Z, scratch.Result, count, functionSeed);
            else
            {
                var batch = scratch.Batch(count, withY: false);
                program.Evaluate(ref batch);
            }
        }

        public void Dispose() => program?.Dispose();
    }

    internal sealed class CompiledBand : IDisposable
    {
        public readonly BandDirection Direction;
        public readonly CompiledSurface Surface;
        public readonly CompiledSurface Extent;
        public readonly CompiledRecipe Recipe;

        public CompiledBand(SurfaceBand band, GenSeed layerSeed, IBlockResolver blocks)
        {
            GenSeed seed = layerSeed.Derive(band.Name);
            Direction = band.Direction;
            //each part owns native memory, so a later one failing has to release the earlier ones
            //rather than leave a rejected generator's allocations behind.
            try
            {
                Surface = new CompiledSurface(band.Surface, seed.Derive("surface"), $"Band '{band.Name}' surface");
                Extent = band.Extent == null ? null : new CompiledSurface(band.Extent, seed.Derive("extent"), $"Band '{band.Name}' extent");
                Recipe = band.Fill.Compile(blocks);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int SlotCount => math.max(Surface.SlotCount, Extent?.SlotCount ?? 1);

        public void Dispose()
        {
            Surface?.Dispose();
            Extent?.Dispose();
            Recipe?.Dispose();
        }
    }

    /// <summary>
    /// A world layer with its programs compiled, its block keys resolved and its native memory
    /// allocated. Built once with the generator and shared by every generation worker, so it holds no
    /// mutable state of its own; scratch lives in the per-call context instead.
    /// </summary>
    internal sealed class CompiledLayer : IDisposable
    {
        public readonly WorldLayer Source;
        public readonly CompiledBand[] Bands;
        public readonly int SeaLevel;
        public readonly uint SeaFluid;
        public readonly bool HasSea;

        public CompiledLayer(WorldLayer layer, GenSeed worldSeed, IBlockResolver blocks)
        {
            Source = layer;
            GenSeed layerSeed = worldSeed.Derive(layer.Name);
            Bands = new CompiledBand[layer.Bands.Count];
            try
            {
                for (int index = 0; index < Bands.Length; index++)
                    Bands[index] = new CompiledBand(layer.Bands[index], layerSeed, blocks);

                HasSea = layer.SeaLevel.HasValue;
                SeaLevel = layer.SeaLevel ?? 0;
                SeaFluid = HasSea ? blocks.Fluid(layer.SeaFluid) : 0u;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int SlotCount
        {
            get
            {
                int slots = 1;
                foreach (var band in Bands) slots = math.max(slots, band.SlotCount);
                return slots;
            }
        }

        /// <summary>
        /// Computes this layer's surface and extent heights for one chunk column. The result does not
        /// depend on the chunk's Y, which is what makes it worth keeping.
        /// </summary>
        public unsafe void Plan(LayerColumnPlan plan, int chunkX, int chunkZ, NoiseScratch scratch)
        {
            const int edge = ColumnFillKernel.Edge;
            const int columns = ColumnFillKernel.Columns;

            //every band samples the same 1024 world positions, so the coordinates are laid out once.
            float baseX = chunkX * edge, baseZ = chunkZ * edge;
            float* sampleX = scratch.X;
            float* sampleZ = scratch.Z;
            for (int z = 0; z < edge; z++)
            for (int x = 0; x < edge; x++)
            {
                int column = x + edge * z;
                sampleX[column] = baseX + x;
                sampleZ[column] = baseZ + z;
            }

            for (int index = 0; index < Bands.Length; index++)
            {
                var band = Bands[index];
                band.Surface.Evaluate(scratch, columns);
                Quantize(scratch.Result, plan.Surface(index), columns);

                if (band.Extent != null)
                {
                    band.Extent.Evaluate(scratch, columns);
                    Quantize(scratch.Result, plan.Extent(index), columns);
                }
            }

            plan.MarkPlanned(chunkX, chunkZ);
        }

        public unsafe void Fill(LayerColumnPlan plan, int chunkMinY, uint* solids)
        {
            for (int index = 0; index < Bands.Length; index++)
            {
                var band = Bands[index];
                var data = new BandFillData
                {
                    SurfaceY = plan.Surface(index),
                    ExtentY = plan.Extent(index),
                    Recipe = band.Recipe.Data,
                    ChunkMinY = chunkMinY,
                    LayerBottom = Source.BottomBound,
                    LayerTop = Source.TopBound,
                    SeaLevel = SeaLevel,
                    FallbackExtentY = band.Direction == BandDirection.Up ? Source.BottomBound : Source.TopBound,
                    Direction = (byte)band.Direction,
                    HasSea = (byte)(HasSea ? 1 : 0),
                    HasExtent = (byte)(band.Extent != null ? 1 : 0)
                };
                ColumnFillDispatch.Compiled.Invoke(&data, solids);
            }
        }

        public unsafe void FillSea(int chunkMinY, uint* solids, uint* fluids)
        {
            if (!HasSea) return;
            var data = new SeaFillData
            {
                ChunkMinY = chunkMinY,
                LayerBottom = Source.BottomBound,
                LayerTop = Source.TopBound,
                SeaLevel = SeaLevel,
                Fluid = SeaFluid
            };
            ColumnFillDispatch.CompiledSea.Invoke(&data, solids, fluids);
        }

        //heights are brought inside the integer range before conversion, so an expression that runs
        //away produces an absurd but well-defined surface rather than undefined behaviour. The
        //comparisons are written negated because a NaN compares false against everything, and that
        //sends it to a bound instead of into an undefined conversion.
        private static unsafe void Quantize(float* source, int* destination, int count)
        {
            const float limit = 1 << 28;
            for (int index = 0; index < count; index++)
            {
                float value = source[index];
                if (!(value > -limit)) value = -limit;
                else if (!(value < limit)) value = limit;
                destination[index] = (int)math.floor(value);
            }
        }

        public void Dispose()
        {
            foreach (var band in Bands) band?.Dispose();
        }
    }
}
