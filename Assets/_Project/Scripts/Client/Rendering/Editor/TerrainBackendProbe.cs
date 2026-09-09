using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DigBlocks.Client.Rendering.Editor
{
    //an isolated GPU feasibility probe; readback intentionally synchronizes for assertions, never for streaming.
    public static class TerrainBackendProbe
    {
        private const string Root = "Assets/_Project/Tests/EditMode/ClientRendering/Fixtures/";
        [StructLayout(LayoutKind.Sequential)]
        private struct Quad { public uint Geometry, Surface, Owner; }

        [MenuItem("DigBlocks/Verification/Terrain GPU backend")]
        public static void Run()
        {
            Directory.CreateDirectory(".utmp");
            string path = ".utmp/terrain-backend-probe.txt";
            File.WriteAllText(path, $"Unity {Application.unityVersion}\n{SystemInfo.graphicsDeviceType}: {SystemInfo.graphicsDeviceName}\n" +
                $"compute={SystemInfo.supportsComputeShaders}, asyncCompute={SystemInfo.supportsAsyncCompute}, instancing={SystemInfo.supportsInstancing}, arrays={SystemInfo.supports2DArrayTextures}, fences={SystemInfo.supportsGraphicsFence}, readback={SystemInfo.supportsAsyncGPUReadback}\n");
            try
            {
                Check(SystemInfo.supportsComputeShaders && SystemInfo.supportsInstancing && SystemInfo.supports2DArrayTextures && (SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute || SystemInfo.supportsAsyncGPUReadback), "Required device features");
                Check(Marshal.SizeOf<Quad>() == 12, "12-byte CPU quad layout");
                Probe(path);
                ProbeUrp(path, false);
                ProbeUrp(path, true);
                File.AppendAllText(path, "PASS: packed GPU decode, indirect instances, compute visibility, tiling, rotation, layers, buffer reuse and GPU completion token.\n");
                Debug.Log("Terrain GPU backend probe PASS. Evidence: " + Path.GetFullPath(path));
            }
            catch (Exception error)
            {
                File.AppendAllText(path, "FAIL: " + error + "\n");
                Debug.LogException(error);
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        private static void Probe(string report)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "TerrainBackendProbe.shader");
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "TerrainBackendProbe.compute");
            Check(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "Probe shader compiled");
            Check(compute != null, "Probe compute loaded");
            using var upload = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite, 16, 12);
            using var arena = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 16, 12);
            using var visible = new GraphicsBuffer(GraphicsBuffer.Target.Append, 16, 4);
            using var args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 4);
            using var cmd = new CommandBuffer { name = "Terrain packed indirect feasibility" };
            var material = new Material(shader);
            var array = new Texture2DArray(2, 2, 2, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
            var target = new RenderTexture(256, 128, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(256, 128, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try
            {
                array.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white }, 0);
                array.SetPixels(new[] { Color.yellow, Color.cyan, Color.magenta, Color.gray }, 1);
                array.Apply(true, true);
                material.SetTexture("_Tiles", array);
                material.SetBuffer("_Quads", arena);
                material.SetBuffer("_Visible", visible);
                target.Create();
                args.SetData(new uint[] { 6, 0, 0, 0 });
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    var mapped = upload.LockBufferForWrite<Quad>(0, 3);
                    mapped[0] = Pack(0, 0, 4, 2, (uint)iteration, 0, 0);
                    mapped[1] = Pack(4, 0, 4, 2, 1, 1, 1);
                    mapped[2] = Pack(16, 0, 4, 2, 0, 0, 2);
                    upload.UnlockBufferAfterWrite<Quad>(3);
                    visible.SetCounterValue(0);
                    cmd.Clear();
                    int uploadKernel = compute.FindKernel("Upload");
                    cmd.SetComputeBufferParam(compute, uploadKernel, "_UploadSource", upload);
                    cmd.SetComputeBufferParam(compute, uploadKernel, "_Destination", arena);
                    cmd.DispatchCompute(compute, uploadKernel, 1, 1, 1);
                    cmd.SetComputeBufferParam(compute, 0, "_Quads", arena);
                    cmd.SetComputeBufferParam(compute, 0, "_Visible", visible);
                    cmd.DispatchCompute(compute, 0, 1, 1, 1);
                    cmd.CopyCounterValue(visible, args, 4);
                    cmd.SetRenderTarget(target);
                    cmd.ClearRenderTarget(false, true, Color.black);
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, args);
                    bool useFence = SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute;
                    var fence = useFence ? cmd.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations) : default;
                    Graphics.ExecuteCommandBuffer(cmd);
                    //readback is a completion token when Unity cannot poll a queue fence (e.g. this Vulkan backend).
                    var completion = useFence ? default : AsyncGPUReadback.Request(args);
                    if (!useFence) completion.WaitForCompletion();
                    //only the probe uses synchronous readback. It establishes completion before the next mapped write.
                    var actualArgs = new uint[4];
                    args.GetData(actualArgs);
                    Check(actualArgs[0] == 6 && actualArgs[1] == 2, "GPU culls third quad and writes exactly two indirect instances");
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 256, 128), 0, 0);
                    pixels.Apply();
                    bool completed = useFence ? fence.passed : completion.done && !completion.hasError;
                    Check(completed, "GPU completion token finishes");
                    //ignore render-target vertical convention; compare horizontal repetition and layer palettes.
                    var a = pixels.GetPixel(8, 16);
                    var repeat = pixels.GetPixel(40, 16);
                    Check((new Vector3(a.r, a.g, a.b) - new Vector3(repeat.r, repeat.g, repeat.b)).sqrMagnitude < 0.001f, "One repeat per voxel across greedy quad");
                    Check(a.maxColorComponent > 0.5f && !(a.r > 0.9f && a.b > 0.9f && a.g < 0.1f && iteration == 0), "Visible packed geometry without error shader");
                    Check(iteration == 0 ? (a.r + a.g + a.b < 1.1f || a.r + a.g + a.b > 2.9f) : (a.r + a.g + a.b > 1.4f), "Texture layer follows packed surface after arena reuse");
                    var right = pixels.GetPixel(136, 16);
                    var rightAcross = pixels.GetPixel(152, 16);
                    bool column0 = ColorDistance(right, Color.yellow) < 0.03f && ColorDistance(rightAcross, Color.magenta) < 0.03f;
                    bool column1 = ColorDistance(right, Color.cyan) < 0.03f && ColorDistance(rightAcross, Color.gray) < 0.03f;
                    Check(column0 || column1, "Quarter-turn rotation traverses a texture column across a horizontal surface row");
                    File.WriteAllBytes($".utmp/terrain-backend-probe-{iteration}.png", pixels.EncodeToPNG());
                    File.AppendAllText(report, $"iteration={iteration}, args=[{string.Join(",", actualArgs)}], stride={arena.stride}, completion={(useFence ? "fence" : "readback")}, completed={completed}, sample={a}\n");
                }
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(array);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static float ColorDistance(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        private static void ProbeUrp(string report, bool indexed)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Root + "TerrainUrpProbe.shader");
            Check(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "URP shader compiled");
            using var quads = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 12);
            using var args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
            using var indexedArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            var template = new Mesh { name = "Probe triangle template" };
            template.vertices = new[] { Vector3.zero, Vector3.up, Vector3.one, Vector3.zero, Vector3.one, Vector3.right };
            template.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            var material = new Material(shader) { enableInstancing = true };
            var cameraObject = new GameObject("Terrain probe camera") { hideFlags = HideFlags.HideAndDontSave };
            var lightObject = new GameObject("Terrain probe light") { hideFlags = HideFlags.HideAndDontSave };
            var target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            var previousSun = RenderSettings.sun;
            var probeCameras = new System.Collections.Generic.List<GameObject>();
            try
            {
                var floor = Pack(0, 0, 4, 4, 0, 0, 0);
                floor.Geometry |= 1u << 28;
                var wall = Pack(1, 0, 2, 2, 0, 0, 0);
                wall.Geometry |= 2u << 12 | 3u << 28;
                quads.SetData(new[] { floor, wall });
                args.SetData(new[] { new GraphicsBuffer.IndirectDrawArgs { vertexCountPerInstance = 6, instanceCount = 2 } });
                indexedArgs.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs { indexCountPerInstance = 6, instanceCount = 2 } });
                material.SetBuffer("_Quads", quads);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = 1 << 31;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.orthographic = true;
                camera.orthographicSize = 3.8f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 40;
                camera.transform.position = new Vector3(7, 7, -7);
                camera.transform.LookAt(new Vector3(2, 0.5f, 2));
                var cameraData = camera.GetUniversalAdditionalCameraData();
                cameraData.renderPostProcessing = false;
                cameraData.renderShadows = true;
                cameraData.volumeLayerMask = 0;
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 0.7f;
                light.cullingMask = 1 << 31;
                light.shadows = LightShadows.Hard;
                light.shadowBias = 0.01f;
                light.shadowNormalBias = 0.01f;
                light.transform.rotation = Quaternion.Euler(50, 120, 0);
                RenderSettings.sun = light;
                target.Create();
                Color[] bright = null, shadowed = null;
                for (int mode = 0; mode < 3; mode++)
                {
                    //draw registrations live for a frame. Distinct cameras prevent earlier modes accumulating draws.
                    var modeCameraObject = UnityEngine.Object.Instantiate(cameraObject);
                    probeCameras.Add(modeCameraObject);
                    var modeCamera = modeCameraObject.GetComponent<Camera>();
                    light.color = mode == 2 ? Color.black : Color.white;
                    var parameters = new RenderParams(material)
                    {
                        camera = modeCamera, layer = 31, worldBounds = new Bounds(new Vector3(2, 1, 2), new Vector3(4, 2, 4)),
                        shadowCastingMode = mode == 1 ? ShadowCastingMode.TwoSided : ShadowCastingMode.Off,
                        receiveShadows = true
                    };
                    if (indexed) Graphics.RenderMeshIndirect(parameters, template, indexedArgs);
                    else Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, args);
                    RenderPipeline.SubmitRenderRequest(modeCamera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    pixels.Apply();
                    var colors = pixels.GetPixels();
                    File.WriteAllBytes($".utmp/terrain-urp-probe-{indexed}-{mode}.png", pixels.EncodeToPNG());
                    if (mode == 0) bright = colors;
                    else if (mode == 1) shadowed = colors;
                    else
                    {
                        int litPixels = 0, shadowPixels = 0;
                        for (int i = 0; i < colors.Length; i++)
                        {
                            if (bright[i].grayscale - colors[i].grayscale > 0.05f) litPixels++;
                            if (bright[i].grayscale - shadowed[i].grayscale > 0.05f) shadowPixels++;
                        }
                        File.AppendAllText(report, $"URP indexed={indexed}, pipeline={GraphicsSettings.currentRenderPipeline.name}, light-responsive pixels={litPixels}, shadow-responsive pixels={shadowPixels}\n");
                        Check(litPixels > 100, "Actual URP main light illuminates packed indirect geometry");
                        Check(shadowPixels > 100, "Packed indirect geometry casts and receives URP shadows");
                    }
                }
            }
            finally
            {
                RenderSettings.sun = previousSun;
                foreach (var probeCamera in probeCameras) UnityEngine.Object.DestroyImmediate(probeCamera);
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(lightObject);
                UnityEngine.Object.DestroyImmediate(pixels);
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(template);
            }
        }
        private static Quad Pack(uint x, uint y, uint width, uint height, uint layer, uint rotation, uint owner) => new Quad
        {
            Geometry = x | y << 6 | (width - 1) << 18 | (height - 1) << 23,
            Surface = layer | rotation << 24,
            Owner = owner
        };
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
