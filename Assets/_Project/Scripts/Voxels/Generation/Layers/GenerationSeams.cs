using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// What a feature generator is handed: one chunk's cells, where they sit in the world, and the
    /// means to ask deterministic questions about the neighbourhood.
    /// <para>
    /// Nothing implements this yet. It is declared now because its shape constrains the rest: a
    /// feature that straddles a chunk boundary must be placed identically by both chunks, whichever is
    /// generated first, so a feature never writes into its neighbour. It instead enumerates the
    /// feature origins in a radius around itself and draws whatever parts of them land inside its own
    /// bounds. <see cref="RegionGrid"/> is the deterministic enumeration that makes that possible.
    /// </para>
    /// </summary>
    public interface IChunkContext
    {
        int ChunkX { get; }
        int ChunkY { get; }
        int ChunkZ { get; }
        /// <summary>World Y of this chunk's local y = 0.</summary>
        int MinY { get; }
        GenSeed Seed { get; }

        uint GetSolid(int localX, int localY, int localZ);
        void SetSolid(int localX, int localY, int localZ, uint state);
        uint GetFluid(int localX, int localY, int localZ);
        void SetFluid(int localX, int localY, int localZ, uint state);

        /// <summary>
        /// The world Y of the layer's surface above a world column, so a feature can sit on the
        /// ground without searching for it. Available because the column plan already knows.
        /// </summary>
        bool TryGetSurfaceY(int worldX, int worldZ, out int surfaceY);
    }

    /// <summary>
    /// Trees, ores, boulders, ruins: anything placed onto terrain rather than shaped as terrain.
    /// Owned per world layer, so layers differ in what grows in them.
    /// <para>Declared, not implemented. See <see cref="IChunkContext"/> for why the shape matters now.</para>
    /// </summary>
    public interface IFeatureGenerator
    {
        /// <summary>A stable name, used to derive this generator's own seed stream.</summary>
        string Name { get; }

        /// <summary>
        /// How many chunks away a placement originating elsewhere can still reach into this one. The
        /// generator is asked about every origin chunk within this radius.
        /// </summary>
        int ChunkRadius { get; }

        void Place(IChunkContext chunk);
    }

    /// <summary>
    /// Which biome a world position belongs to, and what that implies for its surface material.
    /// <para>
    /// Declared, not implemented. The intended input is the same control fields
    /// <see cref="TerrainShape"/> already builds, so biomes and terrain shape agree by construction
    /// rather than by two separately tuned noise stacks happening to line up.
    /// </para>
    /// </summary>
    public interface IBiomeSource
    {
        string Name { get; }
        int Resolve(int worldX, int worldY, int worldZ);
    }

    /// <summary>Thrown when authored generation content does not describe a usable world.</summary>
    public sealed class GenerationContentException : Exception
    {
        public GenerationContentException(string message) : base(message) { }
        public GenerationContentException(string message, Exception inner) : base(message, inner) { }
    }
}
