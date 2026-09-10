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
            public uint Start, Count, Active, CameraVisible, Reserved;
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
        //one frame's worth of staged geometry. Chunks are appended into it rather than each taking a
        //buffer sized for the worst mesh imaginable, so how many can be published in a frame is a
        //question of bytes rather than of how many buffers happen to exist.
        private sealed class Staging : IDisposable
        {
            public readonly GraphicsBuffer Buffer;
            public Completion Completion = new();
            public readonly List<MeshRangeAllocator.Allocation> Retained = new();
            public int Used, Cycle = int.MinValue;
            public Staging(int quads) => Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                GraphicsBuffer.UsageFlags.LockBufferForWrite, quads, PackedQuad.Stride);
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

            //how many cameras have been handed this frame and have not finished rendering it yet
            public int Submissions;
            public Frame(int chunks, int capacity, int materials)
            {
                Chunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunks, 32);
                Batches = new Batch[materials * 2];
                try { for (int i = 0; i < Batches.Length; i++) Batches[i] = new Batch(capacity); }
                catch { Dispose(); throw; }
            }
            public void Dispose() { foreach (var batch in Batches) batch?.Dispose(); Chunks.Dispose(); }
        }
        //one camera's view of the geometry: its own cull output, multi-buffered so the GPU keeps reading the
        //previous visible set while the next is built. The main camera's slot lives as long as the renderer;
        //a secondary camera builds one the first frame it culls for itself and gives it back once it stops.
        private sealed class CameraSlot
        {
            public Camera Camera;
            public Frame[] Frames;
            public Frame Last;
            public int DrawnFrame = int.MinValue;
        }
        private struct Submission
        {
            public Camera Camera;
            public Frame Frame;
            public int FrameCount;
        }

        private readonly TerrainRenderSettings settings;
        private readonly ChunkOcclusionGraph occlusionGraph;
        private readonly MeshRangeAllocator allocator;
        private readonly GraphicsBuffer geometry, tints, marker;
        private readonly Material[] materials;
        private readonly CameraSlot primary;
        private readonly List<CameraSlot> secondaries = new();
        private readonly List<Camera> cameraScratch = new();
        private readonly List<Submission> submissions = new();
        private readonly Staging[] stagings;
        private readonly int stagingQuads;
        private int stagingCursor;
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
        //a slot outlives a few idle frames so an editor viewport that skips a repaint keeps its buffers
        private const int SecondarySlotIdleFrames = 60;
        private TerrainSecondaryCameraCulling secondaryCulling;
        private bool secondaryFailed;
        private int epoch;
        private int uploadFrame = -1, bytesThisFrame;
        private bool disposed;
        //cleared by a user setting or by an allocation failure; the main camera always draws
        public bool SecondaryCameraRendering { get; set; } = true;
        public int LiveQuads { get; private set; }
        public int AllocatedQuads => allocator.Allocated;
        public long UploadedBytes { get; private set; }
        public int DeferredUploads { get; private set; }
        public int ResidentGraphNodes => occlusionGraph.ResidentCount;
        //recorded from the main camera's pass only; a secondary viewport culls for itself without reporting
        public int CameraVisibleChunks { get; private set; }
        public int GraphCulledChunks { get; private set; }
        public int CameraVisibleQuads { get; private set; }

        public TerrainRenderer(CompiledBlockContent content, TerrainRenderSettings settings)
        {
            this.settings = settings;
            try
            {
                Validate(content, settings);
                occlusionGraph = new ChunkOcclusionGraph(settings.MaxChunks);
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
                primary = new CameraSlot { Frames = new Frame[settings.FrameSlots] };
                for (int i = 0; i < primary.Frames.Length; i++) primary.Frames[i] = new Frame(settings.MaxChunks, settings.QuadCapacity, materials.Length);
                SecondaryCameraRendering = settings.SecondaryCameraRendering;
                stagingQuads = settings.UploadBytesPerFrame / PackedQuad.Stride;
                stagings = new Staging[settings.UploadSlots];
                for (int i = 0; i < stagings.Length; i++) stagings[i] = new Staging(stagingQuads);
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
            if (settings.MeshWorkers < 1 || settings.MeshWorkers > 8 || settings.MaxChunks < 1 || settings.MaxChunks > 32768 ||
                settings.QuadCapacity < 2 * GreedyMesherJob.MaximumQuads || settings.QuadCapacity > 16 * 1024 * 1024 ||
                settings.UploadBytesPerFrame < GreedyMesherJob.MaximumQuads * PackedQuad.Stride ||
                settings.FrameSlots < 2 || settings.FrameSlots > 5 || settings.SecondaryFrameSlots < 2 || settings.SecondaryFrameSlots > 5 || settings.UploadSlots < 2 || settings.UploadSlots > 8 || settings.RenderDistance < 32)
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

        public bool TryPublish(uint slot, int3 position, NativeArray<PackedQuad> data, ChunkFaceConnectivity connectivity)
        {
            PollUploads();
            if (uploadFrame != Time.frameCount)
            {
                uploadFrame = Time.frameCount; bytesThisFrame = 0;
                stagingCursor = (stagingCursor + 1) % stagings.Length;
            }
            if (data.Length == 0)
            {
                Replace(slot, position, null);
                occlusionGraph.SetNode((int)slot, position, connectivity, false);
                return true;
            }
            int bytes = data.Length * PackedQuad.Stride;
            int replacedCount = meshes[slot]?.Count ?? 0;
            if (bytes > settings.UploadBytesPerFrame - bytesThisFrame ||
                LiveQuads - replacedCount + data.Length > settings.QuadCapacity - GreedyMesherJob.MaximumQuads)
            { DeferredUploads++; return false; }
            var staging = stagings[stagingCursor];
            if (staging.Cycle != uploadFrame)
            {
                //claiming it for this frame, which is only possible once the GPU has finished reading
                //what it still holds from the last time round the ring.
                if (staging.Retained.Count != 0) { DeferredUploads++; return false; }
                staging.Cycle = uploadFrame; staging.Used = 0;
            }
            if (staging.Used + data.Length > stagingQuads) { DeferredUploads++; return false; }
            var allocation = allocator.Allocate(data.Length);
            if (allocation == null) { DeferredUploads++; return false; }
            var mapped = staging.Buffer.LockBufferForWrite<PackedQuad>(staging.Used, data.Length);
            NativeArray<PackedQuad>.Copy(data, mapped, data.Length);
            staging.Buffer.UnlockBufferAfterWrite<PackedQuad>(data.Length);
            command.Clear();
            command.SetComputeBufferParam(settings.Compute, uploadKernel, "_Upload", staging.Buffer);
            command.SetComputeBufferParam(settings.Compute, uploadKernel, "_GeometryWrite", geometry);
            command.SetComputeIntParam(settings.Compute, "_UploadCount", data.Length);
            command.SetComputeIntParam(settings.Compute, "_UploadStart", allocation.Start);
            command.SetComputeIntParam(settings.Compute, "_UploadOffset", staging.Used);
            command.DispatchCompute(settings.Compute, uploadKernel, (data.Length + 63) / 64, 1, 1);
            //one fence per staging, re-inserted after each dispatch: fences are ordered, so the latest
            //passing means every chunk written into this buffer has been consumed.
            staging.Completion.Pending(); staging.Completion.Insert(command, marker);
            Graphics.ExecuteCommandBuffer(command);
            allocator.Retain(allocation);
            staging.Retained.Add(allocation);
            staging.Used += data.Length;
            Replace(slot, position, allocation);
            occlusionGraph.SetNode((int)slot, position, connectivity, true);
            UploadedBytes += bytes; bytesThisFrame += bytes;
            return true;
        }
        private void PollUploads()
        {
            foreach (var staging in stagings)
            {
                if (staging.Retained.Count == 0 || !staging.Completion.Ready) continue;
                foreach (var allocation in staging.Retained) allocator.Release(allocation);
                staging.Retained.Clear();
                staging.Used = 0;
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

        //mirroring costs nothing extra: the secondary camera is handed the main camera's own visible set,
        //so the holes it left behind become visible from outside its frustum
        public void SetSecondaryCameraCulling(TerrainSecondaryCameraCulling mode) => secondaryCulling = mode;

        public void ClearMeshes()
        {
            epoch++;
            for (int i = 0; i < meshes.Length; i++) Replace((uint)i, int3.zero, null);
            occlusionGraph.Clear();
            CameraVisibleQuads = 0; CameraVisibleChunks = 0; GraphCulledChunks = 0;
        }

        public void Remove(uint slot)
        {
            Replace(slot, int3.zero, null);
            occlusionGraph.RemoveNode((int)slot);
        }

        public void SetGraphReady(bool ready) => occlusionGraph.SetReady(ready);

        public void UpdateCameraVisibility(float3 cameraPosition) => UpdateCameraVisibility(cameraPosition, true);

        //the graph holds one visible set at a time, so each camera culls into it and uploads the result
        //before the next camera overwrites it
        private void UpdateCameraVisibility(float3 cameraPosition, bool record)
        {
            occlusionGraph.Cull(cameraPosition, settings.ChunkOcclusionCulling);
            int visibleQuads = 0;
            for (int i = 0; i < chunks.Length; i++)
            {
                var data = chunks[i];
                data.CameraVisible = occlusionGraph.IsCameraVisible(i) ? 1u : 0u;
                chunks[i] = data;
                if (data.CameraVisible != 0 && meshes[i] != null) visibleQuads += meshes[i].Count;
            }
            if (!record) return;
            CameraVisibleQuads = visibleQuads;
            CameraVisibleChunks = occlusionGraph.CameraVisibleCount;
            GraphCulledChunks = occlusionGraph.GraphCulledCount;
        }

        public void Draw(Camera camera)
        {
            if (disposed || camera == null || !camera.isActiveAndEnabled) return;
            PollUploads();
            ResolveStrandedSubmissions();
            primary.Camera = camera;
            DrawCamera(primary, camera, true);
            if (SecondaryCameraRendering && !secondaryFailed)
            {
                cameraScratch.Clear();
                TerrainCameraSet.Collect(camera, cameraScratch);
                for (int i = 0; i < cameraScratch.Count; i++) DrawSecondary(cameraScratch[i]);
                cameraScratch.Clear();
            }
            RetireSecondaries();
        }

        private void DrawSecondary(Camera camera)
        {
            if (secondaryCulling == TerrainSecondaryCameraCulling.MirrorMain)
            {
                //submitting the main camera frame again shows this viewport exactly what that camera kept
                var mirrored = primary.Last;
                if (mirrored != null && mirrored.Epoch == epoch && mirrored.References.Count > 0) Submit(mirrored, camera);
                return;
            }
            var slot = SlotFor(camera);
            if (slot != null) DrawCamera(slot, camera, false);
        }

        private void DrawCamera(CameraSlot slot, Camera camera, bool isPrimary)
        {
            slot.DrawnFrame = Time.frameCount;
            UpdateCameraVisibility(camera.transform.position, isPrimary);
            Frame frame = null;
            foreach (var candidate in slot.Frames)
                if (candidate.Submissions == 0 && candidate.Completion.Ready) { frame = candidate; break; }
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
                slot.Last = frame;
            }
            else frame = slot.Last;
            if (frame == null || frame.Epoch != epoch || frame.References.Count == 0) return;
            Submit(frame, camera);
        }

        private void Submit(Frame frame, Camera camera)
        {
            if (frame.Submissions == 0)
            {
                //a fallback draw can overlap the old token callback. Never let that callback complete the new draw.
                if (!frame.Completion.Ready) frame.Completion = new Completion();
                frame.Completion.Pending();
            }
            frame.Submissions++;
            submissions.Add(new Submission { Camera = camera, Frame = frame, FrameCount = Time.frameCount });
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
                    camera = camera, layer = TerrainCameraSet.TerrainLayer, worldBounds = frame.Bounds, matProps = batch.Properties,
                    shadowCastingMode = shadows ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off, receiveShadows = true
                };
                Graphics.RenderPrimitivesIndirect(parameters, MeshTopology.Triangles, batch.Args);
            }
        }

        private CameraSlot SlotFor(Camera camera)
        {
            for (int i = 0; i < secondaries.Count; i++) if (secondaries[i].Camera == camera) return secondaries[i];
            var slot = new CameraSlot { Camera = camera, Frames = new Frame[settings.SecondaryFrameSlots] };
            try
            {
                for (int i = 0; i < slot.Frames.Length; i++) slot.Frames[i] = new Frame(settings.MaxChunks, settings.QuadCapacity, materials.Length);
            }
            catch (Exception error)
            {
                //an extra viewport is never worth taking the session down with it
                Release(slot);
                secondaryFailed = true;
                Debug.LogException(new InvalidOperationException("Terrain could not allocate cull buffers for a secondary camera; extra viewports are disabled.", error));
                return null;
            }
            secondaries.Add(slot);
            return slot;
        }

        //buffers are handed back once the viewport has been idle a while and the GPU has finished with them
        private void RetireSecondaries()
        {
            for (int i = secondaries.Count - 1; i >= 0; i--)
            {
                var slot = secondaries[i];
                if (slot.Camera != null && Time.frameCount - slot.DrawnFrame < SecondarySlotIdleFrames) continue;
                bool busy = false;
                foreach (var frame in slot.Frames)
                    if (frame.Submissions > 0 || !frame.Completion.Ready) { busy = true; break; }
                if (busy) continue;
                secondaries.RemoveAt(i);
                Release(slot);
            }
        }

        private void Release(CameraSlot slot)
        {
            if (slot.Frames == null) return;
            foreach (var frame in slot.Frames)
            {
                if (frame == null) continue;
                foreach (var reference in frame.References) allocator.Release(reference);
                frame.References.Clear();
                frame.Dispose();
            }
            slot.Frames = null; slot.Last = null;
        }

        private void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            for (int i = submissions.Count - 1; i >= 0; i--)
            {
                if (submissions[i].Camera != camera) continue;
                var frame = submissions[i].Frame;
                submissions.RemoveAt(i);
                //the last camera holding the frame closes it; a mirrored frame outlives the camera that built it
                if (--frame.Submissions > 0) continue;
                command.Clear(); frame.Completion.Insert(command, marker);
                context.ExecuteCommandBuffer(command);
            }
        }

        //a camera that never reached the pipeline, such as a hidden scene view, would otherwise hold its
        //frame pending forever and drain the ring
        private void ResolveStrandedSubmissions()
        {
            bool queued = false;
            for (int i = submissions.Count - 1; i >= 0; i--)
            {
                if (submissions[i].FrameCount == Time.frameCount) continue;
                var frame = submissions[i].Frame;
                submissions.RemoveAt(i);
                if (--frame.Submissions > 0) continue;
                if (!queued) { command.Clear(); queued = true; }
                frame.Completion.Insert(command, marker);
            }
            if (queued) Graphics.ExecuteCommandBuffer(command);
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
            if (primary?.Frames != null) foreach (var frame in primary.Frames) frame?.Dispose();
            foreach (var slot in secondaries) if (slot.Frames != null) foreach (var frame in slot.Frames) frame?.Dispose();
            secondaries.Clear(); submissions.Clear();
            if (stagings != null) foreach (var staging in stagings) staging?.Dispose();
            if (materials != null) foreach (var material in materials) if (material != null) UnityEngine.Object.Destroy(material);
            geometry?.Dispose(); tints?.Dispose(); marker?.Dispose();
            if (chunks.IsCreated) chunks.Dispose();
            command.Dispose();
        }
    }
}
