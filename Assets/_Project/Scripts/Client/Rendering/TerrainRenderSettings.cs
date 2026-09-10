using System;
using UnityEngine;

namespace DigBlocks.Client.Rendering
{
    [CreateAssetMenu(menuName = "DigBlocks/Terrain render settings")]
    public sealed class TerrainRenderSettings : ScriptableObject
    {
        [Serializable] public struct MaterialBinding { public string Key; public Material Material; public Texture2DArray Textures; }
        [Serializable] public struct TintBinding { public string Key; public Color Color; }
        public ComputeShader Compute;
        public MaterialBinding[] Materials;
        public TintBinding[] Tints;
        [Range(1, 8)] public int MeshWorkers = 2;
        [Range(16, 32768)] public int MaxChunks = 256;
        [Min(393216)] public int QuadCapacity = 1048576;
        [Min(2359296)] public int UploadBytesPerFrame = 4 * 1024 * 1024;
        [Range(1, 8)] public int UploadSlots = 2;
        [Range(2, 5)] public int FrameSlots = 3;

        //ring depth for a camera other than the one the session drives. A shallower ring costs less memory
        //and only risks reusing the previous frame's visible set when the GPU is still holding both slots
        [Range(2, 5)] public int SecondaryFrameSlots = 2;
        [Min(32)] public float RenderDistance = 512;
        public uint VisualSeed = 0x632be59b;
        public bool ChunkOcclusionCulling = true;
        public bool CastShadows = true;

        //whether terrain draws into cameras besides the session's own, scene views included
        public bool SecondaryCameraRendering = true;
    }
}
