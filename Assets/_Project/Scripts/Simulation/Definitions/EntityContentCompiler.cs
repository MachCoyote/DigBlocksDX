using System;
using System.Collections.Generic;

namespace DigBlocks.Simulation.Definitions
{
    /// <summary>
    /// The compiled result of one entity content set: the fingerprinted registry both peers compare,
    /// plus the model definitions the client turns into geometry. Nothing here references UnityEngine.
    /// </summary>
    public sealed class CompiledEntityContent
    {
        public EntityTypeRegistry Registry { get; }
        public IReadOnlyList<EntityModelDefinition> Models { get; }
        private readonly Dictionary<string, EntityModelDefinition> modelsByKey;

        internal CompiledEntityContent(EntityTypeRegistry registry, IReadOnlyList<EntityModelDefinition> models)
        {
            Registry = registry; Models = models;
            modelsByKey = new Dictionary<string, EntityModelDefinition>(models.Count, StringComparer.Ordinal);
            foreach (var model in models) modelsByKey.Add(model.Key, model);
        }

        public EntityModelDefinition ModelOf(string key) =>
            key != null && modelsByKey.TryGetValue(key, out var model) ? model : null;

        /// <summary>The model a runtime type id draws with, or null for a type that draws nothing.</summary>
        public EntityModelDefinition ModelOf(ushort typeId) => ModelOf(Registry[typeId].ModelKey);
    }

    public static class EntityContentCompiler
    {
        public static CompiledEntityContent Compile(IEnumerable<EntityArchetypeDefinition> archetypes,
            IEnumerable<EntityTypeLayer> types, IEnumerable<EntityModelDefinition> models)
        {
            var archetypeIndex = IndexArchetypes(archetypes);
            var modelList = IndexModels(models, out var modelKeys);

            var definitions = new List<EntityTypeDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types ?? throw new ArgumentNullException(nameof(types)))
            {
                if (type?.Key == null) throw new EntityContentException(null, "An entity type needs a key.");
                if (!seen.Add(type.Key)) throw new EntityContentException(type.Key, "Duplicate entity type.");
                var merged = Merge(type, archetypeIndex);
                //an unresolvable model is an authoring error, not something to discover at draw time.
                if (merged.ModelKey != null && !modelKeys.Contains(merged.ModelKey))
                    throw new EntityContentException(type.Key, "Unknown model " + merged.ModelKey + ".");
                try
                {
                    definitions.Add(new EntityTypeDefinition(type.Key, merged.ModelKey,
                        merged.Behaviors ?? (IEnumerable<string>)Array.Empty<string>(), merged.Attributes.Resolve()));
                }
                catch (ArgumentException error) { throw new EntityContentException(type.Key, error.Message, error); }
            }

            try { return new CompiledEntityContent(new EntityTypeRegistry(definitions), modelList); }
            catch (ArgumentException error) { throw new EntityContentException(null, error.Message, error); }
        }

        private static Dictionary<string, EntityArchetypeDefinition> IndexArchetypes(IEnumerable<EntityArchetypeDefinition> archetypes)
        {
            var index = new Dictionary<string, EntityArchetypeDefinition>(StringComparer.Ordinal);
            if (archetypes == null) return index;
            foreach (var archetype in archetypes)
            {
                if (archetype?.Key == null) throw new EntityContentException(null, "An entity archetype needs a key.");
                if (index.ContainsKey(archetype.Key)) throw new EntityContentException(archetype.Key, "Duplicate entity archetype.");
                index.Add(archetype.Key, archetype);
            }
            return index;
        }

        private static List<EntityModelDefinition> IndexModels(IEnumerable<EntityModelDefinition> models, out HashSet<string> keys)
        {
            var sorted = new SortedDictionary<string, EntityModelDefinition>(StringComparer.Ordinal);
            if (models != null)
                foreach (var model in models)
                {
                    if (model == null) throw new EntityContentException(null, "Null entity model.");
                    if (sorted.ContainsKey(model.Key)) throw new EntityContentException(model.Key, "Duplicate entity model.");
                    sorted.Add(model.Key, model);
                }
            keys = new HashSet<string>(sorted.Keys, StringComparer.Ordinal);
            return new List<EntityModelDefinition>(sorted.Values);
        }

        //walks the archetype chain root-first so the nearest layer wins every field it sets.
        private static MergedLayer Merge(EntityTypeLayer type, IReadOnlyDictionary<string, EntityArchetypeDefinition> archetypes)
        {
            var chain = new List<EntityLayer>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string next = type.ArchetypeKey;
            while (next != null)
            {
                if (!archetypes.TryGetValue(next, out var archetype))
                    throw new EntityContentException(type.Key, "Unknown archetype " + next + ".");
                if (!visited.Add(next)) throw new EntityContentException(type.Key, "Archetype cycle at " + next + ".");
                chain.Add(archetype);
                next = archetype.ArchetypeKey;
            }
            chain.Reverse();
            chain.Add(type);

            var merged = new MergedLayer();
            foreach (var layer in chain) merged.Apply(layer);
            return merged;
        }

        private sealed class MergedLayer
        {
            public string ModelKey;
            public List<string> Behaviors;
            public readonly EntityAttributeOverrides Attributes = new EntityAttributeOverrides();

            public void Apply(EntityLayer layer)
            {
                ModelKey = layer.ModelKey ?? ModelKey;
                //behaviours union across the chain rather than replacing, so an archetype's behaviour
                //survives a type adding one of its own. Tags behave the same way in block content.
                if (layer.Behaviors != null)
                {
                    Behaviors = Behaviors ?? new List<string>();
                    foreach (string behavior in layer.Behaviors)
                        if (!Behaviors.Contains(behavior)) Behaviors.Add(behavior);
                }
                Attributes.Overlay(layer.Attributes);
            }
        }
    }
}
