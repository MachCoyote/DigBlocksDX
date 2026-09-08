using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor
{
    //Bakes a Texture2DArray from a tightly packed tile atlas.
    //Unity's built-in flipbook -> Texture2DArray import path crashes the editor when mipmaps are
    //enabled on small slices; this importer builds the array directly and generates mips via
    //Texture2DArray.Apply, which is stable. The produced asset is a normal Texture2DArray: it can be
    //assigned to material array slots and previewed in the inspector like any imported texture array.
    [ScriptedImporter(1, Extension)]
    public sealed class BlockTextureArrayImporter : ScriptedImporter
    {
        public const string Extension = "blockarray";

        [Tooltip("Tile atlas (PNG or JPG). Its own import settings are ignored; pixels are read from the source file.")]
        public Texture2D sourceTexture;

        [Tooltip("Edge length of one square tile in source pixels.")]
        [Min(1)] public int tileSize = 16;

        [Tooltip("0 bakes a full mip chain; a positive value caps the number of mip levels.")]
        [Min(0)] public int mipCount = 0;

        [Tooltip("Albedo atlases are sRGB. Disable for data textures (masks, normal-like packing).")]
        public bool sRGB = true;

        public FilterMode filterMode = FilterMode.Point;
        public TextureWrapMode wrapMode = TextureWrapMode.Repeat;
        [Range(0, 16)] public int anisoLevel = 0;
        public BlockAtlasSliceOrder sliceOrder = BlockAtlasSliceOrder.TopLeftRowMajor;

        [Tooltip("Bleed opaque colour into fully transparent texels before mip generation to stop dark fringing.")]
        public bool dilateAlpha = true;
        [Min(0)] public int dilatePasses = 4;

        [Tooltip("Keep the baked array readable from scripts at runtime (larger memory footprint).")]
        public bool cpuReadable = false;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            if (sourceTexture == null)
            {
                ctx.LogImportError("Block texture array: no source texture assigned.");
                EmitPlaceholder(ctx);
                return;
            }

            var sourcePath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                ctx.LogImportError($"Block texture array: source '{sourcePath}' could not be located on disk.");
                EmitPlaceholder(ctx);
                return;
            }

            //Re-bake whenever the source image bytes change.
            ctx.DependsOnSourceAsset(sourcePath);

            if (!TryDecodeSource(sourcePath, out var sourcePixels, out int width, out int height))
            {
                ctx.LogImportError($"Block texture array: could not decode image data from '{sourcePath}'.");
                EmitPlaceholder(ctx);
                return;
            }

            BlockAtlasLayout layout;
            try
            {
                layout = BlockTextureArrayBaker.ResolveLayout(width, height, tileSize);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Block texture array: {e.Message}");
                EmitPlaceholder(ctx);
                return;
            }

            var slices = BlockTextureArrayBaker.SliceInto(sourcePixels, width, height, layout, sliceOrder);

            if (dilateAlpha && dilatePasses > 0)
            {
                foreach (var slice in slices)
                    BlockTextureArrayBaker.DilateAlpha(slice, tileSize, dilatePasses);
            }

            //-1 lets Texture2DArray allocate a full mip chain; a positive cap is passed through.
            int resolvedMipCount = mipCount > 0 ? mipCount : -1;
            var array = new Texture2DArray(
                tileSize, tileSize, slices.Length, TextureFormat.RGBA32, resolvedMipCount, linear: !sRGB)
            {
                name = Path.GetFileNameWithoutExtension(ctx.assetPath),
                filterMode = filterMode,
                wrapMode = wrapMode,
                anisoLevel = anisoLevel,
            };

            for (int i = 0; i < slices.Length; i++)
                array.SetPixels32(slices[i], i, 0);

            array.Apply(updateMipmaps: true, makeNoLongerReadable: !cpuReadable);

            ctx.AddObjectToAsset("BlockTextureArray", array);
            ctx.SetMainObject(array);
        }

        private static bool TryDecodeSource(string path, out Color32[] pixels, out int width, out int height)
        {
            pixels = Array.Empty<Color32>();
            width = 0;
            height = 0;

            //Decode straight from the file so the source asset's own readable/compression settings never matter.
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: true);
            try
            {
                if (!decoded.LoadImage(File.ReadAllBytes(path), markNonReadable: false))
                    return false;

                width = decoded.width;
                height = decoded.height;
                pixels = decoded.GetPixels32();
                return true;
            }
            finally
            {
                DestroyImmediate(decoded);
            }
        }

        private static void EmitPlaceholder(AssetImportContext ctx)
        {
            var placeholder = new Texture2DArray(2, 2, 1, TextureFormat.RGBA32, mipChain: false)
            {
                name = "MissingBlockTextureArray",
                filterMode = FilterMode.Point,
            };
            var magenta = new Color32(255, 0, 255, 255);
            placeholder.SetPixels32(new[] { magenta, magenta, magenta, magenta }, 0, 0);
            placeholder.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            ctx.AddObjectToAsset("BlockTextureArray", placeholder);
            ctx.SetMainObject(placeholder);
        }
    }
}
