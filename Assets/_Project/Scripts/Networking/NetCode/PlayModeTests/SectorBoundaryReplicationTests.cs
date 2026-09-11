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

        //The concern this whole representation exists for: a player travelling millions of blocks must
        //not lose precision. Measuring the flown radius in absolute terms would mostly measure
        //interpolation chord error, so this compares the same circle at the origin against one fifty
        //million blocks out. Geometry is identical, so any extra error out there is precision loss.
        //A float wire format resolves to four blocks at fifty million and would fail loudly.
        [UnityTest]
        public IEnumerator PrecisionDoesNotDecayFiftyMillionBlocksFromTheOrigin() => UniTask.ToCoroutine(async () =>
        {
            double nearError = await FlyCircleAndMeasureRadiusError(new int3(0, 96, 0), 182);
            double farError = await FlyCircleAndMeasureRadiusError(new int3(50_000_000, 96, 50_000_000), 183);

            //both are dominated by the same chord error, so they should be within a millimetre or two.
            Assert.That(farError, Is.LessThan(nearError + 0.05),
                $"radius error grew from {nearError:0.####} at the origin to {farError:0.####} fifty million blocks out");
        });

        private const float MeasuredRadius = 120f;

        private async UniTask<double> FlyCircleAndMeasureRadiusError(int3 centreBlock, ulong identity)
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => identity);
            var bulk = new ChunkCompanionService(session);
            var ghosts = new EntityGhostService(session, registry, allowDebugSpawns: true);
            var host = Host(server, client, session, bulk, ghosts);
            await host.StartAsync(CancellationToken.None);
            await Until(() => session.State == NetworkSessionState.InGame);

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            var centre = SectorGrid.FromBlocks(centreBlock);
            double3 centreBlocks = SectorGrid.ToBlocks(centre);
            Entity spawned = EntitySpawn.Spawn(server.World.EntityManager, prefabs,
                registry.GetId(Orbiter), centre, radius: MeasuredRadius, angularSpeed: 1.5f);
            Assert.That(spawned, Is.Not.EqualTo(Entity.Null));

            double worstRadiusError = 0, worstServerError = 0, worstWireError = 0;
            int sampled = 0;
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                if (!TryReadClientPosition(client.World, out var position, out var wire)) continue;
                //ignore the first moments, where the ghost has spawned but not yet been interpolated.
                if (++sampled < 30) continue;

                double3 offset = SectorGrid.ToBlocks(position) - centreBlocks;
                worstRadiusError = math.max(worstRadiusError,
                    math.abs(math.length(new double2(offset.x, offset.z)) - MeasuredRadius));

                //the raw replicated doubles, before reconstruction, separate wire loss from ours.
                double3 wireOffset = wire - centreBlocks;
                worstWireError = math.max(worstWireError,
                    math.abs(math.length(new double2(wireOffset.x, wireOffset.z)) - MeasuredRadius));

                //the server's own position isolates simulation error from anything the wire adds.
                server.World.EntityManager.CompleteAllTrackedJobs();
                double3 authoritative = SectorGrid.ToBlocks(server.World.EntityManager.GetComponentData<WorldPosition>(spawned)) - centreBlocks;
                worstServerError = math.max(worstServerError,
                    math.abs(math.length(new double2(authoritative.x, authoritative.z)) - MeasuredRadius));
            }

            Assert.That(sampled, Is.GreaterThan(60), "the ghost should have been replicated and sampled");
            //simulation keeps position as an exact sector plus a small offset, so its own error is
            //independent of distance. If this ever fails, the problem is the sector grid, not the wire.
            Assert.That(worstServerError, Is.LessThan(0.01),
                $"the server's own circle was off by {worstServerError:0.######} blocks at {centreBlock}");
            //the reconstruction is lossless, so anything the wire loses shows up in both.
            Assert.That(worstRadiusError, Is.EqualTo(worstWireError).Within(0.01),
                "reconstructing the position from the wire should not add error of its own");
            await host.StopAsync(CancellationToken.None);
            hosts.Remove(host);
            return worstRadiusError;
        }

        private static bool TryReadClientPosition(World world, out WorldPosition position) =>
            TryReadClientPosition(world, out position, out _);

        private static bool TryReadClientPosition(World world, out WorldPosition position, out double3 wire)
        {
            position = default; wire = default;
            if (world is not { IsCreated: true }) return false;
            //the client rebuilds WorldPosition in a job, so sampling it from the main thread has to
            //wait for that job rather than racing it.
            world.EntityManager.CompleteAllTrackedJobs();
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<WorldPosition>(), ComponentType.ReadOnly<ReplicatedPosition>());
            if (query.CalculateEntityCount() != 1) return false;
            position = query.GetSingleton<WorldPosition>();
            wire = query.GetSingleton<ReplicatedPosition>().Blocks;
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
