using System.Collections.Generic;
using DigBlocks.Voxels.Definitions;

namespace DigBlocks.Voxels.Content
{
    //reads a content source and compiles it. Both worlds load the same content, so a failure here is fatal
    //rather than recoverable: a partial registry would produce a fingerprint no peer can match.
    public static class BlockContentLoader
    {
        public const string DefaultContentFolder = "content/digblocks";

        public static CompiledBlockContent Load(IBlockContentSource source)
        {
            if (source == null) throw new System.ArgumentNullException(nameof(source));
            var materials = new List<RenderMaterialDefinition>();
            foreach (var document in source.Read(BlockContentCategory.Materials))
                materials.Add(BlockContentJson.ParseMaterial(document));

            var archetypes = new List<BlockArchetype>();
            foreach (var document in source.Read(BlockContentCategory.Archetypes))
                archetypes.Add(BlockContentJson.ParseArchetype(document));

            var blocks = new List<BlockDefinition>();
            foreach (var document in source.Read(BlockContentCategory.Blocks))
                blocks.Add(BlockContentJson.ParseBlock(document));

            return BlockContentCompiler.Compile(materials, archetypes, blocks);
        }

        public static CompiledBlockContent LoadFromDirectory(string root) =>
            Load(new DirectoryBlockContentSource(root));
    }
}
