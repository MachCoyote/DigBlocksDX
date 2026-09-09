using System;
using System.Collections.Generic;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace DigBlocks.Client.Rendering
{
    public sealed class TerrainRenderer : IDisposable
    {
        private struct ChunkGpuData
        {
            public float3 Origin;
            public uint Start, Count, Active, Reserved0, Reserved1;
        }
        private sealed class Completion
        {
            private readonly bool useFence = SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute;
            private readonly Action<AsyncGPUReadbackRequest> callback;
            private GraphicsFence fence;
            private bool pending, issued, done, error;
            public Completion() { callback = request => { error = request.hasError; done = true; }; }
            public bool Ready
            {
                get
                {
                    if (!pending) return true;
                    if (!issued) return false;
                    if (error) throw new InvalidOperationException("Terrain GPU completion readback failed.");
                    return useFence ? fence.passed : done;
                }
            }
            public void Pending() { pending = true; issued = false; done = false; error = false; }
            public void Insert(CommandBuffer command, GraphicsBuffer marker)
            {
                if (useFence) fence = command.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                else command.RequestAsyncReadback(marker, callback);
                issued = true;
            }
        }
        private sealed class Upload : IDisposable
        {
            public readonly GraphicsBuffer Buffer = new(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.LockBufferForWrite, GreedyMesherJob.MaximumQuads, PackedQuad.Stride);
            public Completion Completion = new();
            public MeshRangeAllocator.Allocation Allocation;
            public void Dispose() => Buffer.Dispose();
        }
        private sealed class Batch : IDisposable
        {
            public readonly GraphicsBuffer Visible, Args;
            public readonly MaterialPropertyBlock Properties = new();
            public Batch(int capacity)
            {
                Visible = new GraphicsBuffer(GraphicsBuffer.Target.Append, capacity, 4);
                try
                {
                    Args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 4);
                    Args.SetData(new uint[] { 6, 0, 0, 0 });
                }
                catch { Visible.Dispose(); Args?.Dispose(); throw; }
            }
            public void Dispose() { Visible.Dispose(); Args.Dispose(); }
        }
        private sealed class Frame : IDisposable
        {
            public readonly GraphicsBuffer Chunks;
            public readonly Batch[] Batches;
            public Completion Completion = new();
            public readonly List<MeshRangeAllocator.Allocation> References = new();
            public Bounds Bounds;
            public int Epoch;
            public Frame(int chunks, int capacity, int materials)
            {
                Chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunks, 32);
                Batches = new Batch[materials * 2];
                try { for (int i = 0; i < Batches.Length; i++) Batches[i] = new Batch(capacity); }
                catch { Dispose(); throw; }
            }
            public void Dispose() { foreach (var batch in Batches) batch?.Dispose(); Chunks.Dispose(); }
        }

        private readonly TerrainRenderSettings settings;
        private readonly MeshRangeAllocator allocator;
        private readonly GraphicsBuffer geometry, tints, marker;
        private readonly Material[] materials;
        private readonly Frame[] frames;
        private readonly Upload[] uploads;
        private readonly MeshRangeAllocator.Allocation[] meshes;
        private NativeArray<ChunkGpuData> chunks;
        private readonly CommandBuffer command = new() { name = "DigBlocks terrain streaming" };
        private readonly Plane[] planes = new Plane[6];
        private readonly Vector4[] planeVectors = new Vector4[6];
        private readonly int uploadKernel, cullKernel;
        private static readonly int SrcBlendId = Shader.PropertyToID("_DigBlocksSrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DigBlocksDstBlend");
        private static readonly int ZWriteId = Shader.PropertyToID("_DigBlocksZWrite");
        private static readonly int ZTestId = Shader.PropertyToID("_DigBlocksZTest");
        private Frame lastFrame, submittedFrame;
        private Camera submittedCamera;
        private int epoch;
        private int uploadFrame = -1, bytesThisFrame;
        private bool disposed;
        public int LiveQuads { get; private set; }
        public int AllocatedQuads => allocator.Allocated;
        public long UploadedBytes { get; private set; }
        public int DeferredUploads { get; private set; }

        public TerrainRenderer(CompiledBlockContent content, TerrainRenderSettings settings)
        {
            this.settings = settings;
            try
            {
                Validate(content, settings);
                allocator = new MeshRangeAllocator(settings.QuadCapacity);
                meshes = new MeshRangeAllocator.Allocation[settings.MaxChunks];
                chunks = new NativeArray<ChunkGpuData>(settings.MaxChunks, Allocator.Persistent);
                geometry = new GraphicsBuffer(GraphicsBuffer.Target.Structured, settings.QuadCapacity, PackedQuad.Stride);
                marker = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
                marker.SetData(new uint[] { 0 });
                materials = new Material[content.Materials.Count];
                for (int i = 0; i < materials.Length; i++)
                {
                    foreach (var binding in settings.Materials)
                        if (binding.Key == content.Materials[i].Key)
                        {
                            materials[i] = new Material(binding.Material) { name = "Terrain " + binding.Key, enableInstancing = true };
                            materials[i].SetTexture("_BlockTextures", binding.Textures);
                            break;
                        }
                }
                var colors = new Vector4[256];
                for (int i = 0; i < colors.Length; i++) colors[i] = Vector4.one;
                for (int i = 1; i < content.TintKeys.Count; i++)
                    foreach (var tint in settings.Tints ?? Array.Empty<TerrainRenderSettings.TintBinding>())
                        if (tint.Key == content.TintKeys[i]) colors[i] = (Vector4)tint.Color.linear;
                tints = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 256, 16);
                tints.SetData(colors);
                frames = new Frame[settings.FrameSlots];
                for (int i = 0; i < frames.Length; i++) frames[i] = new Frame(settings.MaxChunks, settings.QuadCapacity, materials.Length);
                uploads = new Upload[settings.UploadSlots];
                for (int i = 0; i < uploads.Length; i++) uploads[i] = new Upload();
                uploadKernel = settings.Compute.FindKernel("Upload"); cullKernel = settings.Compute.FindKernel("Cull");
                RenderPipelineManager.endCameraRendering += EndCamera;
            }
            catch { ReleaseResources(); throw; }
        }

        private static void Validate(CompiledBlockContent content, TerrainRenderSettings settings)
        {
            if (settings == null || settings.Compute == null)
                throw new InvalidOperationException("Terrain render assets are missing or unsupported.");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsInstancing || !SystemInfo.supports2DArrayTextures || !SystemInfo.supportsAsyncGPUReadback ||
                !(SystemInfo.supportsGraphicsFence && SystemInfo.supportsAsyncCompute) && !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Terrain requires compute, indirect instancing, texture arrays and GPU completion tokens.");
            if (content.Materials.Count < 1 || content.Materials.Count > 8) throw new NotSupportedException("Terrain supports up to eight material batches within the initial buffer budget.");
            if (settings.MeshWorkers < 1 || settings.MeshWorkers > 8 || settings.MaxChunks < 1 || settings.MaxChunks > 4096 ||
                settings.QuadCapacity < 2 * GreedyMesherJob.MaximumQuads || settings.QuadCapacity > 16 * 1024 * 1024 ||
                settings.UploadBytesPerFrame < GreedyMesherJob.MaximumQuads * PackedQuad.Stride ||
                settings.FrameSlots < 2 || settings.FrameSlots > 5 || settings.UploadSlots < 1 || settings.UploadSlots > 8 || settings.RenderDistance < 32)
                throw new InvalidOperationException("Invalid terrain resource budgets.");
            foreach (var definition in content.Materials)
            {
                if (definition.RenderLayer != BlockRenderLayer.Opaque) throw new NotSupportedException("This terrain milestone supports opaque materials only.");
                bool found = false;
                foreach (var binding in settings.Materials ?? Array.Empty<TerrainRenderSettings.MaterialBinding>())
                    if (binding.Key == definition.Key)
                    {
                        if (found || binding.Textures == null || binding.Textures.depth < definition.SliceCount || binding.Textures.wrapMode != TextureWrapMode.Repeat)
                            throw new InvalidOperationException("Invalid texture array binding for " + definition.Key);
                        var source = binding.Material;
                        if (source == null || source.shader == null || source.shader.name != "DigBlocks/Terrain" || !source.shader.isSupported ||
                            source.FindPass("ForwardLit") < 0 || source.FindPass("ShadowCaster") < 0 ||
                            source.FindPass("DepthOnly") < 0 || source.FindPass("DepthNormals") < 0)
                            throw new InvalidOperationException("Material binding requires the supported DigBlocks/Terrain shader and its procedural passes: " + definition.Key);
                        found = true;
                    }
                if (!found) throw new InvalidOperationException("Missing texture array binding for " + definition.Key);
            }
            for (int i = 0; i < content.SolidAppearance.Count; i++)
                if (content.SolidAppearance[i].IsVisible && !content.Registry.GetSolid((uint)i).Attributes.Has(DigBlocks.Voxels.BlockFlags.FullCube))
                    throw new NotSupportedException("Custom block models are outside this opaque-cube milestone.");
        }

        public bool TryPublish(uint slot, int3 position, NativeArray<PackedQuad> data)
        {
            PollUploads();
            if (uploadFrame != Time.frameCount) { uploadFrame = Time.frameCount; bytesThisFrame = 0; }
            if (data.Length == 0) { Replace(slot, position, null); return true; }
            int bytes = data.Length * PackedQuad.Stride;
            int replacedCount = meshes[slot]?.Count ?? 0;
            if (bytes > settings.UploadBytesPerFrame - bytesThisFrame ||
                LiveQuads - replacedCount + data.Length > settings.QuadCapacity - GreedyMesherJob.MaximumQuads)
            { DeferredUploads++; return false; }
            Upload upload = null;
            foreach (var candidate in uploads) if (candidate.Completion.Ready) { upload = candidate; break; }
            if (upload == null) { DeferredUploads++; return false; }
            var allocation = allocator.Allocate(data.Length);
            if (allocation == null) { DeferredUploads++; return false; }
            var mapped = upload.Buffer.LockBufferForWrite<PackedQuad>(0, data.Length);
            NativeArray<PackedQuad>.Copy(data, mapped, data.Length);
            upload.Buffer.UnlockBufferAfterWrite<PackedQuad>(data.Length);
            command.Clear();
            command.SetComputeBufferParam(settings.Compute, uploadKernel, "_Upload", upload.Buffer);
            command.SetComputeBufferParam(settings.Compute, uploadKernel, "_GeometryWrite", geometry);
            command.SetComputeIntParam(settings.Compute, "_UploadCount", data.Length);
            command.SetComputeIntParam(settings.Compute, "_UploadStart", allocation.Start);
            command.DispatchCompute(settings.Compute, uploadKernel, (data.Length + 63) / 64, 1, 1);
            upload.Completion.Pending(); upload.Completion.Insert(command, marker);
            Graphics.ExecuteCommandBuffer(command);
            allocator.Retain(allocation);
            upload.Allocation = allocation;
            Replace(slot, position, allocation); UploadedBytes += bytes; bytesThisFrame += bytes;
            return true;
        }
        private void PollUploads()
        {
            foreach (var upload in uploads)
                if (upload.Allocation != null && upload.Completion.Ready)
                {
                    allocator.Release(upload.Allocation);
                    upload.Allocation = null;
                }
        }
        private void Replace(uint slot, int3 position, MeshRangeAllocator.Allocation allocation)
        {
            var old = meshes[slot];
            if (old != null) { LiveQuads -= old.Count; allocator.Retire(old); }
            meshes[slot] = allocation;
            chunks[(int)slot] = allocation == null ? default : new ChunkGpuData
            {
                Origin = (float3)position * 32, Start = (uint)allocation.Start, Count = (uint)allocation.Count, Active = 1
            };
            if (allocation != null) LiveQuads += allocation.Count;
        }
        //blend and depth state come from material properties rather than shader globals, so the
        //overdraw view is applied to this session's material clones instead of set once for the process
        public void SetOverdrawMode(TerrainOverdrawMode mode)
        {
            if (disposed || materials == null) return;
            bool counting = mode != TerrainOverdrawMode.Off;
            //counting every layer means ignoring depth entirely; counting shaded fragments keeps it
            bool ignoreDepth = mode == TerrainOverdrawMode.AllLayers;
            foreach (var material in materials)
            {
                if (material == null) continue;
                material.SetFloat(SrcBlendId, (float)BlendMode.One);
                material.SetFloat(DstBlendId, (float)(counting ? BlendMode.One : BlendMode.Zero));
                material.SetFloat(ZWriteId, ignoreDepth ? 0 : 1);
                material.SetFloat(ZTestId, (float)(ignoreDepth ? CompareFunction.Always : CompareFunction.LessEqual));
            }
        }

        public void ClearMeshes()
        {
            epoch++;
            for (int i = 0; i < meshes.Length; i++) Replace((uint)i, int3.zero, null);
        }

        public void Draw(Camera camera)
        {
            if (disposed || camera == null || !camera.isActiveAndEnabled) return;
            PollUploads();
            Frame frame = null;
            foreach (var candidate in frames)
                if (candidate.Completion.Ready) { frame = candidate; break; }
            if (frame != null)
            {
                foreach (var reference in frame.References) allocator.Release(reference);
                frame.References.Clear();
                bool any = false;
                var bounds = new Bounds();
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i] == null) continue;
                    allocator.Retain(meshes[i]); frame.References.Add(meshes[i]);
                    var chunkBounds = new Bounds((Vector3)chunks[i].Origin + Vector3.one * 16, Vector3.one * 32);
                    if (!any) bounds = chunkBounds; else bounds.Encapsulate(chunkBounds);
                    any = true;
                }
                frame.Epoch = epoch; frame.Bounds = bounds;
                frame.Chunks.SetData(chunks);
                GeometryUtility.CalculateFrustumPlanes(camera, planes);
                for (int i = 0; i < 6; i++) planeVectors[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
                command.Clear();
                command.SetComputeBufferParam(settings.Compute, cullKernel, "_Geometry", geometry);
                command.SetComputeBufferParam(settings.Compute, cullKernel, "_Chunks", frame.Chunks);
                command.SetComputeIntParam(settings.Compute, "_ChunkCount", settings.MaxChunks);
                command.SetComputeVectorArrayParam(settings.Compute, "_Planes", planeVectors);
                command.SetComputeVectorParam(settings.Compute, "_CameraPosition", camera.transform.position);
                command.SetComputeFloatParam(settings.Compute, "_RangeSquared", settings.RenderDistance * settings.RenderDistance);
                for (int i = 0; i < frame.Batches.Length; i++)
                {
                    var batch = frame.Batches[i];
                    batch.Visible.SetCounterValue(0);
                    command.SetComputeBufferParam(settings.Compute, cullKernel, "_Visible", batch.Visible);
                    command.SetComputeIntParam(settings.Compute, "_Material", i / 2);
                    command.SetComputeIntParam(settings.Compute, "_ShadowOnly", i % 2);
                    if (i % 2 == 0 || settings.CastShadows) command.DispatchCompute(settings.Compute, cullKernel, settings.MaxChunks, 1, 1);
                    command.CopyCounterValue(batch.Visible, batch.Args, 4);
                }
                Graphics.ExecuteCommandBuffer(command);
                lastFrame = frame;
            }
            else frame = lastFrame;
            if (frame == null || frame.Epoch != epoch || frame.References.Count == 0) return;
            //a fallback draw can overlap the old token's callback. Never let that callback complete the new draw.
            if (!frame.Completion.Ready) frame.Completion = new Completion();
            frame.Completion.Pending();
            for (int i = 0; i < frame.Batches.Length; i++)
            {
                bool shadows = (i & 1) != 0;
                if (shadows && !settings.CastShadows) continue;
                var batch = frame.Batches[i];
                batch.Properties.SetBuffer("_Geometry", geometry);
                batch.Properties.SetBuffer("_Chunks", frame.Chunks);
                batch.Properties.SetBuffer("_Visible", batch.Visible);
                batch.Properties.SetBuffer("_Tints", tints);
                var parameters = new RenderParams(materials[i / 2])
                {
                    camera = camera, worldBounds = frame.Bounds, matProps = batch.Properties,
                    shadowCastingMode = shadows ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off, receiveShadows = true
                };
                Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, batch.Args);
            }
            submittedFrame = frame; submittedCamera = camera;
        }

        private void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != submittedCamera || submittedFrame == null) return;
            command.Clear(); submittedFrame.Completion.Insert(command, marker);
            context.ExecuteCommandBuffer(command);
            submittedFrame = null; submittedCamera = null;
        }

        //call only after the final submitted camera frame; ordinary streaming never waits for GPU completion.
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            RenderPipelineManager.endCameraRendering -= EndCamera;
            var completed = AsyncGPUReadback.Request(marker); completed.WaitForCompletion();
            ReleaseResources();
        }
        private void ReleaseResources()
        {
            if (frames != null) foreach (var frame in frames) frame?.Dispose();
            if (uploads != null) foreach (var upload in uploads) upload?.Dispose();
            if (materials != null) foreach (var material in materials) if (material != null) UnityEngine.Object.Destroy(material);
            geometry?.Dispose(); tints?.Dispose(); marker?.Dispose();
            if (chunks.IsCreated) chunks.Dispose();
            command.Dispose();
        }
    }
}
