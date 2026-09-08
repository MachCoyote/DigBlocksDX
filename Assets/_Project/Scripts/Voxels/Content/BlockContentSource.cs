using System;
using System.Collections.Generic;
using System.IO;

namespace DigBlocks.Voxels.Content
{
    public enum BlockContentCategory { Materials, Archetypes, Blocks }

    //one authored document plus the origin used in load errors, so a failure names a file rather than an index.
    public readonly struct ContentDocument
    {
        public readonly string Origin;
        public readonly string Json;

        public ContentDocument(string origin, string json)
        {
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
            Json = json ?? throw new ArgumentNullException(nameof(json));
        }
    }

    public interface IBlockContentSource
    {
        IEnumerable<ContentDocument> Read(BlockContentCategory category);
    }

    //reads content from a directory tree. The path is supplied by the composition root, which keeps this
    //assembly free of UnityEngine and lets a dedicated server load the same files headlessly.
    public sealed class DirectoryBlockContentSource : IBlockContentSource
    {
        private readonly string root;

        public DirectoryBlockContentSource(string root)
        {
            this.root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public static string FolderOf(BlockContentCategory category)
        {
            switch (category)
            {
                case BlockContentCategory.Materials: return "materials";
                case BlockContentCategory.Archetypes: return "archetypes";
                case BlockContentCategory.Blocks: return "blocks";
                default: throw new ArgumentOutOfRangeException(nameof(category));
            }
        }

        public IEnumerable<ContentDocument> Read(BlockContentCategory category)
        {
            string folder = Path.Combine(root, FolderOf(category));
            if (!Directory.Exists(folder))
            {
                //archetypes are optional; materials and blocks are not, and the compiler reports the shortfall.
                if (category == BlockContentCategory.Archetypes) yield break;
                throw new Definitions.BlockContentException(null, "Missing content folder " + folder + ".");
            }
            var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
            //ordinal ordering keeps load order reproducible across filesystems.
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string json;
                try { json = File.ReadAllText(file); }
                catch (IOException error) { throw new Definitions.BlockContentException(null, "Cannot read " + file + ".", error); }
                yield return new ContentDocument(Path.GetFileName(file), json);
            }
        }
    }

    //in-memory source for tests and for content that is generated rather than authored on disk.
    public sealed class MemoryBlockContentSource : IBlockContentSource
    {
        private readonly Dictionary<BlockContentCategory, List<ContentDocument>> documents =
            new Dictionary<BlockContentCategory, List<ContentDocument>>();

        public MemoryBlockContentSource Add(BlockContentCategory category, string origin, string json)
        {
            if (!documents.TryGetValue(category, out var list)) documents.Add(category, list = new List<ContentDocument>());
            list.Add(new ContentDocument(origin, json));
            return this;
        }

        public IEnumerable<ContentDocument> Read(BlockContentCategory category) =>
            documents.TryGetValue(category, out var list) ? list : (IEnumerable<ContentDocument>)Array.Empty<ContentDocument>();
    }
}
