using System.Collections;
using DigBlocks.Client.Rendering;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class TerrainSchedulerTests
    {
        [UnityTest]
        public IEnumerator ReplacementNeighborsAndEpochResetRejectStaleJobs()
        {
            var content = BlockContentProvider.Load();
            var settings = Resources.Load<TerrainRenderSettings>("TerrainRenderSettings");
            using var world = new World("Terrain scheduler verification");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(content.Registry);
            store.EnableReplicas();
            using var renderer = new TerrainRenderer(content, settings);
            using var scheduler = new ChunkMeshScheduler(content, settings, renderer);
            var center = new ChunkAddress(1, int3.zero);
            var east = new ChunkAddress(1, new int3(1, 0, 0));
            uint stone = content.Registry.LookupSolid("digblocks:stone");
            ChunkImage Image(ChunkAddress address, ulong revision, uint block)
            {
                var cells = new uint[ChunkLayout.Volume];
                System.Array.Fill(cells, block);
                return new ChunkImage(address, 1, revision, cells, new uint[ChunkLayout.Volume]);
            }
            store.SetReplicaInterest(new ChunkInterest(1, center, 1, 0));
            store.PublishReplica(1, Image(center, 1, stone));
            scheduler.Tick(store, float3.zero);
            store.PublishReplica(1, Image(center, 2, 0));
            yield return Settle();
            Assert.That(scheduler.StaleResults, Is.GreaterThan(0));
            Assert.That(renderer.LiveQuads, Is.Zero);
            store.PublishReplica(1, Image(center, 3, stone));
            store.PublishReplica(1, Image(east, 1, stone));
            yield return Settle();
            Assert.That(renderer.LiveQuads, Is.EqualTo(10), "Shared chunk boundary must be hidden.");
            store.SetReplicaInterest(new ChunkInterest(2, center, 0, 0));
            store.PublishReplica(2, Image(center, 3, stone));
            yield return Settle();
            Assert.That(renderer.LiveQuads, Is.EqualTo(6), "Removing the neighbor restores the boundary.");
            store.PublishReplica(2, Image(center, 4, 0));
            scheduler.Tick(store, float3.zero);
            store.ClearReplicas();
            yield return Settle();
            Assert.That(renderer.LiveQuads, Is.Zero);
            Assert.That(scheduler.ResidentCount, Is.Zero);

            IEnumerator Settle()
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 10;
                do
                {
                    scheduler.Tick(store, float3.zero);
                    Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline));
                    yield return null;
                } while (!scheduler.IsCurrent);
            }
        }

        [UnityTest]
        public IEnumerator CompleteReplicaGraphCullsBehindBarrierAndFailsOpenDuringRebuild()
        {
            var content = BlockContentProvider.Load();
            var settings = Object.Instantiate(Resources.Load<TerrainRenderSettings>("TerrainRenderSettings"));
            Assert.That(settings.ChunkOcclusionCulling, Is.True);
            using var world = new World("Terrain occlusion verification");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(content.Registry);
            store.EnableReplicas();
            using var renderer = new TerrainRenderer(content, settings);
            using var scheduler = new ChunkMeshScheduler(content, settings, renderer);
            var anchor = new ChunkAddress(1, int3.zero);
            //radius 2 so the empty chunks flanking the camera are resident: the graph walks face
            //neighbours, and a radius-1 cylinder has no column diagonally adjacent to the camera's.
            var interest = new ChunkInterest(1, anchor, 2, 0);
            uint stone = content.Registry.LookupSolid("digblocks:stone");

            store.SetReplicaInterest(interest);
            foreach (var address in interest.Addresses())
            {
                var cells = new uint[ChunkLayout.Volume];
                //a three-wide wall at x = 0, exactly what the camera's flood fill can reach around.
                if (address.Position.x == 0 && math.abs(address.Position.z) <= 1) System.Array.Fill(cells, stone);
                else if (address.Position.Equals(new int3(1, 0, 0))) cells[ChunkLayout.Index(new int3(16, 16, 16))] = stone;
                store.PublishReplica(interest.Epoch, new ChunkImage(address, 1, 1, cells, new uint[ChunkLayout.Volume]));
            }
            yield return Settle();

            renderer.UpdateCameraVisibility(new float3(-16, 16, 16));
            Assert.That(renderer.ResidentGraphNodes, Is.EqualTo(interest.Count));
            Assert.That(renderer.CameraVisibleChunks, Is.EqualTo(3));
            Assert.That(renderer.GraphCulledChunks, Is.EqualTo(1));
            int enabledQuads = renderer.CameraVisibleQuads;
            settings.ChunkOcclusionCulling = false;
            renderer.UpdateCameraVisibility(new float3(-16, 16, 16));
            Assert.That(renderer.CameraVisibleChunks, Is.EqualTo(4));
            Assert.That(renderer.CameraVisibleQuads, Is.EqualTo(renderer.LiveQuads));
            int disabledQuads = renderer.CameraVisibleQuads;
            settings.ChunkOcclusionCulling = true;

            var opened = new uint[ChunkLayout.Volume];
            System.Array.Fill(opened, stone);
            for (int x = 0; x < ChunkLayout.Edge; x++) opened[ChunkLayout.Index(new int3(x, 16, 16))] = 0;
            store.PublishReplica(interest.Epoch, new ChunkImage(anchor, 1, 2, opened, new uint[ChunkLayout.Volume]));
            renderer.UpdateCameraVisibility(new float3(-16, 16, 16));
            Assert.That(renderer.GraphCulledChunks, Is.Zero, "Dirty graph state must fail open.");
            Assert.That(renderer.CameraVisibleChunks, Is.EqualTo(4));

            yield return Settle();
            renderer.UpdateCameraVisibility(new float3(-16, 16, 16));
            Assert.That(renderer.GraphCulledChunks, Is.Zero, "The opened tunnel reconnects the chunk behind the barrier.");
            Assert.That(renderer.CameraVisibleChunks, Is.EqualTo(4));

            store.ClearReplicas();
            renderer.UpdateCameraVisibility(new float3(-16, 16, 16));
            Assert.That(renderer.ResidentGraphNodes, Is.Zero);
            Assert.That(renderer.CameraVisibleChunks, Is.Zero);

            System.IO.Directory.CreateDirectory(".utmp");
            System.IO.File.WriteAllText(".utmp/terrain-occlusion-fixture.txt",
                $"Complete 3x3 replica fixture, camera in west chunk.\n" +
                $"enabled: resident {interest.Count}, visible chunks 3, culled chunks 1, camera-visible quads {enabledQuads}\n" +
                $"disabled: resident {interest.Count}, visible chunks 4, culled chunks 0, camera-visible quads {disabledQuads}\n" +
                "CPU graph visibility before distance/frustum/material GPU compaction; not a frame-time measurement.\n");
            Object.Destroy(settings);

            IEnumerator Settle()
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 15;
                do
                {
                    scheduler.Tick(store, new float3(-16, 16, 16));
                    Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline));
                    yield return null;
                } while (!scheduler.IsCurrent);
            }
        }
    }
}
