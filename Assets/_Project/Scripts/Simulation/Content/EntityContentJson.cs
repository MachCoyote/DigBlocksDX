using System;
using System.Collections.Generic;
using DigBlocks.Simulation.Definitions;
using DigBlocks.Voxels.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;

namespace DigBlocks.Simulation.Content
{
    //parses authored entity documents. Every field except key is optional and resolves through the
    //archetype chain, exactly as block content does, so a plain mob is three lines.
    internal static class EntityContentJson
    {
        private static readonly string[] CategoryNames = { "mob", "item", "projectile", "player", "marker" };
        private static readonly EntityCategory[] CategoryValues =
            { EntityCategory.Mob, EntityCategory.Item, EntityCategory.Projectile, EntityCategory.Player, EntityCategory.Marker };

        private static readonly string[] GhostModeNames = { "interpolated", "predicted", "ownerPredicted" };
        private static readonly EntityGhostMode[] GhostModeValues =
            { EntityGhostMode.Interpolated, EntityGhostMode.Predicted, EntityGhostMode.OwnerPredicted };

        private static readonly string[] GhostOptimizationNames = { "dynamic", "static" };
        private static readonly EntityGhostOptimization[] GhostOptimizationValues =
            { EntityGhostOptimization.Dynamic, EntityGhostOptimization.Static };

        private static readonly HashSet<string> LayerFields = new HashSet<string>(StringComparer.Ordinal)
            { "key", "archetype", "model", "behaviors" };

        private static readonly HashSet<string> AttributeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "persists", "gravity", "collides", "living", "category", "width", "height",
            "ghostMode", "ghostOptimization", "ghostImportance"
        };

        private static readonly HashSet<string> ModelFields = new HashSet<string>(StringComparer.Ordinal)
            { "key", "texture", "textureSize", "boxes" };

        private static readonly HashSet<string> BoxFields = new HashSet<string>(StringComparer.Ordinal)
            { "name", "pivot", "origin", "size", "uv" };

        internal static EntityArchetypeDefinition ParseArchetype(ContentDocument document)
        {
            var root = Parse(document);
            var archetype = new EntityArchetypeDefinition();
            ReadLayer(document.Origin, root, archetype);
            RejectUnknown(document.Origin, root, Union(LayerFields, AttributeFields));
            return archetype;
        }

        internal static EntityTypeLayer ParseType(ContentDocument document)
        {
            var root = Parse(document);
            var type = new EntityTypeLayer();
            ReadLayer(document.Origin, root, type);
            RejectUnknown(document.Origin, root, Union(LayerFields, AttributeFields));
            return type;
        }

        internal static EntityModelDefinition ParseModel(ContentDocument document)
        {
            var root = Parse(document);
            RejectUnknown(document.Origin, root, ModelFields);
            string key = RequiredString(document.Origin, root, "key");
            string texture = RequiredString(document.Origin, root, "texture");
            int2 size = ReadInt2(document.Origin, root, "textureSize") ?? new int2(64, 32);

            if (!(root["boxes"] is JArray boxes)) throw Fail(document.Origin, key, "A model needs a boxes array.");
            var parsed = new List<EntityModelBox>(boxes.Count);
            for (int i = 0; i < boxes.Count; i++)
            {
                if (!(boxes[i] is JObject box)) throw Fail(document.Origin, key, "boxes[" + i + "] must be an object.");
                RejectUnknown(document.Origin, box, BoxFields, "boxes[" + i + "]");
                try
                {
                    parsed.Add(new EntityModelBox(
                        RequiredString(document.Origin, box, "name"),
                        ReadFloat3(document.Origin, box, "pivot") ?? float3.zero,
                        ReadFloat3(document.Origin, box, "origin") ?? float3.zero,
                        ReadFloat3(document.Origin, box, "size") ?? throw Fail(document.Origin, key, "boxes[" + i + "] needs a size."),
                        ReadInt2(document.Origin, box, "uv") ?? int2.zero));
                }
                catch (ArgumentException error) { throw Fail(document.Origin, key, error.Message, error); }
            }

            try { return new EntityModelDefinition(key, texture, size, parsed); }
            catch (ArgumentException error) { throw Fail(document.Origin, key, error.Message, error); }
        }

