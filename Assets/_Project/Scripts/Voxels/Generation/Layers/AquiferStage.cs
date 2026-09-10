using System;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// Local water tables, so carved space underground is not uniformly dry nor uniformly flooded to
    /// the sea.
    /// <para>
    /// Without this, a layer with caves has exactly two possible looks: every cavern below sea level
    /// full of water, or none of them. Aquifers give each region of the world its own water level,
    /// drawn deterministically from the seed, so some caves hold lakes, some are dry, and the two sit
    /// near each other without either being a special case.
    /// </para>
    /// <para>
    /// Levels come from a jittered lattice rather than a smooth field on purpose: a water surface has
    /// to be flat to look like water, so a region holds one level throughout and neighbouring regions
    /// simply differ.
    /// </para>
    /// </summary>
    public sealed class AquiferStage
    {
        public string Name { get; }
        public string Fluid { get; }
        /// <summary>How large a region sharing one water level is, in blocks.</summary>
        public int3 CellSize { get; }
        /// <summary>The band of world this applies to. Outside it, carved space stays air.</summary>
        public int MinY { get; }
        public int MaxY { get; }
        /// <summary>The height regional levels vary around. Usually the layer's sea level.</summary>
        public int BaseLevel { get; }
        /// <summary>How far above and below <see cref="BaseLevel"/> a region's level may fall.</summary>
        public int LevelJitter { get; }
        /// <summary>The fraction of regions that hold no water at all, in [0, 1). Dry caves.</summary>
        public float DryChance { get; }

        public AquiferStage(string name, string fluid, int minY, int maxY, int baseLevel,
            int levelJitter = 24, float dryChance = 0.25f, int3 cellSize = default)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An aquifer stage needs a name.", nameof(name));
            if (string.IsNullOrWhiteSpace(fluid)) throw new ArgumentException("An aquifer stage needs a fluid.", nameof(fluid));
            if (minY > maxY) throw new GenerationContentException($"Aquifer stage '{name}' has a bottom above its top.");
            if (levelJitter < 0) throw new ArgumentOutOfRangeException(nameof(levelJitter));
            if (!(dryChance >= 0f && dryChance < 1f)) throw new ArgumentOutOfRangeException(nameof(dryChance));

            Name = name;
            Fluid = fluid;
            MinY = minY;
            MaxY = maxY;
            BaseLevel = baseLevel;
            LevelJitter = levelJitter;
            DryChance = dryChance;
            CellSize = math.all(cellSize == 0) ? new int3(16, 12, 16) : cellSize;
            if (math.any(CellSize < 1)) throw new ArgumentOutOfRangeException(nameof(cellSize));
        }

        /// <summary>
        /// The water level of the region containing a world position, or <see cref="int.MinValue"/>
        /// where the region is dry. Deterministic from the seed and the region alone, so two chunks
        /// sharing a region agree without consulting each other.
        /// </summary>
        public int LevelAt(GenSeed seed, int worldX, int worldY, int worldZ)
        {
            int cellX = FloorDiv(worldX, CellSize.x);
            int cellY = FloorDiv(worldY, CellSize.y);
            int cellZ = FloorDiv(worldZ, CellSize.z);
            uint hash = GenHash.Lattice(seed.Lattice, cellX, cellY, cellZ);

            if (GenHash.UnitFloat(hash) < DryChance) return int.MinValue;
            //a second, independent draw so a region's dryness and its level do not move together.
            uint levelHash = GenHash.Mix32(hash ^ 0x9E3779B1u);
            int offset = LevelJitter == 0 ? 0 : (int)(levelHash % (uint)(LevelJitter * 2 + 1)) - LevelJitter;
            return BaseLevel + offset;
        }

        private static int FloorDiv(int value, int divisor)
            => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
    }
}
