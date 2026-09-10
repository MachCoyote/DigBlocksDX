namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// The world types that ship with the game. Registration is explicit rather than discovered by
    /// reflection, so which world types exist is answerable by reading one file.
    /// </summary>
    public static class BuiltInGenerators
    {
        /// <summary>Registers every shipped world type. Safe to call more than once.</summary>
        public static void RegisterAll()
        {
            TerrainGeneratorRegistry.Register(new ClassicGenerator());
            TerrainGeneratorRegistry.Register(new FlatGenerator());
        }

        /// <summary>The world type used when nothing has chosen one.</summary>
        public static TerrainGeneratorDefinition Default
        {
            get
            {
                RegisterAll();
                return TerrainGeneratorRegistry.Resolve(ClassicGenerator.Key);
            }
        }

        /// <summary>
        /// Resolves a world type by id, falling back to the default when the id names nothing. A world
        /// saved against a world type this build no longer has should still open.
        /// </summary>
        public static TerrainGeneratorDefinition ResolveOrDefault(string id)
        {
            RegisterAll();
            return TerrainGeneratorRegistry.TryResolve(id, out var definition) ? definition : Default;
        }
    }
}
