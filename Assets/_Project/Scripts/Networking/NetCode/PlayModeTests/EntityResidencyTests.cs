using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Server.Runtime;
using DigBlocks.Simulation;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //Entities load and unload with the chunks they live in, and are replicated only to peers whose
    //chunk interest covers them. Both derive from the same per-peer interest the chunk companion
    //owns, which is what stops entities and chunks from disagreeing about what is loaded and visible.
    public sealed class EntityResidencyTests
    {
        private readonly List<GameHost> hosts = new();
        private readonly NullLogger logger = new();

        private const string Orbiter = "digblocks:orbiter";

        private static EntityTypeRegistry Registry() => new EntityTypeRegistry(new[]
        {
            new EntityTypeDefinition(Orbiter, "digblocks:models/cube",
                new[] { SimulationBehaviors.CircleFlight },
                new EntityTypeAttributes(EntityFlags.Persists, EntityCategory.Marker, 0.6f, 0.6f,
                    EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1))
        });

        //A chunk leaving the resident set takes its entities with it, and bringing it back restores
        //them. The stored record and the live entity are never both authoritative: the handoff is
        //what makes this safe to back with a file later.
        [UnityTest]
        public IEnumerator EntitiesUnloadWithTheirChunkAndComeBackWithIt() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => 190);
            var bulk = new ChunkCompanionService(session);
            var store = new InMemoryEntityChunkStore();
            var ghosts = new EntityGhostService(session, registry, bulk, allowDebugSpawns: true, chunkStore: store);
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);
            await Until(() => session.State == NetworkSessionState.InGame);

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //spawn inside the peer's interest, which is anchored at the origin by default.
            var home = SectorGrid.FromBlocks(new int3(4, 20, 4));
            Entity spawned = EntitySpawn.Spawn(server.World.EntityManager, prefabs,
                registry.GetId(Orbiter), home, radius: 2f, angularSpeed: 1f);
            Assert.That(spawned, Is.Not.EqualTo(Entity.Null));
            await Until(() => ServerEntityCount(server.World) == 1);
            await Until(() => ClientGhostCount(client.World) == 1);
            Assert.That(store.StoredChunkCount, Is.Zero, "nothing should be stored while the chunk is loaded");

            //move the peer's interest far away, so the chunk the mob stands in leaves the union.
            Assert.That(bulk.SetServerInterest(session.LocalPeerId, new ChunkAddress(1, new int3(600, 0, 600)), 0, 0), Is.True);
            await Until(() => ServerEntityCount(server.World) == 0);
            Assert.That(store.StoredChunkCount, Is.EqualTo(1), "the mob should have been handed to the chunk store");
            await Until(() => ClientGhostCount(client.World) == 0);

            //bring it back and the mob returns, still flying the circle it was flying.
            Assert.That(bulk.SetServerInterest(session.LocalPeerId, new ChunkAddress(1, int3.zero), 1, 0), Is.True);
            await Until(() => ServerEntityCount(server.World) == 1);
            Assert.That(store.StoredChunkCount, Is.Zero, "taking the chunk back should empty the store");

            var restored = SingleServerEntity(server.World);
            Assert.That(server.World.EntityManager.HasComponent<CircleFlight>(restored), Is.True);
            var flight = server.World.EntityManager.GetComponentData<CircleFlight>(restored);
            Assert.That(flight.Radius, Is.EqualTo(2f).Within(1e-3f), "behaviour state should survive the round trip");
            await Until(() => ClientGhostCount(client.World) == 1);
        });

        //With two peers looking at different parts of the world, a mob standing in one peer's chunks
        //must not be replicated to the other. This is what makes mob count scale with the world
        //rather than with the number of clients.
        [UnityTest]
        public IEnumerator AMobIsReplicatedOnlyToPeersWhoseInterestCoversIt() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var serverRuntime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var serverSession = new NetCodeSession(NetworkSessionRole.Server,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion, bindAddress: "127.0.0.1"),
                null, () => serverRuntime.World, logger);
            var serverBulk = new ChunkCompanionService(serverSession, 0);
            var serverGhosts = new EntityGhostService(serverSession, registry, serverBulk);
            await Host(serverRuntime, serverSession, serverBulk, serverGhosts).StartAsync(CancellationToken.None);

            var near = await JoinClient(serverSession.ListeningPort, 191, registry);
            var far = await JoinClient(serverSession.ListeningPort, 192, registry);
            await Until(() => near.Bulk.ClientDataReady && far.Bulk.ClientDataReady);
            await Until(() => near.Session.State == NetworkSessionState.InGame && far.Session.State == NetworkSessionState.InGame);

            //push the two peers apart so their interests are disjoint.
            var farAnchor = new ChunkAddress(1, new int3(500, 0, 500));
            Assert.That(serverBulk.SetServerInterest(far.Session.LocalPeerId, farAnchor, 0, 0), Is.True);
            await Until(() => far.Bulk.ClientDataReady);

            var prefabs = serverRuntime.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);
            Entity spawned = EntitySpawn.Spawn(serverRuntime.World.EntityManager, prefabs,
                registry.GetId(Orbiter), SectorGrid.FromBlocks(new int3(4, 20, 4)), radius: 2f, angularSpeed: 1f);
            Assert.That(spawned, Is.Not.EqualTo(Entity.Null));

            await Until(() => ClientGhostCount(near.Session.ClientWorld) == 1);
            //give the far peer ample time to be sent something it should never receive.
            double deadline = Time.realtimeSinceStartupAsDouble + 2;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                Assert.That(ClientGhostCount(far.Session.ClientWorld), Is.Zero,
                    "a mob outside a peer's chunk interest must not be replicated to it");
            }
        });

        //Simulation distance is deliberately not render distance. A player can see much further than
        //the world should be alive, so a mob well inside the streamed chunks but beyond simulation
        //distance must not be kept alive or replicated.
        [UnityTest]
        public IEnumerator EntitiesBeyondSimulationDistanceAreNotKeptAlive() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => 193);
            //stream a wide volume but only simulate the chunk the player stands in.
            var streaming = new ChunkStreamingOptions(horizontalRadius: 3, verticalRadius: 1);
            var bulk = new ChunkCompanionService(session, streamingOptions: streaming);
            var store = new InMemoryEntityChunkStore();
            var ghosts = new EntityGhostService(session, registry, bulk, allowDebugSpawns: true, chunkStore: store,
                simulationDistance: new EntitySimulationDistance(0, 0));
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);
            await Until(() => session.State == NetworkSessionState.InGame);

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //two chunks out: comfortably inside the streamed radius of three, outside simulation.
            var distant = SectorGrid.FromBlocks(new int3(2 * ChunkLayout.Edge + 4, 20, 4));
            var distantChunk = SectorGrid.ChunkOf(1, distant);
            Assert.That(bulk.ClientDataReady, Is.True);
            Assert.That(EntitySpawn.Spawn(server.World.EntityManager, prefabs, registry.GetId(Orbiter), distant),
                Is.Not.EqualTo(Entity.Null));

            //it is streamed terrain but not simulated space, so the mob is put away rather than kept.
            await Until(() => store.StoredChunkCount == 1);
            Assert.That(ServerEntityCount(server.World), Is.Zero);
            Assert.That(distantChunk.Position.x, Is.EqualTo(2), "the spawn should be two chunks out");

            //inside simulation distance it stays alive and reaches the client.
            var home = SectorGrid.FromBlocks(new int3(4, 20, 4));
            Assert.That(EntitySpawn.Spawn(server.World.EntityManager, prefabs, registry.GetId(Orbiter), home),
                Is.Not.EqualTo(Entity.Null));
            await Until(() => ServerEntityCount(server.World) == 1);
            await Until(() => ClientGhostCount(client.World) == 1);
        });

        private readonly struct Joined
        {
            public readonly NetCodeSession Session;
            public readonly ChunkCompanionService Bulk;
            public Joined(NetCodeSession session, ChunkCompanionService bulk) { Session = session; Bulk = bulk; }
        }

        private async UniTask<Joined> JoinClient(ushort port, ulong identity, EntityTypeRegistry registry)
        {
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client,
                new NetworkSessionOptions("127.0.0.1", port, NetworkSessionOptions.CurrentProtocolVersion),
                () => runtime.World, null, logger, () => identity);
            var bulk = new ChunkCompanionService(session);
            var ghosts = new EntityGhostService(session, registry, bulk);
            await Host(runtime, session, bulk, ghosts).StartAsync(CancellationToken.None);
            return new Joined(session, bulk);
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
