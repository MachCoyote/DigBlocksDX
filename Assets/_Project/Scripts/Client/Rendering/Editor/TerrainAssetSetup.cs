using System.IO;
using UnityEditor;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor
{
    public static class TerrainAssetSetup
    {
        [MenuItem("DigBlocks/Terrain/Create default render settings")]
        public static void Create()
        {
            const string path = "Assets/_Project/Resources/TerrainRenderSettings.asset";
            const string materialPath = "Assets/_Project/Materials/Terrain/Opaque.mat";
            Directory.CreateDirectory("Assets/_Project/Resources");
            Directory.CreateDirectory("Assets/_Project/Materials/Terrain");
            AssetDatabase.Refresh();
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/Terrain/Terrain.shader");
                if (shader == null) throw new System.InvalidOperationException("Terrain shader is missing.");
                material = new Material(shader) { name = "Opaque", enableInstancing = true };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            var settings = AssetDatabase.LoadAssetAtPath<TerrainRenderSettings>(path);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<TerrainRenderSettings>();
                settings.Compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Shaders/Terrain/TerrainStreaming.compute");
                settings.Materials = new[] { new TerrainRenderSettings.MaterialBinding
                {
                    Key = "digblocks:opaque", Material = material,
                    Textures = AssetDatabase.LoadAssetAtPath<Texture2DArray>("Assets/_Project/Textures/blocks.blockarray")
                } };
                settings.Tints = new[] { new TerrainRenderSettings.TintBinding { Key = "digblocks:grass", Color = new Color(0.55f, 0.8f, 0.3f) } };
                AssetDatabase.CreateAsset(settings, path);
            }
            //migrate existing settings without replacing authored materials, textures or budgets.
            for (int i = 0; i < (settings.Materials?.Length ?? 0); i++)
                if (settings.Materials[i].Key == "digblocks:opaque" && settings.Materials[i].Material == null)
                {
                    settings.Materials[i].Material = material;
                    EditorUtility.SetDirty(settings);
                }
            AssetDatabase.SaveAssets();
            Debug.Log("Terrain render settings and material assets are ready.");
        }
    }
}
