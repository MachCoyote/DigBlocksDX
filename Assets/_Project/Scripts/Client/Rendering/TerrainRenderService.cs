using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Session;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Runtime;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace DigBlocks.Client.Rendering
{
    public sealed class TerrainRenderService : IGameService, ISessionReadinessSource
    {
        private readonly Func<World> getWorld;
        private readonly Func<bool> inputAvailable;
        private readonly CompiledBlockContent content;
        private readonly TerrainRenderSettings settings;
        private GameObject root;
        private TerrainView view;
        private TerrainMeshingSystem system;
        private TerrainRenderer renderer;
        private ChunkMeshScheduler scheduler;
        private Camera previousCamera;
        private bool previousCameraEnabled;
        private Exception failure;
        public string Name => nameof(TerrainRenderService);
        public string ReadinessDescription => "Building chunk meshes...";
        public int BuiltChunks => scheduler?.BuiltCount ?? 0;
        public int Quads => renderer?.LiveQuads ?? 0;

        public TerrainRenderService(Func<World> getWorld, CompiledBlockContent content, TerrainRenderSettings settings, Func<bool> inputAvailable)
        { this.getWorld = getWorld; this.content = content; this.settings = settings; this.inputAvailable = inputAvailable; }

        public async UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var world = getWorld();
                if (world == null || !world.IsCreated) throw new InvalidOperationException("Terrain requires the client world.");
                renderer = new TerrainRenderer(content, settings);
                scheduler = new ChunkMeshScheduler(content, settings, renderer);
                root = new GameObject("Terrain presentation");
                UnityEngine.Object.DontDestroyOnLoad(root);
                previousCamera = Camera.main;
                previousCameraEnabled = previousCamera != null && previousCamera.enabled;
                if (previousCamera != null) previousCamera.enabled = false;
                var camera = root.AddComponent<Camera>();
                camera.tag = "MainCamera";
                camera.nearClipPlane = 0.1f; camera.farClipPlane = settings.RenderDistance + 64;
                camera.backgroundColor = new Color(0.45f, 0.67f, 0.9f);
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                camera.transform.position = new Vector3(62, 48, -62);
                camera.transform.LookAt(new Vector3(8, 6, 8));
                view = root.AddComponent<TerrainView>();
                view.Initialize(camera, renderer, inputAvailable);
                system = world.GetExistingSystemManaged<TerrainMeshingSystem>();
                if (system == null)
                {
                    system = world.GetOrCreateSystemManaged<TerrainMeshingSystem>();
                    world.GetOrCreateSystemManaged<SimulationSystemGroup>().AddSystemToUpdateList(system);
                }
                system.Tick = Tick;
            }
            catch
            {
                await StopAsync(CancellationToken.None);
                throw;
            }
        }

        private void Tick()
        {
            if (failure != null || view == null) return;
            try
            {
                var world = getWorld();
                scheduler.Tick(world is { IsCreated: true } ? world.GetExistingSystemManaged<ChunkWorldSystem>()?.Store : null, view.transform.position);
            }
            catch (Exception error) { failure = error; Debug.LogException(error); }
        }

        public async UniTask WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            double start = Time.realtimeSinceStartupAsDouble;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failure != null) throw new InvalidOperationException("Terrain meshing failed.", failure);
                var store = getWorld()?.GetExistingSystemManaged<ChunkWorldSystem>()?.Store;
                if (store != null && !store.IsDisposed && store.DataReady && scheduler.BuiltCount == store.Count && scheduler.IsCurrent) return;
                if (Time.realtimeSinceStartupAsDouble - start > 60) throw new TimeoutException("Terrain did not reach visual readiness.");
                await UniTask.Yield(cancellationToken);
            }
        }

        public async UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (system != null) system.Tick = null;
            if (view != null) view.enabled = false;
            scheduler?.Dispose(); scheduler = null;
            //allow queued indirect submissions to reach the pipeline before releasing their buffers.
            if (root != null && Application.isPlaying) await UniTask.WaitForEndOfFrame();
            renderer?.Dispose(); renderer = null;
            if (previousCamera != null) previousCamera.enabled = previousCameraEnabled;
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null; view = null;
        }
    }

    public sealed class TerrainView : MonoBehaviour
    {
        private Camera camera;
        private TerrainRenderer renderer;
        private Func<bool> inputAvailable;
        private float pitch, yaw;
        public void Initialize(Camera camera, TerrainRenderer renderer, Func<bool> inputAvailable)
        {
            this.camera = camera; this.renderer = renderer; this.inputAvailable = inputAvailable;
            pitch = transform.eulerAngles.x; yaw = transform.eulerAngles.y;
        }
        private void LateUpdate()
        {
            if (renderer == null) return;
            if (inputAvailable?.Invoke() == true && Keyboard.current != null)
            {
                var keyboard = Keyboard.current;
                var look = Mouse.current?.delta.ReadValue() ?? Vector2.zero;
                yaw += look.x * 0.12f; pitch = Mathf.Clamp(pitch - look.y * 0.12f, -89, 89);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
                Vector3 direction = new Vector3((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0),
                    (keyboard.spaceKey.isPressed ? 1 : 0) - (keyboard.leftCtrlKey.isPressed ? 1 : 0),
                    (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0));
                Vector3 world = transform.right * direction.x + Vector3.up * direction.y + transform.forward * direction.z;
                transform.position += Vector3.ClampMagnitude(world, 1) * ((keyboard.leftShiftKey.isPressed ? 72 : 24) * Time.unscaledDeltaTime);
            }
            renderer.Draw(camera);
        }
    }
}
