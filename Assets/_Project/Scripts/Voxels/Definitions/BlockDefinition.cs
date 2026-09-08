using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Definitions
{
    public enum BlockChannel : byte { Solid = 0, Fluid = 1 }

    //a finite authored property. Its value list drives state expansion and its order is preserved so the
    //first value is the default unless one is named explicitly.
    public sealed class BlockProperty
    {
        public const int MaxValues = 32;
        public string Name { get; }
        public IReadOnlyList<string> Values { get; }
        public string DefaultValue { get; }

        public BlockProperty(string name, IEnumerable<string> values, string defaultValue = null)
        {
            Name = ResourceKeys.ValidateToken(name, nameof(name));
            if (values == null) throw new ArgumentNullException(nameof(values));
            var unique = new List<string>();
            foreach (string value in values)
            {
                ResourceKeys.ValidateToken(value, nameof(values));
                if (unique.Contains(value)) throw new ArgumentException("Duplicate property value.", nameof(values));
                unique.Add(value);
            }
            if (unique.Count == 0) throw new ArgumentException("A property needs at least one value.", nameof(values));
            if (unique.Count > MaxValues) throw new ArgumentException("Too many property values.", nameof(values));
            Values = unique.AsReadOnly();
            DefaultValue = defaultValue ?? unique[0];
            if (!unique.Contains(DefaultValue)) throw new ArgumentException("Default is not one of the values.", nameof(defaultValue));
        }
    }

    //narrows a layer to the states whose property values all match. Applied in declaration order after the
    //inherited layers resolve, which is what makes per-state variation additive rather than a separate system.
    public sealed class BlockStateOverride
    {
        public IReadOnlyDictionary<string, string> When { get; }
        public BlockAttributeOverrides Attributes { get; }
        public BlockAppearanceOverrides Appearance { get; }

        public BlockStateOverride(IReadOnlyDictionary<string, string> when,
            BlockAttributeOverrides attributes = null, BlockAppearanceOverrides appearance = null)
        {
            if (when == null) throw new ArgumentNullException(nameof(when));
            if (when.Count == 0) throw new ArgumentException("A state override needs at least one condition.", nameof(when));
            var copy = new Dictionary<string, string>(when.Count, StringComparer.Ordinal);
            foreach (var pair in when)
                copy.Add(ResourceKeys.ValidateToken(pair.Key, nameof(when)), ResourceKeys.ValidateToken(pair.Value, nameof(when)));
            When = copy;
            Attributes = attributes; Appearance = appearance;
        }

        public bool Matches(IReadOnlyDictionary<string, string> state)
        {
            foreach (var condition in When)
                if (!state.TryGetValue(condition.Key, out string value) || value != condition.Value) return false;
            return true;
        }
    }

    //load-time authoring record. Every field except Key is optional; unset fields inherit from the archetype
    //chain and then from engine defaults, which is what keeps a plain block a few lines of content.
    public abstract class BlockLayer
    {
        public string Key { get; set; }
        //an archetype's archetype is its parent; chains resolve nearest-last.
        public string ArchetypeKey { get; set; }
        public string BehaviorKey { get; set; }
        public string ModelKey { get; set; }
        public string BlockEntityKey { get; set; }
        public string DropsKey { get; set; }
        public string SoundSetKey { get; set; }
        public List<string> Tags { get; } = new List<string>();
        public List<BlockProperty> Properties { get; } = new List<BlockProperty>();
        public BlockAttributeOverrides Attributes { get; set; }
        public BlockAppearanceOverrides Appearance { get; set; }
        public List<BlockStateOverride> StateOverrides { get; } = new List<BlockStateOverride>();

        internal void ValidateKeys(string origin)
        {
            ResourceKeys.Validate(Key, origin);
            ResourceKeys.ValidateOptional(ArchetypeKey, origin);
            ResourceKeys.ValidateOptional(BehaviorKey, origin);
            ResourceKeys.ValidateOptional(ModelKey, origin);
            ResourceKeys.ValidateOptional(BlockEntityKey, origin);
            ResourceKeys.ValidateOptional(DropsKey, origin);
            ResourceKeys.ValidateOptional(SoundSetKey, origin);
            foreach (string tag in Tags) ResourceKeys.Validate(tag, origin);
            if (Appearance?.MaterialKey != null) ResourceKeys.Validate(Appearance.MaterialKey, origin);
        }
    }

    //a reusable bundle of defaults. Archetypes carry simulation and appearance defaults alike, so a family
    //such as soil is retuned in one file rather than across every block that belongs to it.
    public sealed class BlockArchetype : BlockLayer
    {
    }

    public sealed class BlockDefinition : BlockLayer
    {
        //guards the combinatorial growth that property expansion invites.
        public const int MaxStates = 256;
        public BlockChannel Channel { get; set; } = BlockChannel.Solid;
        //a block with no appearance is never meshed; air and the empty fluid use this.
        public bool Invisible { get; set; }
    }
}
