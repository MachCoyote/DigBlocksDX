using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;

namespace DigBlocks.Voxels
{
    public sealed class StateDefinition
    {
        public string Key { get; }
        public string BehaviorKey { get; }
        public string ModelKey { get; }
        public IReadOnlyList<string> Tags { get; }
        public BlockAttributes Attributes { get; }
        //optional identity slots; null means the state has no block entity, drop table or sound set.
        public string BlockEntityKey { get; }
        public string DropsKey { get; }
        public string SoundSetKey { get; }
        public bool PermitsFluid => Attributes.PermitsFluid;
        public bool HasBlockEntity => BlockEntityKey != null;

        //convenience overload for tests and fixtures that only care about identity and fluid compatibility.
        public StateDefinition(string key, string behaviorKey, string modelKey, IEnumerable<string> tags, bool permitsFluid)
            : this(key, behaviorKey, modelKey, tags, permitsFluid
                ? BlockAttributes.Default.WithFlags(BlockAttributes.Default.Flags | BlockFlags.PermitsFluid)
                : BlockAttributes.Default.WithFlags(BlockAttributes.Default.Flags & ~BlockFlags.PermitsFluid))
        {
        }

        public StateDefinition(string key, string behaviorKey, string modelKey, IEnumerable<string> tags,
            BlockAttributes attributes, string blockEntityKey = null, string dropsKey = null, string soundSetKey = null)
        {
            Key = CanonicalKey(key);
            BehaviorKey = ValidateResourceKey(behaviorKey);
            ModelKey = ValidateResourceKey(modelKey);
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            var sortedTags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string tag in tags)
                if (!sortedTags.Add(ValidateResourceKey(tag))) throw new ArgumentException("Duplicate tag.", nameof(tags));
            Tags = new List<string>(sortedTags).AsReadOnly();
            BlockEntityKey = blockEntityKey == null ? null : ValidateResourceKey(blockEntityKey);
            DropsKey = dropsKey == null ? null : ValidateResourceKey(dropsKey);
            SoundSetKey = soundSetKey == null ? null : ValidateResourceKey(soundSetKey);
            bool declaresEntity = attributes.Has(BlockFlags.BlockEntity);
            if (declaresEntity != HasBlockEntity)
                throw new ArgumentException("The block-entity flag and block-entity key must agree.", nameof(attributes));
            Attributes = attributes;
        }

        private static string ValidateResourceKey(string key) => ResourceKeys.Validate(key, nameof(key));

