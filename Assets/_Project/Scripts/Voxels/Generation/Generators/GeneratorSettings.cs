using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    public enum GeneratorParameterKind : byte { Number, Integer, Toggle, Choice }

    /// <summary>
    /// One knob a world type exposes. Carries enough for a settings screen to be built from it, so
    /// generators stay plain code and the eventual UI needs no knowledge of any particular one.
    /// </summary>
    public sealed class GeneratorParameter
    {
        public string Key { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public GeneratorParameterKind Kind { get; }
        public float Minimum { get; }
        public float Maximum { get; }
        public float Default { get; }
        public IReadOnlyList<string> Choices { get; }

        private GeneratorParameter(string key, string displayName, string description, GeneratorParameterKind kind,
            float minimum, float maximum, float defaultValue, IReadOnlyList<string> choices)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A parameter needs a key.", nameof(key));
            Key = key;
            DisplayName = displayName ?? key;
            Description = description;
            Kind = kind;
            Minimum = minimum;
            Maximum = maximum;
            Default = defaultValue;
            Choices = choices ?? Array.Empty<string>();
        }

        public static GeneratorParameter Number(string key, string displayName, float minimum, float maximum,
            float defaultValue, string description = null)
        {
            Validate(key, minimum, maximum, defaultValue);
            return new GeneratorParameter(key, displayName, description, GeneratorParameterKind.Number, minimum, maximum, defaultValue, null);
        }

        public static GeneratorParameter Integer(string key, string displayName, int minimum, int maximum,
            int defaultValue, string description = null)
        {
            Validate(key, minimum, maximum, defaultValue);
            return new GeneratorParameter(key, displayName, description, GeneratorParameterKind.Integer, minimum, maximum, defaultValue, null);
        }

        public static GeneratorParameter Toggle(string key, string displayName, bool defaultValue, string description = null)
            => new GeneratorParameter(key, displayName, description, GeneratorParameterKind.Toggle, 0f, 1f, defaultValue ? 1f : 0f, null);

        public static GeneratorParameter Choice(string key, string displayName, IReadOnlyList<string> choices,
            int defaultIndex = 0, string description = null)
        {
            if (choices == null || choices.Count == 0) throw new ArgumentException("A choice needs options.", nameof(choices));
            if ((uint)defaultIndex >= (uint)choices.Count) throw new ArgumentOutOfRangeException(nameof(defaultIndex));
            return new GeneratorParameter(key, displayName, description, GeneratorParameterKind.Choice, 0f, choices.Count - 1, defaultIndex, choices);
        }

        internal float Coerce(float value)
        {
            float clamped = math.clamp(value, Minimum, Maximum);
            return Kind == GeneratorParameterKind.Number ? clamped : math.round(clamped);
        }

        private static void Validate(string key, float minimum, float maximum, float defaultValue)
        {
            if (minimum > maximum) throw new ArgumentException($"Parameter '{key}' has a minimum above its maximum.", nameof(minimum));
            if (defaultValue < minimum || defaultValue > maximum)
                throw new ArgumentOutOfRangeException(nameof(defaultValue), $"Parameter '{key}' defaults outside its own range.");
        }
    }

    /// <summary>The ordered set of knobs one world type exposes.</summary>
    public sealed class GeneratorParameterSchema : IReadOnlyList<GeneratorParameter>
    {
        public static readonly GeneratorParameterSchema Empty = new GeneratorParameterSchema();

        private readonly GeneratorParameter[] parameters;
        private readonly Dictionary<string, GeneratorParameter> byKey;

        public GeneratorParameterSchema(params GeneratorParameter[] parameters)
        {
            this.parameters = parameters ?? Array.Empty<GeneratorParameter>();
            byKey = new Dictionary<string, GeneratorParameter>(this.parameters.Length, StringComparer.Ordinal);
            foreach (var parameter in this.parameters)
            {
                if (byKey.ContainsKey(parameter.Key))
                    throw new ArgumentException($"Parameter '{parameter.Key}' is declared twice.", nameof(parameters));
                byKey.Add(parameter.Key, parameter);
            }
        }

        public int Count => parameters.Length;
        public GeneratorParameter this[int index] => parameters[index];
        public bool TryGet(string key, out GeneratorParameter parameter) => byKey.TryGetValue(key ?? string.Empty, out parameter);

        public GeneratorParameter Require(string key)
            => TryGet(key, out var parameter) ? parameter
                : throw new GenerationContentException($"This world type has no parameter named '{key}'.");

        public IEnumerator<GeneratorParameter> GetEnumerator() => ((IEnumerable<GeneratorParameter>)parameters).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => parameters.GetEnumerator();
    }

    /// <summary>
    /// A world type's knobs, set. Immutable: changing one returns a new set, so a generator built from
    /// a settings value cannot have the ground moved under it.
    /// </summary>
    public sealed class GeneratorSettings
    {
        public GeneratorParameterSchema Schema { get; }
        private readonly Dictionary<string, float> values;

        private GeneratorSettings(GeneratorParameterSchema schema, Dictionary<string, float> values)
        {
            Schema = schema; this.values = values;
        }

        public static GeneratorSettings Defaults(GeneratorParameterSchema schema)
        {
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            var values = new Dictionary<string, float>(schema.Count, StringComparer.Ordinal);
            foreach (var parameter in schema) values[parameter.Key] = parameter.Default;
            return new GeneratorSettings(schema, values);
        }

        /// <summary>Out-of-range values are brought into range rather than rejected, so a stale saved setting still loads.</summary>
        public GeneratorSettings With(string key, float value)
        {
            var parameter = Schema.Require(key);
            var next = new Dictionary<string, float>(values, StringComparer.Ordinal) { [key] = parameter.Coerce(value) };
            return new GeneratorSettings(Schema, next);
        }

        public GeneratorSettings With(string key, int value) => With(key, (float)value);
        public GeneratorSettings With(string key, bool value) => With(key, value ? 1f : 0f);

        public GeneratorSettings With(string key, string choice)
        {
            var parameter = Schema.Require(key);
            if (parameter.Kind != GeneratorParameterKind.Choice)
                throw new GenerationContentException($"Parameter '{key}' is not a choice.");
            int index = -1;
            for (int candidate = 0; candidate < parameter.Choices.Count; candidate++)
                if (string.Equals(parameter.Choices[candidate], choice, StringComparison.Ordinal)) index = candidate;
            if (index < 0) throw new GenerationContentException($"Parameter '{key}' has no option named '{choice}'.");
            return With(key, (float)index);
        }

        public float Number(string key) => Read(key, GeneratorParameterKind.Number);
        public int Integer(string key) => (int)Read(key, GeneratorParameterKind.Integer);
        public bool Toggle(string key) => Read(key, GeneratorParameterKind.Toggle) != 0f;

        public string Choice(string key)
        {
            var parameter = Schema.Require(key);
            if (parameter.Kind != GeneratorParameterKind.Choice)
                throw new GenerationContentException($"Parameter '{key}' is not a choice.");
            return parameter.Choices[(int)values[key]];
        }

        public int ChoiceIndex(string key) => (int)Read(key, GeneratorParameterKind.Choice);

        private float Read(string key, GeneratorParameterKind expected)
        {
            var parameter = Schema.Require(key);
            if (parameter.Kind != expected)
                throw new GenerationContentException($"Parameter '{key}' is a {parameter.Kind}, not a {expected}.");
            return values[key];
        }
    }
}
