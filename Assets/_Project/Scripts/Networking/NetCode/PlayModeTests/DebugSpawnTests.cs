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
using Unity.NetCode;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //The debug spawn RPC is the path the menu key actually takes. Everything else exercised
    //EntitySpawn directly from test code, which runs outside any query and so never noticed that
    //spawning from inside the request loop is a structural change during iteration.
    public sealed class DebugSpawnTests
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

        //A logged exception fails a Unity test, so driving the real path is what catches the
        //structural-change error rather than any assertion written here.
        [UnityTest]
        public IEnumerator ASpawnRequestFromTheClientReachesTheWorld() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(194, permitSpawns: true);

            Assert.That(DebugSpawnRequest.Send(context.Client.World, Orbiter,
                SectorGrid.FromBlocks(new int3(6, 24, 6)), radius: 4f, angularSpeed: 1.5f), Is.True);

            await Until(() => ServerEntityCount(context.Server.World) == 1);
            await Until(() => ClientGhostCount(context.Client.World) == 1);

            //the behaviour named by the entity type should have been attached by the spawn, so the
            //mob is actually flying rather than sitting where it appeared.
            var spawned = SingleServerEntity(context.Server.World);
            Assert.That(context.Server.World.EntityManager.HasComponent<CircleFlight>(spawned), Is.True);
            Assert.That(context.Server.World.EntityManager.GetComponentData<CircleFlight>(spawned).Radius,
                Is.EqualTo(4f).Within(1e-3f));
        });

        //Several requests in one tick is the case that made the structural change unavoidable rather
        //than incidental: the loop has to finish before any of them can spawn.
        [UnityTest]
        public IEnumerator SeveralSpawnRequestsInOneTickAllArrive() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(195, permitSpawns: true);

            for (int i = 0; i < 4; i++)
                Assert.That(DebugSpawnRequest.Send(context.Client.World, Orbiter,
                    SectorGrid.FromBlocks(new int3(4 + i, 24, 4))), Is.True);

            await Until(() => ServerEntityCount(context.Server.World) == 4);
            await Until(() => ClientGhostCount(context.Client.World) == 4);
        });

        //The gate is on the server, not on whether a client shows a debug menu.
        [UnityTest]
        public IEnumerator ASessionThatDoesNotPermitDebugSpawnsRefusesThem() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(196, permitSpawns: false);

            Assert.That(DebugSpawnRequest.Send(context.Client.World, Orbiter,
                SectorGrid.FromBlocks(new int3(6, 24, 6))), Is.True);

            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                Assert.That(ServerEntityCount(context.Server.World), Is.Zero,
                    "a session that does not permit debug spawns must not honour one");
            }
        });

        //An unknown type must be refused rather than reaching the prefab lookup and failing there.
        [UnityTest]
        public IEnumerator AnUnknownEntityTypeIsRefused() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(197, permitSpawns: true);

            Assert.That(DebugSpawnRequest.Send(context.Client.World, "digblocks:not_a_real_type",
                SectorGrid.FromBlocks(new int3(6, 24, 6))), Is.True);

            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                Assert.That(ServerEntityCount(context.Server.World), Is.Zero);
            }
        });

        private readonly struct SessionContext
        {
            public readonly ServerRuntime Server;
            public readonly ClientRuntime Client;
            public SessionContext(ServerRuntime server, ClientRuntime client) { Server = server; Client = client; }
        }

        private async UniTask<SessionContext> StartSession(ulong identity, bool permitSpawns)
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => identity);
            var bulk = new ChunkCompanionService(session);
            var ghosts = new EntityGhostService(session, registry, bulk, allowDebugSpawns: permitSpawns);
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);

            await Until(() => session.State == NetworkSessionState.InGame);
            await Until(() => server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>().Built);
            return new SessionContext(server, client);
        }

        private static int ServerEntityCount(World world)
        {
            if (world is not { IsCreated: true }) return -1;
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityTypeId>(), ComponentType.ReadOnly<ChunkResidency>());
            return query.CalculateEntityCount();
        }

        private static Entity SingleServerEntity(World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityTypeId>(), ComponentType.ReadOnly<ChunkResidency>());
            return query.GetSingletonEntity();
        }

        private static int ClientGhostCount(World world)
        {
            if (world is not { IsCreated: true }) return -1;
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityTypeId>(), ComponentType.ReadOnly<GhostInstance>());
            return query.CalculateEntityCount();
        }

        private GameHost Host(params IGameService[] services)
        { var host = new GameHost(services, logger); hosts.Add(host); return host; }

        private static async UniTask Until(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
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
