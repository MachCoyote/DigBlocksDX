using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor.Tests
{
    //End-to-end check that the ScriptedImporter produces a usable Texture2DArray for the project atlas
    //that previously crashed Unity's built-in flipbook import path.
    public sealed class BlockTextureArrayImportTests
    {
        private const string TransparentsAtlasPath = "Assets/_Project/Textures/blocks_transparents.blockarray";

        [Test]
        public void TransparentBlockAtlasImportsAsAMippedTextureArray()
        {
            var array = AssetDatabase.LoadAssetAtPath<Texture2DArray>(TransparentsAtlasPath);

            Assert.That(array, Is.Not.Null, $"No Texture2DArray imported at {TransparentsAtlasPath}");
            Assert.That(array.width, Is.EqualTo(16));
            Assert.That(array.height, Is.EqualTo(16));
            Assert.That(array.depth, Is.EqualTo(32 * 32), "512x512 atlas at 16px tiles is 1024 slices");
            Assert.That(array.mipmapCount, Is.GreaterThan(1), "mip chain must be generated");
            Assert.That(array.filterMode, Is.EqualTo(FilterMode.Point));
        }

        [Test]
        public void BlockArrayImporterIsBoundToTheManifestExtension()
        {
            var importer = AssetImporter.GetAtPath(TransparentsAtlasPath);
            Assert.That(importer, Is.TypeOf<BlockTextureArrayImporter>());
        }
    }
}
