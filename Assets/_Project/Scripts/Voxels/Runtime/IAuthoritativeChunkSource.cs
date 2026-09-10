namespace DigBlocks.Voxels.Runtime
{
    public interface IAuthoritativeChunkSource
    {
        /// <summary>
        /// Fills one value per cell for the chunk at <paramref name="address"/>.
        /// <para>
        /// Runs on a worker thread. It must not touch Unity APIs, the chunk store, or any native
        /// container. Both buffers hold exactly one chunk, arrive cleared to air, and are reused
        /// between calls, so nothing may retain them past the call.
        /// </para>
        /// </summary>
        void Generate(ChunkAddress address, uint[] solids, uint[] fluids);
    }
}
