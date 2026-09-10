using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>What a world type needs in order to be built into a concrete world.</summary>
    public sealed class GeneratorBuildContext
    {
        public GenSeed Seed { get; }
        public GeneratorSettings Settings { get; }
        public IBlockResolver Blocks { get; }

        public GeneratorBuildContext(GenSeed seed, GeneratorSettings settings, IBlockResolver blocks)
        {
            Seed = seed;
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        }
    }

    /// <summary>
    /// A world type, written in code: what it is called, what it lets a player change, and what layers
    /// it produces for a given seed and settings.
    /// <para>
    /// A definition is a description, not a world. It holds no seed and no state, so one definition
    /// serves every world ever created from it.
    /// </para>
    /// </summary>
    public abstract class TerrainGeneratorDefinition
    {
        /// <summary>A stable namespaced id, for example <c>digblocks:classic</c>.</summary>
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public virtual string Description => null;
        /// <summary>The knobs this world type exposes. Empty when it has none.</summary>
        public virtual GeneratorParameterSchema Schema => GeneratorParameterSchema.Empty;

        /// <summary>Describes the layers for one seed and one set of settings.</summary>
        protected abstract IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context);

        public TerrainGenerator Build(GeneratorBuildContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!ReferenceEquals(context.Settings.Schema, Schema) && context.Settings.Schema.Count != Schema.Count)
                throw new GenerationContentException($"World type '{Id}' was given settings belonging to a different world type.");
            return new TerrainGenerator(Id, DisplayName, context, CreateLayers(context));
        }

        /// <summary>Builds with this type's default settings.</summary>
        public TerrainGenerator Build(GenSeed seed, IBlockResolver blocks)
            => Build(new GeneratorBuildContext(seed, GeneratorSettings.Defaults(Schema), blocks));
    }

    /// <summary>
    /// The world types the game knows about. Populated in code at startup; a player picking a world
    /// type is picking from here.
    /// </summary>
    public static class TerrainGeneratorRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, TerrainGeneratorDefinition> Definitions =
            new Dictionary<string, TerrainGeneratorDefinition>(StringComparer.Ordinal);
        private static readonly List<TerrainGeneratorDefinition> Ordered = new List<TerrainGeneratorDefinition>();

        public static void Register(TerrainGeneratorDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.Id)) throw new GenerationContentException("A world type needs an id.");
            lock (Gate)
            {
                if (Definitions.TryGetValue(definition.Id, out var existing))
                {
                    //re-registering the same type is how a domain reload behaves, and is not a problem.
                    if (existing.GetType() == definition.GetType()) return;
                    throw new GenerationContentException($"Two different world types claim the id '{definition.Id}'.");
                }
                Definitions.Add(definition.Id, definition);
                Ordered.Add(definition);
            }
        }

        public static bool TryResolve(string id, out TerrainGeneratorDefinition definition)
        {
            lock (Gate) return Definitions.TryGetValue(id ?? string.Empty, out definition);
        }

        public static TerrainGeneratorDefinition Resolve(string id)
            => TryResolve(id, out var definition) ? definition
                : throw new GenerationContentException($"No world type is registered under '{id}'.");

        /// <summary>Every registered world type, in registration order.</summary>
        public static IReadOnlyList<TerrainGeneratorDefinition> All
        {
            get { lock (Gate) return Ordered.ToArray(); }
        }

        internal static void Clear()
        {
            lock (Gate) { Definitions.Clear(); Ordered.Clear(); }
        }
    }
}
