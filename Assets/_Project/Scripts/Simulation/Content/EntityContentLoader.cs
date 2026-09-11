using System;
using System.Collections.Generic;
using DigBlocks.Simulation.Definitions;

namespace DigBlocks.Simulation.Content
{
    //reads an entity content source and compiles it. Both worlds load the same content, so a failure
    //here is fatal rather than recoverable: a partial registry produces a fingerprint no peer matches.
    public static class EntityContentLoader
    {
        //the same root as block content, with its own category folders beside the block ones.
        public const string DefaultContentFolder = "content/digblocks";

        public static CompiledEntityContent Load(IEntityContentSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var archetypes = new List<EntityArchetypeDefinition>();
            foreach (var document in source.Read(EntityContentCategory.Archetypes))
                archetypes.Add(EntityContentJson.ParseArchetype(document));

            var models = new List<EntityModelDefinition>();
            foreach (var document in source.Read(EntityContentCategory.Models))
                models.Add(EntityContentJson.ParseModel(document));

            var types = new List<EntityTypeLayer>();
            foreach (var document in source.Read(EntityContentCategory.Types))
                types.Add(EntityContentJson.ParseType(document));

            return EntityContentCompiler.Compile(archetypes, types, models);
        }

        public static CompiledEntityContent LoadFromDirectory(string root) =>
            Load(new DirectoryEntityContentSource(root));
    }
}
