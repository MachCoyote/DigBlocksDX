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
using Unity.NetCode;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //Ghost prefabs built from code have one failure mode that matters more than all the others: if the
    //two worlds do not build byte-identical collections, NetCode hashes them, disagrees, and drops the
    //connection. These tests exist to catch that, because the symptom on its own is opaque.
    public sealed class EntityGhostPrefabTests
    {
        private readonly List<GameHost> hosts = new();
        private readonly NullLogger logger = new();

        private static EntityTypeRegistry Registry() => new EntityTypeRegistry(new[]
        {
            new EntityTypeDefinition("digblocks:orbiter", "digblocks:models/cube",
                new[] { "digblocks:circle_flight" },
                new EntityTypeAttributes(EntityFlags.None, EntityCategory.Marker, 0.6f, 0.6f,
                    EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1)),
            //a second type with a different policy, so the mode and optimization mapping is exercised
            //rather than one path being tested three times.
            new EntityTypeDefinition("digblocks:dropped_item", "digblocks:models/cube",
                Array.Empty<string>(),
                new EntityTypeAttributes(EntityFlags.Persists, EntityCategory.Item, 0.25f, 0.25f,
                    EntityGhostMode.Interpolated, EntityGhostOptimization.Static, 7))
        });

        [UnityTest]
        public IEnumerator CodeBuiltPrefabsAgreeAcrossWorldsAndTheConnectionSurvivesGoingInGame() => UniTask.ToCoroutine(async () =>
        {
            var registry = Registry();
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => 170);
            var bulk = new ChunkCompanionService(session);
            var ghosts = new EntityGhostService(session, registry);
            await Host(server, client, session, bulk, ghosts).StartAsync(CancellationToken.None);

            await Until(() => bulk.ClientDataReady);
            await Until(() => session.State == NetworkSessionState.InGame);

            var serverPrefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            var clientPrefabs = client.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            await Until(() => serverPrefabs.Built && clientPrefabs.Built);
            Assert.That(serverPrefabs.PrefabCount, Is.EqualTo(registry.Count));
            Assert.That(clientPrefabs.PrefabCount, Is.EqualTo(registry.Count));

            //this is the hash NetCode itself compares: the ghost name plus every component serializer.
            await Until(() => Serializers(server.World).Count == registry.Count && Serializers(client.World).Count == registry.Count);
            Assert.That(Serializers(client.World), Is.EqualTo(Serializers(server.World)),
                "client and server ghost prefab hashes must agree or NetCode drops the connection");

            //a mismatch shows up as a disconnect a few ticks later, so staying connected is the proof.
            await Ticks(45);
            Assert.That(session.State, Is.EqualTo(NetworkSessionState.InGame));
            Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.None));
        });

        //the registry is what makes both worlds agree, so a world that never received one must not
        //quietly publish an empty collection and look like a legitimate disagreement.
        [UnityTest]
        public IEnumerator AWorldWithNoRegistryBuildsNoPrefabs() => UniTask.ToCoroutine(async () =>
        {
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 0, NetworkSessionOptions.CurrentProtocolVersion),
                () => client.World, () => server.World, logger, () => 171);
            await Host(server, client, session).StartAsync(CancellationToken.None);
            await Ticks(10);

            var prefabs = server.World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            Assert.That(prefabs.Built, Is.False);
            Assert.That(prefabs.PrefabCount, Is.Zero);
        });

        private static List<ulong> Serializers(World world)
        {
            var hashes = new List<ulong>();
            using var query = world.EntityManager.CreateEntityQuery(typeof(GhostCollectionPrefabSerializer));
            if (query.IsEmptyIgnoreFilter) return hashes;
            var buffer = world.EntityManager.GetBuffer<GhostCollectionPrefabSerializer>(query.GetSingletonEntity(), true);
            for (int i = 0; i < buffer.Length; i++) hashes.Add(buffer[i].TypeHash);
            return hashes;
        }

        private GameHost Host(params IGameService[] services)
        { var host = new GameHost(services, logger); hosts.Add(host); return host; }

        private static async UniTask Ticks(int count)
        { for (int i = 0; i < count; i++) await UniTask.Yield(); }

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
