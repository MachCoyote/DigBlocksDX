using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DigBlocks.Voxels
{
    public sealed class StateDefinition
    {
        private static readonly Regex ResourceKey = new Regex(@"\A[a-z0-9_.-]+:[a-z0-9_./-]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex PropertyToken = new Regex(@"\A[a-z0-9_.-]+\z", RegexOptions.CultureInvariant);
        public string Key { get; }
        public string BehaviorKey { get; }
        public string ModelKey { get; }
        public IReadOnlyList<string> Tags { get; }
        public bool PermitsFluid { get; }

        public StateDefinition(string key, string behaviorKey, string modelKey, IEnumerable<string> tags, bool permitsFluid)
        {
            Key = CanonicalKey(key);
            BehaviorKey = ValidateResourceKey(behaviorKey);
            ModelKey = ValidateResourceKey(modelKey);
            if (tags == null) throw new ArgumentNullException(nameof(tags));
            var sortedTags = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string tag in tags)
                if (!sortedTags.Add(ValidateResourceKey(tag))) throw new ArgumentException("Duplicate tag.", nameof(tags));
            Tags = new List<string>(sortedTags).AsReadOnly();
            PermitsFluid = permitsFluid;
        }

        private static string ValidateResourceKey(string key)
        {
            if (key == null || !ResourceKey.IsMatch(key)) throw new ArgumentException("Expected a lowercase namespaced key.", nameof(key));
            return key;
        }

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
                if (pair.Length != 2 || !PropertyToken.IsMatch(pair[0]) || !PropertyToken.IsMatch(pair[1]) || properties.ContainsKey(pair[0]))
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
                writer.Write("DigBlocks.Registry.v1");
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

        private static void WriteChannel(BinaryWriter writer, StateDefinition[] states)
        {
            writer.Write(states.Length);
            foreach (var state in states)
            {
                writer.Write(state.Key);
                writer.Write(state.BehaviorKey);
                writer.Write(state.ModelKey);
                writer.Write(state.PermitsFluid);
                writer.Write(state.Tags.Count);
                foreach (string tag in state.Tags) writer.Write(tag);
            }
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

        public static BlockRegistry CreateDummy()
        {
            StateDefinition Define(string key, bool permitsFluid = false) =>
                new StateDefinition(key, "digblocks:static", "digblocks:cube", Array.Empty<string>(), permitsFluid);
            return new BlockRegistry(new[] { Define("digblocks:air", true), Define("digblocks:stone") },
                new[] { Define("digblocks:empty"), Define("digblocks:water") });
        }
    }
}
