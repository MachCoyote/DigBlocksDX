using System;
using System.Collections.Generic;
using System.IO;
using DigBlocks.Simulation.Definitions;
using DigBlocks.Voxels.Content;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DigBlocks.Simulation.Content
{
    public enum EntityContentCategory { Archetypes, Types, Models }

    public interface IEntityContentSource
    {
        IEnumerable<ContentDocument> Read(EntityContentCategory category);
    }

    //reads entity content from a directory tree. The path comes from the composition root, so this
    //assembly stays free of UnityEngine and a dedicated server loads the same files headlessly.
    //ContentDocument is shared with block content deliberately: one vocabulary for authored documents.
    //If a third content kind appears, the directory walk below is what should be extracted, not copied.
    public sealed class DirectoryEntityContentSource : IEntityContentSource
    {
        private readonly string root;

        public DirectoryEntityContentSource(string root) =>
            this.root = root ?? throw new ArgumentNullException(nameof(root));

        public static string FolderOf(EntityContentCategory category)
        {
            switch (category)
            {
                case EntityContentCategory.Archetypes: return "entity_archetypes";
                case EntityContentCategory.Types: return "entities";
                case EntityContentCategory.Models: return "entity_models";
                default: throw new ArgumentOutOfRangeException(nameof(category));
            }
        }

        public IEnumerable<ContentDocument> Read(EntityContentCategory category)
        {
            string folder = Path.Combine(root, FolderOf(category));
            //every entity category is optional: a world with no entity content is a valid world, and
            //it compiles to an empty registry rather than a load failure.
            if (!Directory.Exists(folder)) yield break;
            var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
            //ordinal ordering keeps load order reproducible across filesystems.
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string json;
                try { json = File.ReadAllText(file); }
                catch (IOException error) { throw new EntityContentException(null, "Cannot read " + file + ".", error); }
                foreach (var document in SplitDocuments(Path.GetFileName(file), json)) yield return document;
            }
        }

        //a file's root is either one object or an array of them, so related entity types can share a
        //file. Array order is load order within the file; files still sort ordinally against each other.
        private static IEnumerable<ContentDocument> SplitDocuments(string fileName, string json)
        {
            JToken parsed;
            try { parsed = JToken.Parse(json); }
            catch (JsonException error)
            { throw new EntityContentException(null, "Malformed JSON in " + fileName + ": " + error.Message, error); }

            if (!(parsed is JArray array))
            {
                yield return new ContentDocument(fileName, json);
                yield break;
            }

            for (int i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JObject entry))
                    throw new EntityContentException(null, fileName + "[" + i + "] must be a JSON object.");
                string key = entry["key"]?.Type == JTokenType.String ? entry["key"].Value<string>() : i.ToString();
                yield return new ContentDocument(fileName + "[" + key + "]", entry.ToString(Formatting.None));
            }
        }
    }

    //in-memory source for tests and for content that is generated rather than authored on disk.
    public sealed class MemoryEntityContentSource : IEntityContentSource
    {
        private readonly Dictionary<EntityContentCategory, List<ContentDocument>> documents = new();

        public MemoryEntityContentSource Add(EntityContentCategory category, string origin, string json)
        {
            if (!documents.TryGetValue(category, out var list)) documents.Add(category, list = new List<ContentDocument>());
            list.Add(new ContentDocument(origin, json));
            return this;
        }

        public IEnumerable<ContentDocument> Read(EntityContentCategory category) =>
            documents.TryGetValue(category, out var list) ? list : (IEnumerable<ContentDocument>)Array.Empty<ContentDocument>();
    }
}
