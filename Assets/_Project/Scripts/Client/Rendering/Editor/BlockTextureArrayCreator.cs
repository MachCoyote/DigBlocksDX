using System.IO;
using UnityEditor;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor
{
    //Adds an "Assets/Create" entry for a new block texture array. All slicing options live on the
    //ScriptedImporter, so the created file only needs to exist; its contents are a human-readable hint.
    internal static class BlockTextureArrayCreator
    {
        private const string Template =
            "# DigBlocks block texture array.\r\n" +
            "# Select this asset and configure the atlas slicing in the Inspector.\r\n";

        [MenuItem("Assets/Create/DigBlocks/Block Texture Array", priority = 310)]
        private static void Create()
        {
            var folder = "Assets";
            foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path))
                    continue;
                folder = AssetDatabase.IsValidFolder(path)
                    ? path
                    : Path.GetDirectoryName(path).Replace('\\', '/');
                break;
            }

            var assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{folder}/BlockTextureArray.{BlockTextureArrayImporter.Extension}");
            File.WriteAllText(assetPath, Template);
            AssetDatabase.ImportAsset(assetPath);

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
