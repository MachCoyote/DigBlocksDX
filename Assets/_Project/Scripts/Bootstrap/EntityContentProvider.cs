using System.IO;
using DigBlocks.Simulation.Content;
using DigBlocks.Simulation.Definitions;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    //resolves the shipped entity content root and caches the compiled result, for the same reason
    //BlockContentProvider does: sessions restart often and recompiling would only risk fingerprint churn.
    public static class EntityContentProvider
    {
        private static CompiledEntityContent cached;
        private static string cachedRoot;

        public static string DefaultRoot =>
            Path.Combine(Application.streamingAssetsPath, EntityContentLoader.DefaultContentFolder);

        public static CompiledEntityContent Load(string root = null)
        {
            root = root ?? DefaultRoot;
            if (cached != null && cachedRoot == root) return cached;
            //a content failure must surface as a session failure: a partial registry cannot bind to any peer.
            var content = EntityContentLoader.LoadFromDirectory(root);
            cached = content; cachedRoot = root;
            return content;
        }

        public static void Invalidate()
        {
            cached = null; cachedRoot = null;
        }
    }
}
