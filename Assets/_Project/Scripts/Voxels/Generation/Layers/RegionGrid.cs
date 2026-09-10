using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>One candidate placement drawn from a region grid.</summary>
    public readonly struct RegionPlacement
    {
        public readonly int RegionX, RegionZ, WorldX, WorldZ;
        public readonly GenSeed Seed;

        internal RegionPlacement(int regionX, int regionZ, int worldX, int worldZ, GenSeed seed)
        {
            RegionX = regionX; RegionZ = regionZ; WorldX = worldX; WorldZ = worldZ; Seed = seed;
        }
    }

    /// <summary>
    /// Deterministic scattering for things that are rare and large: villages, ruins, dungeons.
    /// <para>
    /// The world is divided into regions of a fixed size, and each region either holds one placement
    /// or does not, decided by hashing the region's coordinates against the seed. The position within
    /// the region is jittered so the result does not read as a grid, while separation stays bounded
    /// because two placements can never share a region.
    /// </para>
    /// <para>
    /// The reason to do it this way rather than rolling dice per chunk is that a chunk can work out
    /// what its neighbours placed without generating them. A structure larger than a chunk is drawn by
    /// every chunk it touches, each asking the same question of the same regions and getting the same
    /// answer, in any order, with no communication.
    /// </para>
    /// </summary>
    public sealed class RegionGrid
    {
        public string Name { get; }
        /// <summary>Region size in blocks.</summary>
        public int Spacing { get; }
        /// <summary>How far from a region's low corner a placement may sit, in blocks. At most <see cref="Spacing"/>.</summary>
        public int Jitter { get; }
        /// <summary>The fraction of regions that hold a placement, in (0, 1].</summary>
        public float Density { get; }

        public RegionGrid(string name, int spacing, int jitter = -1, float density = 1f)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A region grid needs a name.", nameof(name));
            if (spacing < 1) throw new ArgumentOutOfRangeException(nameof(spacing));
            if (!(density > 0f && density <= 1f)) throw new ArgumentOutOfRangeException(nameof(density));
            Name = name;
            Spacing = spacing;
            Jitter = jitter < 0 ? spacing : jitter;
            if (Jitter > spacing) throw new ArgumentOutOfRangeException(nameof(jitter), "Jitter beyond the region size would let placements swap regions.");
            Density = density;
        }

        public int RegionOf(int worldCoordinate) => FloorDiv(worldCoordinate, Spacing);

        /// <summary>The placement in one region, if it holds one.</summary>
        public bool TryPlacement(GenSeed worldSeed, int regionX, int regionZ, out RegionPlacement placement)
        {
            GenSeed gridSeed = worldSeed.Derive(Name);
            uint hash = GenHash.Lattice(gridSeed.Lattice, regionX, regionZ);
            placement = default;
            if (Density < 1f && GenHash.UnitFloat(hash) >= Density) return false;

            //independent draws for presence and for position, so thinning the grid does not also move
            //the placements that survive.
            uint offsetHash = GenHash.Mix32(hash ^ 0x85EBCA77u);
            int offsetX = Jitter == 0 ? 0 : (int)(offsetHash % (uint)Jitter);
            int offsetZ = Jitter == 0 ? 0 : (int)(GenHash.Mix32(offsetHash) % (uint)Jitter);

            placement = new RegionPlacement(regionX, regionZ,
                regionX * Spacing + offsetX, regionZ * Spacing + offsetZ,
                gridSeed.Derive((ulong)((long)regionX << 32 ^ (uint)regionZ)));
            return true;
        }

        /// <summary>
        /// Every placement whose region lies within <paramref name="blockRadius"/> of the given world
        /// area. This is what a feature generator calls to draw the parts of its neighbours' work that
        /// reach into the chunk it is filling.
        /// </summary>
        public void Around(GenSeed worldSeed, int worldX, int worldZ, int blockRadius, Action<RegionPlacement> onPlacement)
        {
            if (onPlacement == null) throw new ArgumentNullException(nameof(onPlacement));
            if (blockRadius < 0) throw new ArgumentOutOfRangeException(nameof(blockRadius));

            int lowX = RegionOf(worldX - blockRadius), highX = RegionOf(worldX + blockRadius);
            int lowZ = RegionOf(worldZ - blockRadius), highZ = RegionOf(worldZ + blockRadius);
            for (int regionZ = lowZ; regionZ <= highZ; regionZ++)
            for (int regionX = lowX; regionX <= highX; regionX++)
                if (TryPlacement(worldSeed, regionX, regionZ, out var placement))
                    onPlacement(placement);
        }

        private static int FloorDiv(int value, int divisor)
            => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
    }
}
