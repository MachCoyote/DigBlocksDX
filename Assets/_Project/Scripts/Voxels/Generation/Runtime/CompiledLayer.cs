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

    /// <summary>A density stage with its field compiled and its lattice worked out.</summary>
    internal sealed class CompiledDensity : IDisposable
    {
        private readonly NoiseProgram program;
        private Unity.Collections.NativeArray<DensityGradientData> gradients;
        private readonly int gradientCount;

        public readonly int3 Resolution, LatticeSize;
        public readonly int LatticeCount;
        public readonly float Threshold;
        public readonly uint AddBlock;
        public readonly DensityMode Mode;

        public int SlotCount => program.SlotCount;

        public CompiledDensity(DensityStage stage, GenSeed layerSeed, IBlockResolver blocks)
        {
            program = NoiseProgram.Compile(stage.Field, layerSeed.Derive(stage.Name));
            Resolution = stage.Resolution;
            LatticeSize = ColumnFillKernel.Edge / Resolution + 1;
            LatticeCount = LatticeSize.x * LatticeSize.y * LatticeSize.z;
            Threshold = stage.Threshold;
            Mode = stage.Mode;
            AddBlock = stage.Mode == DensityMode.Add ? blocks.Solid(stage.AddBlock) : 0u;

            gradientCount = stage.Gradients.Count;
            gradients = new Unity.Collections.NativeArray<DensityGradientData>(
                math.max(1, gradientCount), Unity.Collections.Allocator.Persistent);
            for (int index = 0; index < gradientCount; index++)
            {
                var gradient = stage.Gradients[index];
                gradients[index] = new DensityGradientData
                {
                    FromY = gradient.FromY, ToY = gradient.ToY,
                    FromBias = gradient.FromBias, ToBias = gradient.ToBias
                };
            }
        }

        /// <summary>
        /// Fills the lattice for one chunk. Sampling runs in batches the scratch can hold, so a fine
        /// lattice is slower but never larger than the workspace.
        /// </summary>
        public unsafe void Sample(NoiseScratch scratch, float* lattice, int chunkX, int chunkZ, int chunkMinY)
        {
            var gradientData = (DensityGradientData*)Unity.Collections.LowLevel.Unsafe
                .NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(gradients);
            int baseX = chunkX * ColumnFillKernel.Edge, baseZ = chunkZ * ColumnFillKernel.Edge;
            int written = 0;

            while (written < LatticeCount)
            {
                int batch = math.min(scratch.Capacity, LatticeCount - written);
                for (int offset = 0; offset < batch; offset++)
                {
                    int point = written + offset;
                    int x = point % LatticeSize.x;
                    int z = point / LatticeSize.x % LatticeSize.z;
                    int y = point / (LatticeSize.x * LatticeSize.z);
                    scratch.X[offset] = baseX + x * Resolution.x;
                    scratch.Y[offset] = chunkMinY + y * Resolution.y;
                    scratch.Z[offset] = baseZ + z * Resolution.z;
                }

                var noiseBatch = scratch.Batch(batch, withY: true);
                program.Evaluate(ref noiseBatch);
                if (gradientCount > 0)
                    DensityDispatch.CompiledBias.Invoke(scratch.Result, scratch.Y, batch, gradientData, gradientCount);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(
                    lattice + written, scratch.Result, (long)batch * sizeof(float));
                written += batch;
            }
        }

        public unsafe void Apply(float* lattice, int chunkMinY, int layerBottom, int layerTop, uint* solids)
        {
            var data = new DensityData
            {
                Lattice = lattice,
                Resolution = Resolution,
                LatticeSize = LatticeSize,
                ChunkMinY = chunkMinY,
                LayerBottom = layerBottom,
                LayerTop = layerTop,
                Threshold = Threshold,
                AddBlock = AddBlock,
                Mode = (byte)Mode
            };
            DensityDispatch.CompiledApply.Invoke(&data, solids);
        }

        public void Dispose()
        {
            program?.Dispose();
            if (gradients.IsCreated) gradients.Dispose();
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
        public readonly CompiledDensity Density;
        public readonly AquiferStage Aquifers;
        public readonly uint AquiferFluid;
        public readonly uint AquiferSeed;

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

                if (layer.Density != null) Density = new CompiledDensity(layer.Density, layerSeed, blocks);
                Aquifers = layer.Aquifers;
                if (Aquifers != null)
                {
                    AquiferFluid = blocks.Fluid(Aquifers.Fluid);
                    AquiferSeed = layerSeed.Derive(Aquifers.Name).Lattice;
                }
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
                int slots = Density?.SlotCount ?? 1;
                foreach (var band in Bands) slots = math.max(slots, band.SlotCount);
                return slots;
            }
        }

        public int LatticeCount => Density?.LatticeCount ?? 0;

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

        public unsafe void Carve(float* lattice, int chunkX, int chunkZ, int chunkMinY, NoiseScratch scratch, uint* solids)
        {
            if (Density == null) return;
            Density.Sample(scratch, lattice, chunkX, chunkZ, chunkMinY);
            Density.Apply(lattice, chunkMinY, Source.BottomBound, Source.TopBound, solids);
        }

        public unsafe void FillSea(int chunkMinY, uint* solids, uint* fluids)
        {
            if (!HasSea) return;
            var data = new SeaFillData
            {
                ChunkMinY = chunkMinY,
                //where a layer has aquifers, they own the water below their top and the sea owns only
                //what is above it. Otherwise a blanket sea fill would flood every carved cavern down to
                //the floor, and no region could be dry.
                LayerBottom = Aquifers == null ? Source.BottomBound : math.max(Source.BottomBound, Aquifers.MaxY + 1),
                LayerTop = Source.TopBound,
                SeaLevel = SeaLevel,
                Fluid = SeaFluid
            };
            ColumnFillDispatch.CompiledSea.Invoke(&data, solids, fluids);
        }

        public unsafe void FillAquifers(int chunkX, int chunkZ, int chunkMinY, uint* solids, uint* fluids)
        {
            if (Aquifers == null) return;
            var data = new AquiferData
            {
                Seed = AquiferSeed,
                CellSize = Aquifers.CellSize,
                ChunkMinY = chunkMinY,
                MinY = math.max(Aquifers.MinY, Source.BottomBound),
                MaxY = math.min(Aquifers.MaxY, Source.TopBound),
                BaseLevel = Aquifers.BaseLevel,
                LevelJitter = Aquifers.LevelJitter,
                ChunkX = chunkX,
                ChunkZ = chunkZ,
                Fluid = AquiferFluid,
                DryChance = Aquifers.DryChance
            };
            DensityDispatch.CompiledAquifers.Invoke(&data, solids, fluids);
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
            Density?.Dispose();
        }
    }
}