        private static void ReadLayer(string origin, JObject root, EntityLayer layer)
        {
            layer.Key = OptionalString(origin, root, "key")
                ?? throw Fail(origin, null, "Missing required field key.");
            layer.ArchetypeKey = OptionalString(origin, root, "archetype");
            layer.ModelKey = OptionalString(origin, root, "model");
            if (root["behaviors"] is JArray behaviors) layer.Behaviors = ToStrings(origin, layer.Key, "behaviors", behaviors);

            var attributes = layer.Attributes;
            attributes.Persists = OptionalBool(origin, root, "persists");
            attributes.Gravity = OptionalBool(origin, root, "gravity");
            attributes.Collides = OptionalBool(origin, root, "collides");
            attributes.Living = OptionalBool(origin, root, "living");
            attributes.Width = OptionalFloat(origin, root, "width");
            attributes.Height = OptionalFloat(origin, root, "height");
            attributes.GhostImportance = OptionalByte(origin, root, "ghostImportance");

            string category = OptionalString(origin, root, "category");
            if (category != null) attributes.Category = ParseEnum(origin, "category", category, CategoryNames, CategoryValues);
            string ghostMode = OptionalString(origin, root, "ghostMode");
            if (ghostMode != null) attributes.GhostMode = ParseEnum(origin, "ghostMode", ghostMode, GhostModeNames, GhostModeValues);
            string optimization = OptionalString(origin, root, "ghostOptimization");
            if (optimization != null)
                attributes.GhostOptimization = ParseEnum(origin, "ghostOptimization", optimization, GhostOptimizationNames, GhostOptimizationValues);
        }

        private static List<string> ToStrings(string origin, string key, string field, JArray array)
        {
            var values = new List<string>(array.Count);
            foreach (var token in array)
            {
                if (token.Type != JTokenType.String) throw Fail(origin, key, field + " must contain strings.");
                values.Add(token.Value<string>());
            }
            return values;
        }

        private static float3? ReadFloat3(string origin, JObject root, string field)
        {
            if (!(root[field] is JArray array)) return null;
            if (array.Count != 3) throw Fail(origin, null, field + " must have three numbers.");
            return new float3(Number(origin, field, array[0]), Number(origin, field, array[1]), Number(origin, field, array[2]));
        }

        private static int2? ReadInt2(string origin, JObject root, string field)
        {
            if (!(root[field] is JArray array)) return null;
            if (array.Count != 2) throw Fail(origin, null, field + " must have two whole numbers.");
            return new int2(Whole(origin, field, array[0]), Whole(origin, field, array[1]));
        }

        private static float Number(string origin, string field, JToken token) =>
            token.Type == JTokenType.Float || token.Type == JTokenType.Integer
                ? token.Value<float>() : throw Fail(origin, null, field + " must contain numbers.");

        private static int Whole(string origin, string field, JToken token) =>
            token.Type == JTokenType.Integer ? token.Value<int>() : throw Fail(origin, null, field + " must contain whole numbers.");

        private static JObject Parse(ContentDocument document)
        {
            try
            {
                var token = JToken.Parse(document.Json);
                if (token is JObject root) return root;
                throw Fail(document.Origin, null, "Expected a JSON object at the document root.");
            }
            catch (JsonException error) { throw Fail(document.Origin, null, "Malformed JSON: " + error.Message, error); }
        }

        private static void RejectUnknown(string origin, JObject root, HashSet<string> known, string scope = null)
        {
            foreach (var property in root)
                if (!known.Contains(property.Key))
                    throw Fail(origin, null, "Unknown field " + property.Key + (scope == null ? "." : " in " + scope + "."));
        }

        private static HashSet<string> Union(params HashSet<string>[] sets)
        {
            var union = new HashSet<string>(StringComparer.Ordinal);
            foreach (var set in sets) foreach (string value in set) union.Add(value);
            return union;
        }

        private static TValue ParseEnum<TValue>(string origin, string field, string value, string[] names, TValue[] values)
        {
            int index = Array.IndexOf(names, value);
            if (index < 0) throw Fail(origin, null, field + " must be one of " + string.Join(", ", names) + ".");
            return values[index];
        }

        private static string RequiredString(string origin, JObject root, string field) =>
            OptionalString(origin, root, field) ?? throw Fail(origin, null, "Missing required field " + field + ".");

        private static string OptionalString(string origin, JObject root, string field)
        {
            var token = root[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.String) throw Fail(origin, null, field + " must be a string.");
            return token.Value<string>();
        }

        private static bool? OptionalBool(string origin, JObject root, string field)
        {
            var token = root[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Boolean) throw Fail(origin, null, field + " must be true or false.");
            return token.Value<bool>();
        }

        private static float? OptionalFloat(string origin, JObject root, string field)
        {
            var token = root[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                throw Fail(origin, null, field + " must be a number.");
            return token.Value<float>();
        }

        private static byte? OptionalByte(string origin, JObject root, string field)
        {
            var token = root[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer) throw Fail(origin, null, field + " must be a whole number.");
            int value = token.Value<int>();
            if (value < 0 || value > byte.MaxValue) throw Fail(origin, null, field + " must be 0-255.");
            return (byte)value;
        }

        private static EntityContentException Fail(string origin, string key, string message, Exception inner = null) =>
            new EntityContentException(key == null ? origin : origin + " (" + key + ")", message, inner);
    }
}
