namespace DigBlocks.Voxels.Runtime
{
    //synchronous boundary used by the current bounded streaming scheduler. A future disk-backed
    //source can replace the fixture here before residency is made asynchronous.
    public interface IAuthoritativeChunkSource
    {
        CellEdit[] LoadOrGenerate(ChunkAddress address);
    }
}
