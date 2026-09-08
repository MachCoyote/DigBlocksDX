using System.IO;
using DigBlocks.Voxels.Content;
using DigBlocks.Voxels.Definitions;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    //resolves the shipped content root and caches the compiled result. Sessions restart often and the
    //compiled registry is immutable, so recompiling per session would only risk fingerprint churn.
    public static class BlockContentProvider
    {
        private static CompiledBlockContent cached;
        private static string cachedRoot;

        public static string DefaultRoot =>
            Path.Combine(Application.streamingAssetsPath, BlockContentLoader.DefaultContentFolder);

        public static CompiledBlockContent Load(string root = null)
        {
            root = root ?? DefaultRoot;
            if (cached != null && cachedRoot == root) return cached;
            //a content failure must surface as a session failure: a partial registry cannot bind to any peer.
            var content = BlockContentLoader.LoadFromDirectory(root);
            cached = content; cachedRoot = root;
            return content;
        }

        public static void Invalidate()
        {
            cached = null; cachedRoot = null;
        }
    }
}
