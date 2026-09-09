using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Definitions
{
    //compiles authored materials, archetypes and blocks into a registry plus aligned appearance rows.
    //Ordering here is deterministic on purpose: the registry fingerprint is a network compatibility check.
    public static class BlockContentCompiler
    {
        public const string ReservedSolid = "digblocks:air";
        public const string ReservedFluid = "digblocks:empty";
        private const string DefaultBehavior = "digblocks:static";
        private const string DefaultModel = "digblocks:cube";

        public static CompiledBlockContent Compile(IEnumerable<RenderMaterialDefinition> materials,
            IEnumerable<BlockArchetype> archetypes, IEnumerable<BlockDefinition> blocks)
        {
            var materialList = OrderMaterials(materials);
            var archetypeIndex = IndexArchetypes(archetypes);
            var blockList = OrderBlocks(blocks);

            var merged = new List<MergedBlock>(blockList.Count);
            foreach (var block in blockList) merged.Add(new MergedBlock(block, Merge(block, archetypeIndex)));

            var tints = CollectTintKeys(merged);
            var solids = new List<StateDefinition>();
            var fluids = new List<StateDefinition>();
            var appearance = new Dictionary<string, BlockStateAppearance>(StringComparer.Ordinal);
            foreach (var entry in merged) Expand(entry, materialList, tints, solids, fluids, appearance);

            BlockRegistry registry;
            try { registry = new BlockRegistry(solids, fluids); }
            catch (ArgumentException error) { throw new BlockContentException(null, error.Message, error); }

            return new CompiledBlockContent(registry, materialList, tints.Keys,
                AlignAppearance(appearance, registry.MaxSolidStateId, registry.GetSolid),
                AlignAppearance(appearance, registry.MaxFluidStateId, registry.GetFluid));
        }

        //opaque materials sort first so the client can build submeshes in draw order without a second sort.
        private static List<RenderMaterialDefinition> OrderMaterials(IEnumerable<RenderMaterialDefinition> materials)
        {
            if (materials == null) throw new ArgumentNullException(nameof(materials));
            var sorted = new List<RenderMaterialDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in materials)
            {
                if (material == null) throw new BlockContentException(null, "Null render material.");
                if (!seen.Add(material.Key)) throw new BlockContentException(material.Key, "Duplicate render material.");
                sorted.Add(material);
            }
            if (sorted.Count == 0) throw new BlockContentException(null, "Content defines no render materials.");
            sorted.Sort((left, right) => left.RenderLayer != right.RenderLayer
                ? left.RenderLayer.CompareTo(right.RenderLayer)
                : string.CompareOrdinal(left.Key, right.Key));
            return sorted;
        }

        private static Dictionary<string, BlockArchetype> IndexArchetypes(IEnumerable<BlockArchetype> archetypes)
        {
            var index = new Dictionary<string, BlockArchetype>(StringComparer.Ordinal);
            if (archetypes == null) return index;
            foreach (var archetype in archetypes)
            {
                if (archetype == null) throw new BlockContentException(null, "Null archetype.");
                Validate(archetype, nameof(archetypes));
                if (index.ContainsKey(archetype.Key)) throw new BlockContentException(archetype.Key, "Duplicate archetype.");
                index.Add(archetype.Key, archetype);
            }
            return index;
        }

        private static List<BlockDefinition> OrderBlocks(IEnumerable<BlockDefinition> blocks)
        {
            if (blocks == null) throw new ArgumentNullException(nameof(blocks));
            var sorted = new SortedDictionary<string, BlockDefinition>(StringComparer.Ordinal);
            foreach (var block in blocks)
            {
                if (block == null) throw new BlockContentException(null, "Null block definition.");
                Validate(block, nameof(blocks));
                if (sorted.ContainsKey(block.Key)) throw new BlockContentException(block.Key, "Duplicate block definition.");
                sorted.Add(block.Key, block);
            }
            return new List<BlockDefinition>(sorted.Values);
        }

        //surfaces authoring key errors as content failures so the message always names the offending entry.
        private static void Validate(BlockLayer layer, string origin)
        {
            try { layer.ValidateKeys(origin); }
            catch (ArgumentException error) { throw new BlockContentException(layer.Key, error.Message, error); }
        }

        //walks the archetype chain root-first so the nearest layer wins every field it sets.
        private static MergedLayer Merge(BlockDefinition block, IReadOnlyDictionary<string, BlockArchetype> archetypes)
        {
            var chain = new List<BlockLayer>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string next = block.ArchetypeKey;
            while (next != null)
            {
                if (!archetypes.TryGetValue(next, out var archetype))
                    throw new BlockContentException(block.Key, "Unknown archetype " + next + ".");
                if (!visited.Add(next)) throw new BlockContentException(block.Key, "Archetype cycle at " + next + ".");
                chain.Add(archetype);
                next = archetype.ArchetypeKey;
            }
            chain.Reverse();
            chain.Add(block);

            var merged = new MergedLayer();
            foreach (var layer in chain) merged.Apply(layer);
            return merged;
        }

        private static TintTable CollectTintKeys(IEnumerable<MergedBlock> blocks)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in blocks)
            {
                Collect(entry.Layer.Appearance, keys);
                foreach (var stateOverride in entry.Layer.StateOverrides) Collect(stateOverride.Appearance, keys);
            }
            return new TintTable(keys);
        }

        private static void Collect(BlockAppearanceOverrides appearance, ISet<string> keys)
        {
            if (appearance == null) return;
            if (appearance.TintKey != null) keys.Add(appearance.TintKey);
            foreach (var face in appearance.Faces) if (face != null && face.TintKey != null) keys.Add(face.TintKey);
        }

        private static void Expand(MergedBlock entry, IReadOnlyList<RenderMaterialDefinition> materials,
            TintTable tints, List<StateDefinition> solids, List<StateDefinition> fluids,
            IDictionary<string, BlockStateAppearance> appearance)
        {
            var block = entry.Definition;
            var layer = entry.Layer;
            var properties = new List<BlockProperty>(layer.Properties.Values);
            properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

            var target = block.Channel == BlockChannel.Fluid ? fluids : solids;
            foreach (var values in Combinations(block.Key, properties))
            {
                string stateKey = StateKey(block.Key, properties, values);
                var attributes = layer.Attributes.Clone();
                var faces = layer.Appearance.Clone();
                foreach (var stateOverride in layer.StateOverrides)
                {
                    if (!stateOverride.Matches(values)) continue;
                    attributes.Overlay(stateOverride.Attributes);
                    faces.Overlay(stateOverride.Appearance);
                }

                StateDefinition state;
                try
                {
                    state = new StateDefinition(stateKey, layer.BehaviorKey, layer.ModelKey, layer.Tags,
                        attributes.Resolve(layer.BlockEntityKey != null), layer.BlockEntityKey, layer.DropsKey, layer.SoundSetKey);
                }
                catch (ArgumentException error) { throw new BlockContentException(stateKey, error.Message, error); }

                target.Add(state);
                appearance.Add(state.Key, block.Invisible
                    ? BlockStateAppearance.Invisible
                    : ResolveAppearance(state.Key, faces, materials, tints));
            }
        }

        //the last property varies fastest, which keeps expansion order stable as properties are added.
        private static List<Dictionary<string, string>> Combinations(string blockKey, List<BlockProperty> properties)
        {
            var combinations = new List<Dictionary<string, string>> { new Dictionary<string, string>(StringComparer.Ordinal) };
            foreach (var property in properties)
            {
                var expanded = new List<Dictionary<string, string>>(combinations.Count * property.Values.Count);
                foreach (var partial in combinations)
                    foreach (string value in property.Values)
                        expanded.Add(new Dictionary<string, string>(partial, StringComparer.Ordinal) { { property.Name, value } });
                if (expanded.Count > BlockDefinition.MaxStates)
                    throw new BlockContentException(blockKey, "Block expands to more than " + BlockDefinition.MaxStates + " states.");
                combinations = expanded;
            }
            return combinations;
        }

        private static string StateKey(string blockKey, List<BlockProperty> properties, IReadOnlyDictionary<string, string> values)
        {
            if (properties.Count == 0) return blockKey;
            var entries = new List<string>(properties.Count);
            foreach (var property in properties) entries.Add(property.Name + "=" + values[property.Name]);
            return blockKey + "[" + string.Join(",", entries) + "]";
        }

        private static BlockStateAppearance ResolveAppearance(string stateKey, BlockAppearanceOverrides overrides,
            IReadOnlyList<RenderMaterialDefinition> materials, TintTable tints)
        {
            if (overrides.MaterialKey == null)
                throw new BlockContentException(stateKey, "A visible block must name a render material.");
            int materialIndex = -1;
            for (int i = 0; i < materials.Count && materialIndex < 0; i++)
                if (materials[i].Key == overrides.MaterialKey) materialIndex = i;
            if (materialIndex < 0)
                throw new BlockContentException(stateKey, "Unknown render material " + overrides.MaterialKey + ".");

            int sliceCount = materials[materialIndex].SliceCount;
            var faces = new BlockFaceAppearance[BlockAppearanceOverrides.FaceCount];
            for (int face = 0; face < faces.Length; face++)
            {
                var self = overrides.Faces[face];
                int? texture = (self != null ? self.Texture : null) ?? overrides.Texture;
                if (!texture.HasValue)
                    throw new BlockContentException(stateKey, "Face " + (BlockFace)face + " has no texture slice.");
                if (texture.Value < 0 || texture.Value >= sliceCount)
                    throw new BlockContentException(stateKey, "Texture slice " + texture.Value + " is outside "
                        + overrides.MaterialKey + " which holds " + sliceCount + " slices.");
                byte rotation = (self != null ? self.Rotation : null) ?? overrides.Rotation ?? 0;
                if (rotation > 3) throw new BlockContentException(stateKey, "Face rotation must be 0-3 quarter turns.");
                string tint = (self != null ? self.TintKey : null) ?? overrides.TintKey;
                bool randomized = self?.RandomizeRotation ?? overrides.RandomizeRotation ?? false;
                faces[face] = new BlockFaceAppearance((ushort)texture.Value, rotation, tints.IndexOf(tint), randomized);
            }
            return new BlockStateAppearance(materialIndex, overrides.RandomizeRotation ?? false, faces);
        }

        private static IReadOnlyList<BlockStateAppearance> AlignAppearance(
            IReadOnlyDictionary<string, BlockStateAppearance> appearance, uint maxId, Func<uint, StateDefinition> get)
        {
            var aligned = new BlockStateAppearance[maxId + 1];
            for (uint id = 0; id <= maxId; id++) aligned[id] = appearance[get(id).Key];
            return Array.AsReadOnly(aligned);
        }

        private sealed class MergedBlock
        {
            public readonly BlockDefinition Definition;
            public readonly MergedLayer Layer;
            public MergedBlock(BlockDefinition definition, MergedLayer layer) { Definition = definition; Layer = layer; }
        }

        private sealed class MergedLayer
        {
            public string BehaviorKey = DefaultBehavior;
            public string ModelKey = DefaultModel;
            public string BlockEntityKey, DropsKey, SoundSetKey;
            public readonly SortedSet<string> Tags = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedDictionary<string, BlockProperty> Properties =
                new SortedDictionary<string, BlockProperty>(StringComparer.Ordinal);
            public readonly BlockAttributeOverrides Attributes = new BlockAttributeOverrides();
            public readonly BlockAppearanceOverrides Appearance = new BlockAppearanceOverrides();
            public readonly List<BlockStateOverride> StateOverrides = new List<BlockStateOverride>();

            public void Apply(BlockLayer layer)
            {
                BehaviorKey = layer.BehaviorKey ?? BehaviorKey;
                ModelKey = layer.ModelKey ?? ModelKey;
                BlockEntityKey = layer.BlockEntityKey ?? BlockEntityKey;
                DropsKey = layer.DropsKey ?? DropsKey;
                SoundSetKey = layer.SoundSetKey ?? SoundSetKey;
                foreach (string tag in layer.Tags) Tags.Add(tag);
                foreach (var property in layer.Properties) Properties[property.Name] = property;
                Attributes.Overlay(layer.Attributes);
                Appearance.Overlay(layer.Appearance);
                StateOverrides.AddRange(layer.StateOverrides);
            }
        }

        //index zero is reserved for the untinted face, so a compiled tint of zero never needs a lookup.
        private sealed class TintTable
        {
            public readonly IReadOnlyList<string> Keys;
            private readonly Dictionary<string, byte> indices;

            public TintTable(SortedSet<string> keys)
            {
                var ordered = new List<string> { null };
                ordered.AddRange(keys);
                if (ordered.Count > byte.MaxValue + 1) throw new BlockContentException(null, "Too many tint sources.");
                Keys = ordered.AsReadOnly();
                indices = new Dictionary<string, byte>(ordered.Count, StringComparer.Ordinal);
                for (int i = 1; i < ordered.Count; i++) indices.Add(ordered[i], (byte)i);
            }

            public byte IndexOf(string key) => key == null ? (byte)0 : indices[key];
        }
    }
}
