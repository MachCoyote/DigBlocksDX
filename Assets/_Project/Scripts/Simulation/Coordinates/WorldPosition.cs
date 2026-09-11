using System;
using DigBlocks.Voxels;
using Unity.Entities;
using Unity.Mathematics;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// The authoritative position of a dynamic entity: an exact integer sector plus a small offset
    /// inside it. This is what simulation reads and what replication carries.
    /// <para>
    /// An absolute <c>float3</c> cannot express this world. Float spacing is about <c>v * 2^-23</c>,
    /// so a position a million blocks out resolves to roughly twelve centimetres and one sixteen
    /// million blocks out resolves to a whole block. Splitting the number keeps the large part exact,
    /// so precision is constant everywhere rather than decaying with distance.
    /// </para>
    /// </summary>
    public struct WorldPosition : IComponentData, IEquatable<WorldPosition>
    {
        /// <summary>Which sector of the world, exactly. Range is bounded only by this integer.</summary>
        public int3 Sector;

        /// <summary>Offset inside the sector. Normalized positions keep every component in [0, SectorEdge).</summary>
        public float3 Local;

        public WorldPosition(int3 sector, float3 local) { Sector = sector; Local = local; }

        public bool Equals(WorldPosition other) => math.all(Sector == other.Sector) && math.all(Local == other.Local);
        public override bool Equals(object obj) => obj is WorldPosition other && Equals(other);
        public override int GetHashCode() => unchecked((int)(math.hash(Sector) * 397 ^ math.hash(Local)));
        public override string ToString() => $"{Sector.x},{Sector.y},{Sector.z}+({Local.x:0.###},{Local.y:0.###},{Local.z:0.###})";
    }

    /// <summary>
    /// The sector grid every world position is expressed against, and the conversions between it,
    /// block space, chunk addresses and a frame-local float.
    /// </summary>
    public static class SectorGrid
    {
        /// <summary>
        /// Blocks across one sector. Worst-case float granularity inside a sector is
        /// <see cref="SectorEdge"/> * 2^-23, about half a millimetre, which holds no matter how far
        /// from the origin the sector sits.
        /// </summary>
        public const int SectorEdge = 4096;

        /// <summary>Chunks across one sector. Sector boundaries fall on chunk boundaries by construction.</summary>
        public const int ChunksPerSector = SectorEdge / ChunkLayout.Edge;

        /// <summary>
        /// The largest float below <see cref="SectorEdge"/>. Floats just under 4096 are spaced 2^-12
        /// apart, so this is exact; normalization clamps to it rather than letting a rounding step
        /// leave an offset sitting exactly on the next sector's origin.
        /// </summary>
        public const float MaxLocal = SectorEdge - 0.000244140625f;

        /// <summary>Carries any whole sectors out of the offset, leaving it in [0, SectorEdge).</summary>
        public static WorldPosition Normalize(WorldPosition position)
        {
            int3 carry = (int3)math.floor(position.Local / SectorEdge);
            position.Sector += carry;
            position.Local = math.clamp(position.Local - (float3)carry * SectorEdge, float3.zero, MaxLocal);
            return position;
        }

        public static WorldPosition FromBlocks(int3 block)
        {
            //integer floor division. Routing this through a float would reintroduce exactly the
            //precision loss the type exists to avoid, at the one point where the input is exact.
            int3 remainder = block % SectorEdge;
            int3 sector = block / SectorEdge - math.select(new int3(0), new int3(1), remainder < 0);
            return new WorldPosition(sector, (float3)(block - sector * SectorEdge));
        }

        public static WorldPosition FromBlocks(double3 block)
        {
            int3 sector = (int3)math.floor(block / SectorEdge);
            return new WorldPosition(sector, (float3)(block - (double3)sector * SectorEdge));
        }

        /// <summary>Moves a position by a local delta, carrying across sector boundaries.</summary>
        public static WorldPosition Offset(WorldPosition position, float3 delta)
        {
            position.Local += delta;
            return Normalize(position);
        }

        /// <summary>
        /// Absolute block coordinates, for diagnostics and authoring. Returns a double because the
        /// whole point of the split representation is that no float can hold this.
        /// </summary>
        public static double3 ToBlocks(WorldPosition position)
        {
            position = Normalize(position);
            return (double3)position.Sector * SectorEdge + (double3)position.Local;
        }

        /// <summary>
        /// Offset from a frame origin, which is what physics and rendering consume. Only meaningful
        /// while the position is near the frame; that is the frame's entire job.
        /// </summary>
        public static float3 ToFrame(WorldPosition position, int3 frameOrigin)
        {
            position = Normalize(position);
            return (float3)(position.Sector - frameOrigin) * SectorEdge + position.Local;
        }

        /// <summary>
        /// Separation between two positions. Valid for positions near each other, which is the only
        /// case where a float answer means anything.
        /// </summary>
        public static float3 Delta(WorldPosition from, WorldPosition to)
        {
            from = Normalize(from); to = Normalize(to);
            return (float3)(to.Sector - from.Sector) * SectorEdge + (to.Local - from.Local);
        }

        public static ChunkAddress ChunkOf(uint world, WorldPosition position)
        {
            position = Normalize(position);
            int3 chunk = position.Sector * ChunksPerSector + (int3)math.floor(position.Local / ChunkLayout.Edge);
            return new ChunkAddress(world, chunk);
        }
    }
}
