using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Server.Runtime;
using DigBlocks.Simulation;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //A position split into an exact sector and an offset inside it cannot be replicated as two ghost
    //fields: NetCode interpolates each independently, so the offset would be lerped across the wrap
    //while the sector snapped, and every sector crossing would throw the entity a full sector's width
    //and back inside one tick. ReplicatedPosition pins an origin at spawn so the interpolated value
    //is continuous everywhere. This test flies a mob straight through a sector boundary and watches.
    public sealed class SectorBoundaryReplicationTests
    {
        private readonly List<GameHost> hosts = new();
        private readonly NullLogger logger = new();

        private const string Orbiter = "digblocks:orbiter";

        private static EntityTypeRegistry Registry() => new EntityTypeRegistry(new[]
        {
            new EntityTypeDefinition(Orbiter, "digblocks:models/cube",
                new[] { SimulationBehaviors.CircleFlight },
                new EntityTypeAttributes(EntityFlags.None, EntityCategory.Marker, 0.6f, 0.6f,
                    EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1))
        });

        [UnityTest]
        public IEnumerator AGhostCrossingASectorBoundaryMovesContinuously() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => 180);
            var bulk = new ChunkCompanionService(session);
            var ghosts = new EntityGhostService(session, registry, allowDebugSpawns: true);
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);
            await Until(() => session.State == NetworkSessionState.InGame);

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //centre the circle exactly on a sector boundary so the mob crosses it twice per lap, and
            //fly it fast enough that a lap completes well inside the sampling window.
            var centre = SectorGrid.FromBlocks(new int3(SectorGrid.SectorEdge, 96, 0));
            Assert.That(centre.Sector.x, Is.EqualTo(1), "the centre should sit on the first sector boundary");
            Entity spawned = EntitySpawn.Spawn(server.World.EntityManager, prefabs,
                registry.GetId(Orbiter), centre, radius: 120f, angularSpeed: 4f);
            Assert.That(spawned, Is.Not.EqualTo(Entity.Null));

            //sample what the client actually renders from, which is the reconstructed world position.
            var samples = new List<double3>();
            double deadline = Time.realtimeSinceStartupAsDouble + 6;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                if (TryReadClientPosition(client.World, out var position)) samples.Add(SectorGrid.ToBlocks(position));
            }

            Assert.That(samples.Count, Is.GreaterThan(60), "the ghost should have been replicated and sampled");

            double minX = double.MaxValue, maxX = double.MinValue, worstStep = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                minX = math.min(minX, samples[i].x);
                maxX = math.max(maxX, samples[i].x);
                if (i > 0) worstStep = math.max(worstStep, math.distance(samples[i], samples[i - 1]));
            }

            //the run is only meaningful if the mob actually crossed the boundary it was aimed at.
            Assert.That(minX, Is.LessThan(SectorGrid.SectorEdge), "the mob should have been seen inside sector 0");
            Assert.That(maxX, Is.GreaterThan(SectorGrid.SectorEdge), "the mob should have been seen inside sector 1");

            //a torn reconstruction jumps by about one sector edge. Real motion is at most a few units
            //per frame at this speed, so anything near the sector edge is the bug and this is decisive.
            Assert.That(worstStep, Is.LessThan(SectorGrid.SectorEdge / 8.0),
                $"position jumped {worstStep:0.##} blocks between frames; a sector boundary tear looks like {SectorGrid.SectorEdge}");
        });

        private static bool TryReadClientPosition(World world, out WorldPosition position)
        {
            position = default;
            if (world is not { IsCreated: true }) return false;
            //the client rebuilds WorldPosition in a job, so sampling it from the main thread has to
            //wait for that job rather than racing it.
            world.EntityManager.CompleteAllTrackedJobs();
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<WorldPosition>(), ComponentType.ReadOnly<ReplicatedPosition>());
            if (query.CalculateEntityCount() != 1) return false;
            position = query.GetSingleton<WorldPosition>();
            return true;
        }

        private GameHost Host(params IGameService[] services)
        { var host = new GameHost(services, logger); hosts.Add(host); return host; }

        private static async UniTask Until(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
            Assert.That(condition(), Is.True);
        }

        [UnityTearDown]
        public IEnumerator Cleanup() => UniTask.ToCoroutine(async () =>
        { for (int i = hosts.Count - 1; i >= 0; i--) await hosts[i].StopAsync(CancellationToken.None); hosts.Clear(); });

        private sealed class NullLogger : IGameLogger
        {
            public IGameLogger CreateFor(string sourceName) => this;
            public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null) { }
        }
    }
}