        internal static string CanonicalKey(string key)
        {
            if (key == null) throw new ArgumentException("State key is required.", nameof(key));
            int bracket = key.IndexOf('[');
            if (bracket < 0) return ValidateResourceKey(key);
            string resource = ValidateResourceKey(key.Substring(0, bracket));
            if (!key.EndsWith("]", StringComparison.Ordinal)) throw new ArgumentException("Invalid state properties.", nameof(key));
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string property in key.Substring(bracket + 1, key.Length - bracket - 2).Split(','))
            {
                string[] pair = property.Split('=');
                if (pair.Length != 2 || !ResourceKeys.IsPropertyToken(pair[0]) || !ResourceKeys.IsPropertyToken(pair[1]) || properties.ContainsKey(pair[0]))
                    throw new ArgumentException("Invalid or duplicate state property.", nameof(key));
                properties.Add(pair[0], pair[1]);
            }
            var entries = new List<string>(properties.Count);
            foreach (var pair in properties) entries.Add(pair.Key + "=" + pair.Value);
            return resource + "[" + string.Join(",", entries) + "]";
        }
    }

    public sealed class BlockRegistry
    {
        private readonly StateDefinition[] solids, fluids;
        private readonly Dictionary<string, uint> solidIds, fluidIds;
        public uint MaxSolidStateId => (uint)(solids.Length - 1);
        public uint MaxFluidStateId => (uint)(fluids.Length - 1);
        public string Fingerprint { get; }

        public BlockRegistry(IEnumerable<StateDefinition> solids, IEnumerable<StateDefinition> fluids)
        {
            this.solids = BuildChannel(solids, "digblocks:air", out solidIds);
            this.fluids = BuildChannel(fluids, "digblocks:empty", out fluidIds);
            //length-prefixed fields and channel counts make the canonical hash input unambiguous.
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("DigBlocks.Registry.v2");
                WriteChannel(writer, this.solids);
                WriteChannel(writer, this.fluids);
            }
            using var hash = SHA256.Create();
            Fingerprint = BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        private static StateDefinition[] BuildChannel(IEnumerable<StateDefinition> definitions, string reserved,
            out Dictionary<string, uint> ids)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            var sorted = new SortedDictionary<string, StateDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null || sorted.ContainsKey(definition.Key))
                    throw new ArgumentException("Null or duplicate state definition.", nameof(definitions));
                sorted.Add(definition.Key, definition);
            }
            if (!sorted.TryGetValue(reserved, out var empty)) throw new ArgumentException("Missing reserved state: " + reserved, nameof(definitions));
            var states = new StateDefinition[sorted.Count];
            states[0] = empty;
            sorted.Remove(reserved);
            int index = 1;
            foreach (var definition in sorted.Values) states[index++] = definition;
            ids = new Dictionary<string, uint>(states.Length, StringComparer.Ordinal);
            for (int i = 0; i < states.Length; i++) ids.Add(states[i].Key, (uint)i);
            return states;
        }

        //appearance is deliberately absent: a client texture or material difference must never fail binding.
        private static void WriteChannel(BinaryWriter writer, StateDefinition[] states)
        {
            writer.Write(states.Length);
            foreach (var state in states)
            {
                writer.Write(state.Key);
                writer.Write(state.BehaviorKey);
                writer.Write(state.ModelKey);
                WriteOptional(writer, state.BlockEntityKey);
                WriteOptional(writer, state.DropsKey);
                WriteOptional(writer, state.SoundSetKey);
                var attributes = state.Attributes;
                writer.Write((uint)attributes.Flags);
                writer.Write(attributes.Hardness);
                writer.Write(attributes.BlastResistance);
                writer.Write(attributes.Friction);
                writer.Write((byte)attributes.ToolClass);
                writer.Write(attributes.ToolTier);
                writer.Write(attributes.LightEmission);
                writer.Write(attributes.LightAttenuation);
                writer.Write(attributes.FlammabilityCatch);
                writer.Write(attributes.FlammabilitySpread);
                writer.Write(state.Tags.Count);
                foreach (string tag in state.Tags) writer.Write(tag);
            }
        }

        //the presence flag keeps an absent key distinct from an empty one in the hash input.
        private static void WriteOptional(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null) writer.Write(value);
        }

        //caller-owned; a world should build one table and share its read-only view with jobs.
        public BlockAttributeTable CreateAttributeTable(Allocator allocator)
        {
            return new BlockAttributeTable(BuildAttributes(solids, allocator), BuildAttributes(fluids, allocator));
        }

        private static NativeArray<BlockAttributes> BuildAttributes(StateDefinition[] states, Allocator allocator)
        {
            var attributes = new NativeArray<BlockAttributes>(states.Length, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < states.Length; i++) attributes[i] = states[i].Attributes;
            return attributes;
        }

        public uint LookupSolid(string key) => solidIds[StateDefinition.CanonicalKey(key)];
        public uint LookupFluid(string key) => fluidIds[StateDefinition.CanonicalKey(key)];
        public StateDefinition GetSolid(uint id) => Get(solids, id);
        public StateDefinition GetFluid(uint id) => Get(fluids, id);
        private static StateDefinition Get(StateDefinition[] states, uint id)
        {
            if (id >= states.Length) throw new ArgumentOutOfRangeException(nameof(id));
            return states[id];
        }

        //fixture registry for tests and bootstrap paths that have no content loaded yet.
        public static BlockRegistry CreateDummy()
        {
            StateDefinition Define(string key, BlockAttributes attributes) =>
                new StateDefinition(key, "digblocks:static", "digblocks:cube", Array.Empty<string>(), attributes);
            var water = BlockAttributes.Air.WithFlags(BlockFlags.Replaceable);
            return new BlockRegistry(
                new[] { Define("digblocks:air", BlockAttributes.Air), Define("digblocks:stone", BlockAttributes.Default) },
                new[] { Define("digblocks:empty", BlockAttributes.Air), Define("digblocks:water", water) });
        }
    }
}
