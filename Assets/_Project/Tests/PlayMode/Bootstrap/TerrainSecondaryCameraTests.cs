using System.Collections;
using DigBlocks.ChunkProtocol;
using DigBlocks.Client.Rendering;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    //terrain is submitted per camera, so a second viewport only shows anything if it was named on the draw
    public sealed class TerrainSecondaryCameraTests
    {
        private static readonly int FullbrightId = Shader.PropertyToID("_DigBlocksFullbright");

        [UnityTest]
        public IEnumerator SecondCameraDrawsItsOwnViewAndShowsNothingExtraWhileMirroringTheMainCamera()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("Secondary viewport pixels need a graphics device.");

            var content = BlockContentProvider.Load();
            var settings = Object.Instantiate(Resources.Load<TerrainRenderSettings>("TerrainRenderSettings"));
            using var world = new World("Terrain secondary camera verification");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(content.Registry);
            store.EnableReplicas();
            var grid = new ChunkSlotGrid(12, 4);
            using var renderer = new TerrainRenderer(content, settings, grid);
            using var scheduler = new ChunkMeshScheduler(content, settings, renderer, grid);

            //unlit albedo makes "geometry reached this camera" a colour test rather than a lighting test
            Shader.SetGlobalFloat(FullbrightId, 1f);

            var main = Open("main", new Vector3(16, 16, -48), new Vector3(16, 16, 16));
            var second = Open("second", new Vector3(16, 16, -48), new Vector3(16, 16, 16));
            try
            {
                uint stone = content.Registry.LookupSolid("digblocks:stone");
                var cells = new uint[ChunkLayout.Volume];
                System.Array.Fill(cells, stone);
                store.SetReplicaInterest(new ChunkInterest(1, new ChunkAddress(1, int3.zero), 0, 0));
                store.PublishReplica(1, new ChunkImage(new ChunkAddress(1, int3.zero), 1, 1, cells, new uint[ChunkLayout.Volume]));

                double deadline = Time.realtimeSinceStartupAsDouble + 10;
                do
                {
                    scheduler.Tick(store, float3.zero);
                    Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "Meshing never settled.");
                    yield return null;
                } while (!scheduler.IsCurrent);
                Assert.That(renderer.LiveQuads, Is.GreaterThan(0));

                renderer.SetSecondaryCameraCulling(TerrainSecondaryCameraCulling.OwnView);
                yield return Frames(renderer, main.Camera);
                Assert.That(Painted(main), Is.GreaterThan(0), "The camera the session drives has to draw the chunk in front of it.");
                Assert.That(Painted(second), Is.GreaterThan(0), "A second camera culling for itself has to show the terrain in front of it.");

                //once the main camera turns away its frustum pass rejects the only chunk, so a mirrored
                //viewport inherits an empty visible set while one culling for itself keeps the terrain
                main.Camera.transform.LookAt(new Vector3(16, 16, -400));
                yield return Frames(renderer, main.Camera);
                Assert.That(Painted(main), Is.Zero, "The main camera is facing away from the only chunk.");
                Assert.That(Painted(second), Is.GreaterThan(0), "A second camera culls for its own view, not the main camera's.");

                renderer.SetSecondaryCameraCulling(TerrainSecondaryCameraCulling.MirrorMain);
                yield return Frames(renderer, main.Camera);
                Assert.That(Painted(second), Is.Zero, "Mirroring hands the second camera the main camera's visible set, which is empty here.");

                renderer.SetSecondaryCameraCulling(TerrainSecondaryCameraCulling.OwnView);
                renderer.SecondaryCameraRendering = false;
                yield return Frames(renderer, main.Camera);
                Assert.That(Painted(second), Is.Zero, "Secondary viewports are switched off wholesale.");
            }
            finally
            {
                Shader.SetGlobalFloat(FullbrightId, 0f);
                main.Dispose(); second.Dispose();
            }
        }

        private static IEnumerator Frames(TerrainRenderer renderer, Camera primary)
        {
            //the draw has to be submitted during a frame's update phase to be picked up by that frame's
            //rendering, and the ring needs a couple of frames to leave the previous mode behind
            for (int i = 0; i < 3; i++)
            {
                yield return null;
                renderer.Draw(primary);
                yield return new WaitForEndOfFrame();
            }
        }

        private static Viewport Open(string name, Vector3 position, Vector3 target)
        {
            var host = new GameObject(name);
            host.transform.position = position;
            host.transform.LookAt(target);
            var camera = host.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1000;
            camera.targetTexture = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            return new Viewport { Host = host, Camera = camera, Pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false) };
        }

        //how much of the viewport stopped being the clear colour, which is only terrain in an empty scene
        private static int Painted(Viewport viewport)
        {
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = viewport.Camera.targetTexture;
                viewport.Pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                viewport.Pixels.Apply();
            }
            finally { RenderTexture.active = previous; }

            int painted = 0;
            foreach (var color in viewport.Pixels.GetPixels32())
                if (color.r > 8 || color.g > 8 || color.b > 8) painted++;
            return painted;
        }

        private struct Viewport
        {
            public GameObject Host;
            public Camera Camera;
            public Texture2D Pixels;
            public void Dispose()
            {
                var target = Camera != null ? Camera.targetTexture : null;
                if (Camera != null) Camera.targetTexture = null;
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                if (Pixels != null) Object.DestroyImmediate(Pixels);
                if (Host != null) Object.DestroyImmediate(Host);
            }
        }
    }
}
