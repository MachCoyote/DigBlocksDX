using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DigBlocks.Voxels;
using Unity.Collections;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// One authored entity type. A managed load-time record: nothing on a hot path reads it, exactly
    /// as block definitions are never read by meshing or simulation jobs.
    /// </summary>
    public sealed class EntityTypeDefinition
    {
        public string Key { get; }
        /// <summary>Appearance only. Absent from the fingerprint, because a different model is not a divergence.</summary>
        public string ModelKey { get; }
        /// <summary>Behaviours this type runs, sorted and deduplicated. These decide which systems act on it.</summary>
        public IReadOnlyList<string> Behaviors { get; }
        public EntityTypeAttributes Attributes { get; }

        public EntityTypeDefinition(string key, string modelKey, IEnumerable<string> behaviors, EntityTypeAttributes attributes)
        {
            Key = ResourceKeys.Validate(key, nameof(key));
            ModelKey = ResourceKeys.ValidateOptional(modelKey, nameof(modelKey));
            if (behaviors == null) throw new ArgumentNullException(nameof(behaviors));
            var sorted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string behavior in behaviors)
                if (!sorted.Add(ResourceKeys.Validate(behavior, nameof(behaviors))))
                    throw new ArgumentException("Duplicate behavior.", nameof(behaviors));
            Behaviors = new List<string>(sorted).AsReadOnly();
            Attributes = attributes;
        }
    }

    /// <summary>
    /// Compiled entity types, indexed by the runtime id that <see cref="EntityTypeId"/> carries.
    /// </summary>
    public sealed class EntityTypeRegistry
    {
        private readonly EntityTypeDefinition[] types;
        private readonly Dictionary<string, ushort> ids;

        /// <summary>
        /// Compatibility hash both peers compare. Covers identity, behaviours, and every attribute
        /// field; excludes the model, because a client with a different mob model still simulates the
        /// same world while a different hitbox does not.
        /// </summary>
        public string Fingerprint { get; }

        public int Count => types.Length - 1;
        public ushort MaxTypeId => (ushort)(types.Length - 1);

        public EntityTypeRegistry(IEnumerable<EntityTypeDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            var sorted = new SortedDictionary<string, EntityTypeDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null || sorted.ContainsKey(definition.Key))
                    throw new ArgumentException("Null or duplicate entity type definition.", nameof(definitions));
                sorted.Add(definition.Key, definition);
            }
            if (sorted.Count > ushort.MaxValue) throw new ArgumentException("Too many entity types.", nameof(definitions));

            //index 0 stays empty so EntityTypeId.None is what a default-constructed component means,
            //rather than silently naming whichever type happened to sort first.
            types = new EntityTypeDefinition[sorted.Count + 1];
            ids = new Dictionary<string, ushort>(sorted.Count, StringComparer.Ordinal);
            ushort next = 1;
            foreach (var definition in sorted.Values)
            {
                types[next] = definition;
                ids.Add(definition.Key, next);
                next++;
            }

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write("DigBlocks.EntityRegistry.v1");
                writer.Write(Count);
                for (int i = 1; i < types.Length; i++) Write(writer, types[i]);
            }
            using var hash = SHA256.Create();
            Fingerprint = BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        private static void Write(BinaryWriter writer, EntityTypeDefinition type)
        {
            writer.Write(type.Key);
            writer.Write(type.Behaviors.Count);
            foreach (string behavior in type.Behaviors) writer.Write(behavior);
            var attributes = type.Attributes;
            writer.Write((uint)attributes.Flags);
            writer.Write((byte)attributes.Category);
            writer.Write(attributes.Width);
            writer.Write(attributes.Height);
            writer.Write((byte)attributes.GhostMode);
            writer.Write((byte)attributes.GhostOptimization);
            writer.Write(attributes.GhostImportance);
        }

        public bool TryGetId(string key, out ushort id) => ids.TryGetValue(key ?? string.Empty, out id);

        public ushort GetId(string key) => TryGetId(key, out ushort id)
            ? id : throw new KeyNotFoundException("Unknown entity type: " + key);

        public EntityTypeDefinition this[ushort id] => id != EntityTypeId.None && id < types.Length
            ? types[id] : throw new ArgumentOutOfRangeException(nameof(id));

        /// <summary>
        /// Caller-owned attribute table for jobs. Index 0 is the reserved unknown type and holds
        /// defaults so a stray id reads as something harmless rather than out of bounds.
        /// </summary>
        public NativeArray<EntityTypeAttributes> CreateAttributeTable(Allocator allocator)
        {
            var table = new NativeArray<EntityTypeAttributes>(types.Length, allocator);
            table[EntityTypeId.None] = EntityTypeAttributes.Default;
            for (int i = 1; i < types.Length; i++) table[i] = types[i].Attributes;
            return table;
        }
    }
}
