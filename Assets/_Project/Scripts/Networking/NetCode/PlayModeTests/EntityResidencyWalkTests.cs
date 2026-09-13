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
    //A player walks. Every other residency test moves interest in one jump, with one entity standing
    //still, which is the one shape that cannot go wrong: a single chunk leaves the set and the same
    //single chunk comes back. Walking an anchor out and back past several moving mobs is what the
    //game actually does, and is where entities go missing.
    public sealed class EntityResidencyWalkTests
    {
        private readonly List<GameHost> hosts = new();
        private readonly NullLogger logger = new();

        private const string Orbiter = "digblocks:orbiter";
        private const int MobCount = 10;
        private const int StreamRadius = 3;
        private const int SimulateRadius = 2;

        private static EntityTypeRegistry Registry() => new EntityTypeRegistry(new[]
        {
            new EntityTypeDefinition(Orbiter, "digblocks:models/cube",
                new[] { SimulationBehaviors.CircleFlight },
                new EntityTypeAttributes(EntityFlags.Persists, EntityCategory.Marker, 0.6f, 0.6f,
                    EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1))
        });

        //Out and back, one chunk at a time, with every mob flying a circle wide enough to cross chunk
        //boundaries while it does. None of them may be lost on the way.
        [UnityTest]
        public IEnumerator EveryMobSurvivesTheAnchorWalkingOutAndBack() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(198);
            var server = context.Server;
            var client = context.Client;
            var store = context.Store;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            ushort type = prefabs.Registry.GetId(Orbiter);
            for (int i = 0; i < MobCount; i++)
                Assert.That(EntitySpawn.Spawn(server.World.EntityManager, prefabs, type,
                        SectorGrid.FromBlocks(new int3(6 + i * 2, 40, 6 + i * 2)),
                        radius: 8f, angularSpeed: 1.1f + i * 0.13f),
                    Is.Not.EqualTo(Entity.Null));

            await Until(() => ServerEntityCount(server.World) == MobCount);
            await Until(() => ClientGhostCount(client.World) == MobCount);

            //walk out until the mobs are well beyond simulation distance, then walk back to where
            //they were spawned. One chunk per step, several ticks apart, so the mobs keep flying.
            for (int x = 1; x <= 8; x++) await MoveAnchorTo(context, x);
            await Until(() => ServerEntityCount(server.World) == 0);
            Assert.That(store.StoredEntityCount, Is.EqualTo((long)MobCount), "every mob should be in the chunk store");

            for (int x = 7; x >= 0; x--) await MoveAnchorTo(context, x);

            await Until(() => ServerEntityCount(server.World) == MobCount);
            Assert.That(store.StoredEntityCount, Is.Zero, "no mob should still be put away where the player is standing");
            await Until(() => ClientGhostCount(client.World) == MobCount);
        });

        //The same trip taken repeatedly and quickly, which is what a player flying out and back does.
        //One step per tick leaves interest changing faster than a mob crosses a chunk, so a chunk can
        //leave and rejoin the simulated set between two looks at any given entity.
        [UnityTest]
        public IEnumerator NoMobIsLostOverRepeatedTrips() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(199);
            var server = context.Server;
            var client = context.Client;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            ushort type = prefabs.Registry.GetId(Orbiter);
            for (int i = 0; i < MobCount; i++)
                EntitySpawn.Spawn(server.World.EntityManager, prefabs, type,
                    SectorGrid.FromBlocks(new int3(6 + i * 2, 40, 6 + i * 2)), radius: 8f, angularSpeed: 1.1f + i * 0.13f);
            await Until(() => ServerEntityCount(server.World) == MobCount);

            for (int trip = 0; trip < 4; trip++)
            {
                for (int step = 1; step <= 8; step++) await MoveAnchorTo(context, step, step, frames: 1);
                await Until(() => ServerEntityCount(server.World) == 0);

                for (int step = 7; step >= 0; step--) await MoveAnchorTo(context, step, step, frames: 1);
                await Until(() => ServerEntityCount(server.World) == MobCount);
                Assert.That(server.World.EntityManager.CreateEntityQuery(typeof(EntityTypeId)).CalculateEntityCount(),
                    Is.EqualTo(MobCount), $"trip {trip} lost a mob");
            }

            await Until(() => ClientGhostCount(client.World) == MobCount);
        });

        //Residency decides which chunk an entity is filed under when it is put away, and therefore
        //which chunk it is looked for in when it comes back. A moving entity whose residency lags its
        //position by a tick is filed under a chunk it is not in.
        [UnityTest]
        public IEnumerator AMovingMobIsFiledUnderTheChunkItIsActuallyIn() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(200);
            var server = context.Server;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //a wide circle about a chunk corner, so the mob crosses a boundary several times a second
            //while staying well inside the simulated volume.
            EntitySpawn.Spawn(server.World.EntityManager, prefabs, prefabs.Registry.GetId(Orbiter),
                SectorGrid.FromBlocks(new int3(ChunkLayout.Edge, 40, 0)), radius: 20f, angularSpeed: 6f);
            await Until(() => ServerEntityCount(server.World) == 1);

            int crossings = 0;
            ChunkAddress previous = default;
            double deadline = Time.realtimeSinceStartupAsDouble + 4;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                using var query = server.World.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<ChunkResidency>(), ComponentType.ReadOnly<WorldPosition>());
                if (query.CalculateEntityCount() != 1) continue;
                Entity mob = query.GetSingletonEntity();
                var position = server.World.EntityManager.GetComponentData<WorldPosition>(mob);
                var residency = server.World.EntityManager.GetComponentData<ChunkResidency>(mob).Address;
                if (!residency.Equals(previous)) { crossings++; previous = residency; }
                Assert.That(residency, Is.EqualTo(SectorGrid.ChunkOf(1, position)),
                    "an entity is filed under the chunk it was in, not the chunk it is in");
            }

            Assert.That(crossings, Is.GreaterThan(2), "the mob should have crossed several chunk boundaries");
        });

        //A player does not retrace their steps. Wandering the anchor around the mobs and coming back
        //to where they were spawned is the same trip the report describes, and reaches chunk sets a
        //straight line out and back never does.
        [UnityTest]
        public IEnumerator NoMobIsLostOverAWanderingTrip() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(201);
            var server = context.Server;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //what the debug menu actually spawns: a circle of radius six, a little way in front of
            //wherever the player was standing.
            ushort type = prefabs.Registry.GetId(Orbiter);
            for (int i = 0; i < MobCount; i++)
                EntitySpawn.Spawn(server.World.EntityManager, prefabs, type,
                    SectorGrid.FromBlocks(new int3(12 + i * 4, 40, 12 + (i % 3) * 8)), radius: 6f, angularSpeed: 1.2f);
            await Until(() => ServerEntityCount(server.World) == MobCount);

            var random = new Unity.Mathematics.Random(0x5f3759df);
            int x = 0, z = 0;
            for (int step = 0; step < 60; step++)
            {
                x = math.clamp(x + random.NextInt(-1, 2), -5, 5);
                z = math.clamp(z + random.NextInt(-1, 2), -5, 5);
                await MoveAnchorTo(context, x, z, frames: random.NextInt(1, 4));
            }

            //back to where they were spawned, one chunk at a time.
            while (x != 0 || z != 0)
            {
                x -= math.clamp(x, -1, 1);
                z -= math.clamp(z, -1, 1);
                await MoveAnchorTo(context, x, z, frames: 2);
            }

            await Until(() => ServerEntityCount(server.World) == MobCount);
            Assert.That(context.Store.StoredEntityCount, Is.Zero,
                "a mob put away where the player is standing has nothing to bring it back");
        });

        //The plainest statement of what must never happen: a mob inside the simulated volume, with the
        //player standing still, disappearing. Nothing brings a stored entity back until interest
        //changes, so a mob put away by mistake while the player stands still is gone for good.
        [UnityTest]
        public IEnumerator AMobInsideTheSimulatedVolumeIsNeverPutAway() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(202);
            var server = context.Server;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            await MoveAnchorTo(context, 0, 0, frames: 2);

            //a circle about a chunk corner: well inside a simulated radius of two chunks, but crossing
            //chunk boundaries several times a second.
            EntitySpawn.Spawn(server.World.EntityManager, prefabs, prefabs.Registry.GetId(Orbiter),
                SectorGrid.FromBlocks(new int3(ChunkLayout.Edge, 40, 0)), radius: 20f, angularSpeed: 6f);
            await Until(() => ServerEntityCount(server.World) == 1);

            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                await UniTask.Yield();
                Assert.That(context.Store.StoredEntityCount, Is.Zero,
                    "a mob well inside simulation distance was put away");
                Assert.That(ServerEntityCount(server.World), Is.EqualTo(1));
            }
        });

        //The consequence of a stale residency, stated as what it costs: an entity put away under the
        //chunk it was in a tick ago is looked for in a chunk it is not in, so bringing that chunk back
        //does not bring the entity back, and the player has to walk somewhere else to find it.
        [UnityTest]
        public IEnumerator EveryStoredMobIsFiledUnderTheChunkItIsIn() => UniTask.ToCoroutine(async () =>
        {
            var context = await StartSession(203);
            var server = context.Server;

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);

            //fast wide circles, so a mob is usually somewhere near a chunk boundary when it is put away.
            ushort type = prefabs.Registry.GetId(Orbiter);
            for (int i = 0; i < MobCount; i++)
                EntitySpawn.Spawn(server.World.EntityManager, prefabs, type,
                    SectorGrid.FromBlocks(new int3(8 + i * 3, 40, 8 + i * 3)), radius: 24f, angularSpeed: 5f + i * 0.4f);
            await Until(() => ServerEntityCount(server.World) == MobCount);

            //walk out past simulation distance so every mob is put away, checking the filing as they go.
            for (int x = 1; x <= 8; x++)
            {
                await MoveAnchorTo(context, x, 0, frames: 2);
                AssertFilingIsHonest(context.Store);
            }

            await Until(() => ServerEntityCount(server.World) == 0);
            AssertFilingIsHonest(context.Store);
            Assert.That(context.Store.StoredEntityCount, Is.EqualTo((long)MobCount));
        });

        private static void AssertFilingIsHonest(InMemoryEntityChunkStore store)
        {
            foreach (var address in store.StoredChunks)
            {
                if (!store.TryPeek(address, out var stored)) continue;
                for (int i = 0; i < stored.Count; i++)
                    Assert.That(SectorGrid.ChunkOf(address.World, stored[i].Position), Is.EqualTo(address),
                        "a mob was filed under a chunk it is not in, so the chunk that would bring it back is not the one holding it");
            }
        }

        //Residency is the union of what peers are interested in, and an empty union should mean
        //nothing is alive. A dedicated server whose last player leaves otherwise keeps ticking every
        //mob in the world forever, with nobody to see any of it.
        [UnityTest]
        public IEnumerator MobsArePutAwayWhenTheLastPlayerLeaves() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var serverRuntime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var serverSession = new NetCodeSession(NetworkSessionRole.Server,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion, bindAddress: "127.0.0.1"),
                null, () => serverRuntime.World, logger);
            var serverBulk = new ChunkCompanionService(serverSession, 0);
            var store = new InMemoryEntityChunkStore();
            var serverGhosts = new EntityGhostService(serverSession, registry, serverBulk, chunkStore: store);
            await Host(serverRuntime, serverSession, serverBulk, serverGhosts).StartAsync(CancellationToken.None);

            var clientRuntime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var clientSession = new NetCodeSession(NetworkSessionRole.Client,
                new NetworkSessionOptions("127.0.0.1", serverSession.ListeningPort, NetworkSessionOptions.CurrentProtocolVersion),
                () => clientRuntime.World, null, logger, () => 204);
            var clientBulk = new ChunkCompanionService(clientSession);
            var clientHost = Host(clientRuntime, clientSession, clientBulk,
                new EntityGhostService(clientSession, registry, clientBulk));
            await clientHost.StartAsync(CancellationToken.None);
            await Until(() => clientSession.State == NetworkSessionState.InGame, "the client never got in game");
            await Until(() => serverBulk.BoundPeerCount == 1, "the client never bound to the chunk channel");

            var prefabs = serverRuntime.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => prefabs.Built);
            EntitySpawn.Spawn(serverRuntime.World.EntityManager, prefabs, prefabs.Registry.GetId(Orbiter),
                SectorGrid.FromBlocks(new int3(4, 20, 4)), radius: 4f, angularSpeed: 1f);
            await Until(() => ServerEntityCount(serverRuntime.World) == 1, "the mob never came alive");

            //the only player leaves, so nothing is interested in anything any more.
            await clientHost.StopAsync(CancellationToken.None);
            await Until(() => serverBulk.BoundPeerCount == 0, "the peer never went away");

            await Until(() => ServerEntityCount(serverRuntime.World) == 0,
                "the mob is still being simulated with nobody connected");
            Assert.That(store.StoredEntityCount, Is.EqualTo(1L),
                "the mob should have been handed to the chunk store rather than left ticking");
        });

        //Quitting to the menu and loading again is a thing a player does constantly, and it is the
        //boundary where everything the entity foundation builds per session is torn down and rebuilt:
        //ghost prefabs, the chunk store, the presentation backend's meshes, the variant registration.
        //The two bugs here that survived longest were both something outliving what owned it.
        [UnityTest]
        public IEnumerator ASecondSessionInTheSameProcessStillSpawnsAndReplicates() => UniTask.ToCoroutine(async () =>
        {
            for (int session = 0; session < 2; session++)
            {
                var context = await StartSession(205 + (ulong)session);
                var prefabs = context.Server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
                await Until(() => prefabs.Built, $"session {session} never built its ghost prefabs");

                EntitySpawn.Spawn(context.Server.World.EntityManager, prefabs, prefabs.Registry.GetId(Orbiter),
                    SectorGrid.FromBlocks(new int3(6, 40, 6)), radius: 6f, angularSpeed: 1.2f);
                await Until(() => ServerEntityCount(context.Server.World) == 1, $"session {session} never spawned");
                await Until(() => ClientGhostCount(context.Client.World) == 1, $"session {session} never replicated");

                //tear the session down the way the game does, before the next one starts.
                for (int i = hosts.Count - 1; i >= 0; i--) await hosts[i].StopAsync(CancellationToken.None);
                hosts.Clear();
                await Frames(4);
            }
        });

        private static UniTask MoveAnchorTo(SessionContext context, int x) => MoveAnchorTo(context, x, 0, frames: 4);

        private static async UniTask MoveAnchorTo(SessionContext context, int x, int z, int frames)
        {
            Assert.That(context.Bulk.SetServerInterest(context.Session.LocalPeerId,
                new ChunkAddress(1, new int3(x, 1, z)), StreamRadius, 1), Is.True);
            await Frames(frames);
        }

        private readonly struct SessionContext
        {
            public readonly NetCodeSession Session;
            public readonly ChunkCompanionService Bulk;
            public readonly InMemoryEntityChunkStore Store;
            public readonly ServerRuntime Server;
            public readonly ClientRuntime Client;

            public SessionContext(NetCodeSession session, ChunkCompanionService bulk, InMemoryEntityChunkStore store,
                ServerRuntime server, ClientRuntime client)
            { Session = session; Bulk = bulk; Store = store; Server = server; Client = client; }
        }

        private async UniTask<SessionContext> StartSession(ulong identity)
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => identity);
            var bulk = new ChunkCompanionService(session,
                streamingOptions: new ChunkStreamingOptions(horizontalRadius: StreamRadius, verticalRadius: 1));
            var store = new InMemoryEntityChunkStore();
            var ghosts = new EntityGhostService(session, registry, bulk, allowDebugSpawns: true, chunkStore: store,
                simulationDistance: new EntitySimulationDistance(SimulateRadius, 1));
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);
            await Until(() => session.State == NetworkSessionState.InGame);
            return new SessionContext(session, bulk, store, server, client);
        }

        private static int ServerEntityCount(World world)
        {
            if (world is not { IsCreated: true }) return -1;
            using var query = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EntityTypeId>(), ComponentType.ReadOnly<ChunkResidency>());
            return query.CalculateEntityCount();
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

        private static async UniTask Frames(int count)
        { for (int i = 0; i < count; i++) await UniTask.Yield(); }

        private static UniTask Until(Func<bool> condition) => Until(condition, "condition was never met");

        private static async UniTask Until(Func<bool> condition, string because)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
            Assert.That(condition(), Is.True, because);
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
