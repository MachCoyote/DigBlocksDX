using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>A sampled density lattice and what to do with the field it describes.</summary>
    public unsafe struct DensityData
    {
        /// <summary>One value per lattice point, indexed x + size.x * (z + size.z * y).</summary>
        public float* Lattice;
        /// <summary>Lattice spacing in blocks.</summary>
        public int3 Resolution;
        /// <summary>Lattice points per axis, which is <c>32 / Resolution + 1</c>.</summary>
        public int3 LatticeSize;
        public int ChunkMinY, LayerBottom, LayerTop;
        public float Threshold;
        public uint AddBlock;
        public byte Mode;
    }

    /// <summary>One height-dependent bias, as the kernel reads it.</summary>
    public struct DensityGradientData
    {
        public float FromY, ToY, FromBias, ToBias;
    }

    /// <summary>Everything the aquifer fill needs, all of it integer-derived.</summary>
    public unsafe struct AquiferData
    {
        public uint Seed;
        public int3 CellSize;
        public int ChunkMinY, MinY, MaxY, BaseLevel, LevelJitter;
        public int ChunkX, ChunkZ;
        public uint Fluid;
        public float DryChance;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void DensityBias(float* lattice, float* worldY, int count, DensityGradientData* gradients, int gradientCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void DensityApply(DensityData* density, uint* solids);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void AquiferApply(AquiferData* aquifer, uint* solids, uint* fluids);

    /// <summary>
    /// Turns a three-dimensional density field into solid and air.
    /// <para>
    /// The field is sampled on a coarse lattice and interpolated between rather than evaluated per
    /// cell. A chunk holds 32768 cells; a lattice of four by eight by four holds 405. That ratio is
    /// what makes opting a layer into three dimensions cost about what a heightmap costs instead of
    /// eighty times one, and it is why the lattice spacing has to divide the chunk edge evenly:
    /// neighbouring chunks must interpolate between the same sample points or terrain will not meet
    /// at the seam.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class DensityKernel
    {
        private const int Edge = ChunkLayout.Edge;
        private const int Columns = Edge * Edge;

        /// <summary>
        /// Adds the height-dependent bias to each sampled lattice value. Doing it here rather than in
        /// managed code keeps every float operation in generation under the same strict rules.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(DensityBias))]
        public static void Bias(float* lattice, float* worldY, int count, DensityGradientData* gradients, int gradientCount)
        {
            for (int gradient = 0; gradient < gradientCount; gradient++)
            {
                DensityGradientData entry = gradients[gradient];
                float span = entry.ToY - entry.FromY;
                for (int index = 0; index < count; index++)
                {
                    float t = (worldY[index] - entry.FromY) / span;
                    t = math.min(math.max(t, 0f), 1f);
                    lattice[index] += entry.FromBias + t * (entry.ToBias - entry.FromBias);
                }
            }
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(DensityApply))]
        public static void Apply(DensityData* density, uint* solids)
        {
            int3 resolution = density->Resolution;
            int3 size = density->LatticeSize;
            bool carve = density->Mode == (byte)DensityMode.Carve;
            float threshold = density->Threshold;
            uint addBlock = density->AddBlock;
            float* lattice = density->Lattice;

            //reciprocals of exact powers of two are exact, and the spacings that divide 32 all are, so
            //this costs nothing in precision and saves a division per cell.
            float inverseX = 1f / resolution.x, inverseY = 1f / resolution.y, inverseZ = 1f / resolution.z;

            for (int localY = 0; localY < Edge; localY++)
            {
                int worldY = density->ChunkMinY + localY;
                if (worldY < density->LayerBottom || worldY > density->LayerTop) continue;

                int cellY = localY / resolution.y;
                float ty = (localY - cellY * resolution.y) * inverseY;
                uint* slice = solids + (long)localY * Columns;

                for (int localZ = 0; localZ < Edge; localZ++)
                {
                    int cellZ = localZ / resolution.z;
                    float tz = (localZ - cellZ * resolution.z) * inverseZ;

                    //the four lattice edges running along x for this (y, z) are fixed, so the y and z
                    //blends are done once per row and only the x blend runs per cell.
                    int rowLow = cellZ * size.x + cellY * size.x * size.z;
                    int rowHigh = rowLow + size.x * size.z;
                    int rowLowNext = rowLow + size.x;
                    int rowHighNext = rowHigh + size.x;

                    for (int localX = 0; localX < Edge; localX++)
                    {
                        int cellX = localX / resolution.x;
                        float tx = (localX - cellX * resolution.x) * inverseX;

                        float c00 = Blend(lattice[rowLow + cellX], lattice[rowLow + cellX + 1], tx);
                        float c01 = Blend(lattice[rowLowNext + cellX], lattice[rowLowNext + cellX + 1], tx);
                        float c10 = Blend(lattice[rowHigh + cellX], lattice[rowHigh + cellX + 1], tx);
                        float c11 = Blend(lattice[rowHighNext + cellX], lattice[rowHighNext + cellX + 1], tx);

                        float value = Blend(Blend(c00, c01, tz), Blend(c10, c11, tz), ty);

                        if (carve) { if (value < threshold) slice[localX + Edge * localZ] = 0u; }
                        else if (value >= threshold) slice[localX + Edge * localZ] = addBlock;
                    }
                }
            }
        }

        /// <summary>
        /// Fills carved space that sits below its region's water table.
        /// <para>
        /// Each region's level is drawn from the seed and the region's own coordinates, so two chunks
        /// sharing a region agree without consulting each other, and a region either holds water to a
        /// flat level or holds none at all. Flatness is the point: a water surface that followed a
        /// smooth field would not look like water.
        /// </para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(AquiferApply))]
        public static void Aquifers(AquiferData* aquifer, uint* solids, uint* fluids)
        {
            int3 cell = aquifer->CellSize;
            int baseX = aquifer->ChunkX * Edge, baseZ = aquifer->ChunkZ * Edge;

            for (int localY = 0; localY < Edge; localY++)
            {
                int worldY = aquifer->ChunkMinY + localY;
                if (worldY < aquifer->MinY || worldY > aquifer->MaxY) continue;
                int cellY = FloorDiv(worldY, cell.y);

                long offset = (long)localY * Columns;
                uint* solidSlice = solids + offset;
                uint* fluidSlice = fluids + offset;

                for (int localZ = 0; localZ < Edge; localZ++)
                {
                    int cellZ = FloorDiv(baseZ + localZ, cell.z);
                    for (int localX = 0; localX < Edge; localX++)
                    {
                        int index = localX + Edge * localZ;
                        if (solidSlice[index] != 0u) continue;

                        int cellX = FloorDiv(baseX + localX, cell.x);
                        int level = LevelOf(aquifer, cellX, cellY, cellZ);
                        if (level != int.MinValue && worldY <= level) fluidSlice[index] = aquifer->Fluid;
                    }
                }
            }
        }

        //kept identical to AquiferStage.LevelAt so tools and tests can predict what generation does.
        private static int LevelOf(AquiferData* aquifer, int cellX, int cellY, int cellZ)
        {
            uint hash = GenHash.Lattice(aquifer->Seed, cellX, cellY, cellZ);
            if (GenHash.UnitFloat(hash) < aquifer->DryChance) return int.MinValue;
            uint levelHash = GenHash.Mix32(hash ^ 0x9E3779B1u);
            int jitter = aquifer->LevelJitter;
            int offset = jitter == 0 ? 0 : (int)(levelHash % (uint)(jitter * 2 + 1)) - jitter;
            return aquifer->BaseLevel + offset;
        }

        private static float Blend(float a, float b, float t) => a + t * (b - a);

        private static int FloorDiv(int value, int divisor)
            => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
    }

    /// <summary>Holds the Burst compilations of the density and aquifer kernels.</summary>
    public static unsafe class DensityDispatch
    {
        private static readonly BurstEntryPoint<DensityBias> BiasEntry = new BurstEntryPoint<DensityBias>(DensityKernel.Bias);
        private static readonly BurstEntryPoint<DensityApply> ApplyEntry = new BurstEntryPoint<DensityApply>(DensityKernel.Apply);
        private static readonly BurstEntryPoint<AquiferApply> AquiferEntry = new BurstEntryPoint<AquiferApply>(DensityKernel.Aquifers);

        public static FunctionPointer<DensityBias> CompiledBias => BiasEntry.Compiled;
        public static FunctionPointer<DensityApply> CompiledApply => ApplyEntry.Compiled;
        public static FunctionPointer<AquiferApply> CompiledAquifers => AquiferEntry.Compiled;

        public static void Warm() { BiasEntry.Warm(); ApplyEntry.Warm(); AquiferEntry.Warm(); }
    }
}
