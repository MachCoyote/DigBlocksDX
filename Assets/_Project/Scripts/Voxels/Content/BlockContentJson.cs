using System;
using System.Collections.Generic;
using DigBlocks.Voxels.Definitions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DigBlocks.Voxels.Content
{
    //parses the authored JSON schema. Fields are flat rather than nested so a plain block stays three lines,
    //and every unknown field is an error: a silently ignored typo would ship as a wrong block.
    internal static class BlockContentJson
    {
        private static readonly string[] FaceNames = { "down", "up", "north", "south", "west", "east" };
        //applied before the individual face names so a specific face always wins over its group.
        private static readonly string[] GroupNames = { "end", "side" };

        private static readonly HashSet<string> MaterialFields = new HashSet<string>(StringComparer.Ordinal)
            { "key", "renderLayer", "slices" };

        private static readonly HashSet<string> AttributeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "opaque", "fullCube", "collides", "replaceable", "permitsFluid", "requiresTool", "flammable",
            "unbreakable", "randomTicks", "hardness", "blastResistance", "friction", "tool", "toolTier",
            "lightEmission", "lightAttenuation", "flammabilityCatch", "flammabilitySpread"
        };

        private static readonly HashSet<string> AppearanceFields = new HashSet<string>(StringComparer.Ordinal)
            { "material", "texture", "textures", "rotation", "rotations", "tint", "tints", "randomizeRotation", "randomizeRotations" };

        private static readonly HashSet<string> LayerFields = new HashSet<string>(StringComparer.Ordinal)
            { "key", "archetype", "behavior", "model", "tags", "properties", "blockEntity", "drops", "sounds", "states" };

        private static readonly HashSet<string> BlockFields = new HashSet<string>(StringComparer.Ordinal)
            { "channel", "invisible" };

        internal static RenderMaterialDefinition ParseMaterial(ContentDocument document)
        {
            var root = Parse(document);
            RejectUnknown(document.Origin, root, MaterialFields);
            string key = RequiredString(document.Origin, root, "key");
            var layer = ParseEnum(document.Origin, "renderLayer", OptionalString(document.Origin, root, "renderLayer") ?? "opaque",
                new[] { "opaque", "cutout", "transparent" }, new[] { BlockRenderLayer.Opaque, BlockRenderLayer.Cutout, BlockRenderLayer.Transparent });
            int slices = RequiredInt(document.Origin, root, "slices");
            try { return new RenderMaterialDefinition(key, layer, slices); }
            catch (ArgumentException error) { throw Fail(document.Origin, key, error.Message, error); }
        }

        internal static BlockArchetype ParseArchetype(ContentDocument document)
        {
            var root = Parse(document);
            var archetype = new BlockArchetype();
            ReadLayer(document.Origin, root, archetype);
            RejectUnknown(document.Origin, root, Union(LayerFields, AttributeFields, AppearanceFields));
            return archetype;
        }

        internal static BlockDefinition ParseBlock(ContentDocument document)
        {
            var root = Parse(document);
            var block = new BlockDefinition();
            ReadLayer(document.Origin, root, block);
            block.Channel = ParseEnum(document.Origin, "channel", OptionalString(document.Origin, root, "channel") ?? "solid",
                new[] { "solid", "fluid" }, new[] { BlockChannel.Solid, BlockChannel.Fluid });
            block.Invisible = OptionalBool(document.Origin, root, "invisible") ?? false;
            var known = Union(LayerFields, AttributeFields, AppearanceFields);
            foreach (string field in BlockFields) known.Add(field);
            RejectUnknown(document.Origin, root, known);
            return block;
        }

        private static void ReadLayer(string origin, JObject root, BlockLayer layer)
        {
            layer.Key = RequiredString(origin, root, "key");
            layer.ArchetypeKey = OptionalString(origin, root, "archetype");
            layer.BehaviorKey = OptionalString(origin, root, "behavior");
            layer.ModelKey = OptionalString(origin, root, "model");
            layer.BlockEntityKey = OptionalString(origin, root, "blockEntity");
            layer.DropsKey = OptionalString(origin, root, "drops");
            layer.SoundSetKey = OptionalString(origin, root, "sounds");

            var tags = root["tags"];
            if (tags != null)
            {
                if (tags.Type != JTokenType.Array) throw Fail(origin, layer.Key, "tags must be an array.");
                foreach (var tag in tags) layer.Tags.Add(tag.Value<string>());
            }

            ReadProperties(origin, root, layer);
            layer.Attributes = ReadAttributes(origin, root);
            layer.Appearance = ReadAppearance(origin, layer.Key, root);
            ReadStateOverrides(origin, root, layer);
        }

        private static void ReadProperties(string origin, JObject root, BlockLayer layer)
        {
            if (!(root["properties"] is JObject properties))
            {
                if (root["properties"] != null) throw Fail(origin, layer.Key, "properties must be an object.");
                return;
            }
            foreach (var entry in properties)
            {
                IEnumerable<string> values;
                string defaultValue = null;
                if (entry.Value is JArray array) values = ToStrings(origin, layer.Key, entry.Key, array);
                else if (entry.Value is JObject shape)
                {
                    RejectUnknown(origin, shape, new HashSet<string>(StringComparer.Ordinal) { "values", "default" },
                        "property " + entry.Key);
                    if (!(shape["values"] is JArray declared)) throw Fail(origin, layer.Key, "property " + entry.Key + " needs a values array.");
                    values = ToStrings(origin, layer.Key, entry.Key, declared);
                    defaultValue = shape["default"]?.Value<string>();
                }
                else throw Fail(origin, layer.Key, "property " + entry.Key + " must be an array or an object.");
                try { layer.Properties.Add(new BlockProperty(entry.Key, values, defaultValue)); }
                catch (ArgumentException error) { throw Fail(origin, layer.Key, "property " + entry.Key + ": " + error.Message, error); }
            }
        }

        private static void ReadStateOverrides(string origin, JObject root, BlockLayer layer)
        {
            var states = root["states"];
            if (states == null) return;
            if (!(states is JArray entries)) throw Fail(origin, layer.Key, "states must be an array.");
            var known = Union(AttributeFields, AppearanceFields);
            known.Add("when");
            foreach (var token in entries)
            {
                if (!(token is JObject entry)) throw Fail(origin, layer.Key, "each state override must be an object.");
                RejectUnknown(origin, entry, known, "state override");
                if (!(entry["when"] is JObject when)) throw Fail(origin, layer.Key, "a state override needs a when object.");
                var conditions = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var condition in when) conditions.Add(condition.Key, condition.Value.Value<string>());
                try
                {
                    layer.StateOverrides.Add(new BlockStateOverride(conditions,
                        ReadAttributes(origin, entry), ReadAppearance(origin, layer.Key, entry)));
                }
                catch (ArgumentException error) { throw Fail(origin, layer.Key, error.Message, error); }
            }
        }

        private static BlockAttributeOverrides ReadAttributes(string origin, JObject root)
        {
            var attributes = new BlockAttributeOverrides
            {
                Opaque = OptionalBool(origin, root, "opaque"),
                FullCube = OptionalBool(origin, root, "fullCube"),
                Collides = OptionalBool(origin, root, "collides"),
                Replaceable = OptionalBool(origin, root, "replaceable"),
                PermitsFluid = OptionalBool(origin, root, "permitsFluid"),
                RequiresTool = OptionalBool(origin, root, "requiresTool"),
                Flammable = OptionalBool(origin, root, "flammable"),
                Unbreakable = OptionalBool(origin, root, "unbreakable"),
                RandomTicks = OptionalBool(origin, root, "randomTicks"),
                Hardness = OptionalFloat(origin, root, "hardness"),
                BlastResistance = OptionalFloat(origin, root, "blastResistance"),
                Friction = OptionalFloat(origin, root, "friction"),
                ToolTier = OptionalByte(origin, root, "toolTier"),
                LightEmission = OptionalByte(origin, root, "lightEmission"),
                LightAttenuation = OptionalByte(origin, root, "lightAttenuation"),
                FlammabilityCatch = OptionalByte(origin, root, "flammabilityCatch"),
                FlammabilitySpread = OptionalByte(origin, root, "flammabilitySpread")
            };
            string tool = OptionalString(origin, root, "tool");
            if (tool != null)
                attributes.ToolClass = ParseEnum(origin, "tool", tool,
                    new[] { "none", "pickaxe", "axe", "shovel", "hoe", "shears", "sword" },
                    new[] { BlockToolClass.None, BlockToolClass.Pickaxe, BlockToolClass.Axe, BlockToolClass.Shovel,
                        BlockToolClass.Hoe, BlockToolClass.Shears, BlockToolClass.Sword });
            return attributes;
        }

        private static BlockAppearanceOverrides ReadAppearance(string origin, string key, JObject root)
        {
            var appearance = new BlockAppearanceOverrides
            {
                MaterialKey = OptionalString(origin, root, "material"),
                Texture = OptionalInt(origin, root, "texture"),
                Rotation = OptionalByte(origin, root, "rotation"),
                TintKey = OptionalString(origin, root, "tint"),
                RandomizeRotation = OptionalBool(origin, root, "randomizeRotation")
            };
            ReadFaceMap(origin, key, root, "textures", appearance,
                (face, token) => face.Texture = token.Value<int>());
            ReadFaceMap(origin, key, root, "rotations", appearance,
                (face, token) => face.Rotation = token.Value<byte>());
            ReadFaceMap(origin, key, root, "tints", appearance,
                (face, token) => face.TintKey = token.Value<string>());
            ReadFaceMap(origin, key, root, "randomizeRotations", appearance, (face, token) =>
            {
                if (token.Type != JTokenType.Boolean) throw Fail(origin, key, "randomizeRotations values must be booleans.");
                face.RandomizeRotation = token.Value<bool>();
            });
            return appearance;
        }

        //group names expand before individual faces, so "side" sets four faces that "north" can still override.
        private static void ReadFaceMap(string origin, string key, JObject root, string field,
            BlockAppearanceOverrides appearance, Action<FaceAppearanceOverrides, JToken> assign)
        {
            var token = root[field];
            if (token == null) return;
            if (!(token is JObject map)) throw Fail(origin, key, field + " must be an object keyed by face.");
            foreach (string group in GroupNames)
            {
                var value = map[group];
                if (value == null) continue;
                foreach (int face in FacesOf(group)) assign(FaceAt(appearance, face), value);
            }
            foreach (var entry in map)
            {
                if (Array.IndexOf(GroupNames, entry.Key) >= 0) continue;
                int face = Array.IndexOf(FaceNames, entry.Key);
                if (face < 0) throw Fail(origin, key, field + " names an unknown face " + entry.Key + ".");
                assign(FaceAt(appearance, face), entry.Value);
            }
        }

        private static IEnumerable<int> FacesOf(string group)
        {
            if (group == "end") return new[] { (int)BlockFace.Down, (int)BlockFace.Up };
            return new[] { (int)BlockFace.North, (int)BlockFace.South, (int)BlockFace.West, (int)BlockFace.East };
        }

        private static FaceAppearanceOverrides FaceAt(BlockAppearanceOverrides appearance, int face) =>
            appearance.Faces[face] ?? (appearance.Faces[face] = new FaceAppearanceOverrides());

        private static List<string> ToStrings(string origin, string key, string field, JArray array)
        {
            var values = new List<string>(array.Count);
            foreach (var token in array)
            {
                if (token.Type != JTokenType.String) throw Fail(origin, key, field + " values must be strings.");
                values.Add(token.Value<string>());
            }
            return values;
        }

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

        private static int RequiredInt(string origin, JObject root, string field) =>
            OptionalInt(origin, root, field) ?? throw Fail(origin, null, "Missing required field " + field + ".");

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

        private static int? OptionalInt(string origin, JObject root, string field)
        {
            var token = root[field];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type != JTokenType.Integer) throw Fail(origin, null, field + " must be a whole number.");
            return token.Value<int>();
        }

        private static byte? OptionalByte(string origin, JObject root, string field)
        {
            int? value = OptionalInt(origin, root, field);
            if (value == null) return null;
            if (value.Value < 0 || value.Value > byte.MaxValue) throw Fail(origin, null, field + " must be 0-255.");
            return (byte)value.Value;
        }

        private static BlockContentException Fail(string origin, string key, string message, Exception inner = null) =>
            new BlockContentException(key == null ? origin : origin + " (" + key + ")", message, inner);
    }
}
