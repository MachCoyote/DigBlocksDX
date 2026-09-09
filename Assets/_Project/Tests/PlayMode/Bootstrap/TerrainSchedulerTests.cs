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
    }
}
