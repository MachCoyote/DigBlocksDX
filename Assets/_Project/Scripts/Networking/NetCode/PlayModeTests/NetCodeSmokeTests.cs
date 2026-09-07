using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Networking;
using DigBlocks.Server.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class NetCodeSmokeTests
    {
        private GameHost host;
        private readonly List<GameHost> extraHosts = new();

        [UnityTest]
        public IEnumerator StopOnAdmissionCannotCompleteStartupSuccessfully() => UniTask.ToCoroutine(async () =>
        {
            var server = StartServer(1, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            NetCodeSession session = null;
            var runtime = new ClientRuntime(() =>
            {
                World world = NetCodeWorldFactory.CreateClientWorld();
                var probe = world.GetOrCreateSystemManaged<StopOnAdmissionTestSystem>();
                probe.Stop = () => session.StopAsync(CancellationToken.None).Forget();
                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                simulation.AddSystemToUpdateList(probe);
                simulation.SortSystems();
                return world;
            });
            session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", server.ListeningPort, 1), () => runtime.World, null, new NullLogger(), () => 100ul);
            var candidate = new GameHost(new IGameService[] { runtime, session }, new NullLogger());
            extraHosts.Add(candidate);
            bool cancelled = false;
            try { await candidate.StartAsync(CancellationToken.None); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True, "Startup returned success after the session was stopped.");
            Assert.That(runtime.World, Is.Null);
        });

        [UnityTest]
        public IEnumerator Udp_SimultaneousArrivalsCannotBothReserveLastSlot() => UniTask.ToCoroutine(async () =>
        {
            var server = StartServer(1, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            CreateClient(server.ListeningPort, 100, 1, out var first);
            CreateClient(server.ListeningPort, 200, 1, out var second);
            async UniTask<bool> Join(GameHost candidate)
            {
                try { await candidate.StartAsync(CancellationToken.None); return true; }
                catch (NetworkSessionException exception)
                {
                    Assert.That(exception.Reason, Is.EqualTo(NetworkFailure.ServerFull));
                    return false;
                }
            }
            var firstJoin = Join(first);
            var secondJoin = Join(second);
            var results = await UniTask.WhenAll(firstJoin, secondJoin);
            Assert.That(results.Item1 ^ results.Item2, Is.True);
            Assert.That(server.Peers.Count, Is.EqualTo(1));
        });

        [UnityTest]
        public IEnumerator Udp_CancelAfterConnectRequestRollsBackWorld() => UniTask.ToCoroutine(async () =>
        {
            var temporaryServer = StartServer(1, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            ushort port = temporaryServer.ListeningPort;
            await serverHost.StopAsync(CancellationToken.None);
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", port, 1), () => runtime.World, null, new NullLogger(), () => 101ul);
            var clientHost = new GameHost(new IGameService[] { runtime, session }, new NullLogger());
            extraHosts.Add(clientHost);
            using var cancellation = new CancellationTokenSource();
            var start = clientHost.StartAsync(cancellation.Token).Preserve();
            Assert.That(runtime.World, Is.Not.Null);
            await UniTask.Yield();
            cancellation.Cancel();
            bool cancelled = false;
            try { await start; } catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True);
            Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.Cancelled));
            Assert.That(runtime.World, Is.Null);
            await clientHost.StopAsync(CancellationToken.None);
        });

        [Test]
        public void Runtime_CancelledStopStillDisposesOwnedWorld()
        {
            var runtime = new ClientRuntime(() => new World("Runtime lifetime test"));
            runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var world = runtime.World;
            try
            {
                Assert.Throws<InvalidOperationException>(() => runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                runtime.StopAsync(cancellation.Token).GetAwaiter().GetResult();
                Assert.That(world.IsCreated, Is.False);
                Assert.That(runtime.World, Is.Null);
            }
            finally
            {
                runtime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (world.IsCreated) world.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Udp_RejectsProtocolDuplicateAndFullThenReleasesSlot() => UniTask.ToCoroutine(async () =>
        {
            var server = StartServer(1, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var first = CreateClient(server.ListeningPort, 100, 1, out var firstHost);
            await firstHost.StartAsync(CancellationToken.None);
            Assert.That(first.State, Is.EqualTo(NetworkSessionState.AwaitingWorldData));
            Assert.That(server.Peers.Count, Is.EqualTo(1));
            Assert.That(server.Peers[0].OfflineXuid, Is.EqualTo(100));

            var duplicate = CreateClient(server.ListeningPort, 100, 1, out var duplicateHost);
            await ExpectFailure(duplicateHost, NetworkFailure.DuplicateIdentity);
            var full = CreateClient(server.ListeningPort, 200, 1, out var fullHost);
            await ExpectFailure(fullHost, NetworkFailure.ServerFull);
            var mismatch = CreateClient(server.ListeningPort, 300, 2, out var mismatchHost);
            await ExpectFailure(mismatchHost, NetworkFailure.ProtocolMismatch);
            Assert.That(server.State, Is.EqualTo(NetworkSessionState.Listening));
            Assert.That(first.State, Is.EqualTo(NetworkSessionState.AwaitingWorldData));

            Assert.That(server.Kick(first.LocalPeerId), Is.True);
            await WaitUntil(() => first.State == NetworkSessionState.Disconnected && server.Peers.Count == 0);
            Assert.That(first.LastFailure, Is.EqualTo(NetworkFailure.Kicked));
            Assert.That(first.LocalPeerId, Is.Zero);
            var rejoined = CreateClient(server.ListeningPort, 100, 1, out var rejoinedHost);
            await rejoinedHost.StartAsync(CancellationToken.None);
            Assert.That(rejoined.LocalPeerId, Is.GreaterThan(1));
            await serverHost.StopAsync(CancellationToken.None);
            await WaitUntil(() => rejoined.State == NetworkSessionState.Disconnected);
            Assert.That(rejoined.LastFailure, Is.EqualTo(NetworkFailure.ServerStopping));
        });

        [UnityTest]
        public IEnumerator Udp_TimeoutAndCancelledStartupCleanTheirWorlds() => UniTask.ToCoroutine(async () =>
        {
            var server = StartServer(1, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            ushort unusedPort = server.ListeningPort;
            await serverHost.StopAsync(CancellationToken.None);
            CreateClient(unusedPort, 100, 1, out var absentHost, 0.2);
            await ExpectFailure(absentHost, NetworkFailure.TimedOut);
            var cancelled = CreateClient(unusedPort, 200, 1, out var cancelledHost);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool wasCancelled = false;
            try { await cancelledHost.StartAsync(cancellation.Token); }
            catch (OperationCanceledException) { wasCancelled = true; }
            Assert.That(wasCancelled, Is.True);
            Assert.That(cancelledHost.State, Is.EqualTo(GameHostState.Faulted));
        });

        [UnityTest]
        public IEnumerator Udp_ThirtyTwoPeersAdmittedAndThirtyThirdRejected() => UniTask.ToCoroutine(async () =>
        {
            var server = StartServer(32, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            for (ulong i = 1; i <= 32; i++)
            {
                var client = CreateClient(server.ListeningPort, i, 1, out var clientHost);
                await clientHost.StartAsync(CancellationToken.None);
                Assert.That(client.LocalPeerId, Is.Not.Zero);
            }
            Assert.That(server.Peers.Count, Is.EqualTo(32));
            CreateClient(server.ListeningPort, 33, 1, out var extra);
            await ExpectFailure(extra, NetworkFailure.ServerFull);
            Assert.That(server.Peers.Count, Is.EqualTo(32));
        });

        private NetCodeSession StartServer(int capacity, out GameHost gameHost)
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, capacity: capacity, bindAddress: "127.0.0.1"), null, () => runtime.World, new NullLogger());
            gameHost = new GameHost(new IGameService[] { runtime, session }, new NullLogger());
            extraHosts.Add(gameHost);
            return session;
        }

        private NetCodeSession CreateClient(ushort port, ulong identity, uint protocol, out GameHost gameHost, double timeout = 10)
        {
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", port, protocol, startupTimeoutSeconds: timeout), () => runtime.World, null, new NullLogger(), () => identity);
            gameHost = new GameHost(new IGameService[] { runtime, session }, new NullLogger());
            extraHosts.Add(gameHost);
            return session;
        }

        private static async UniTask ExpectFailure(GameHost gameHost, NetworkFailure reason)
        {
            NetworkSessionException failure = null;
            try { await gameHost.StartAsync(CancellationToken.None); }
            catch (NetworkSessionException exception) { failure = exception; }
            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.Reason, Is.EqualTo(reason));
            Assert.That(gameHost.State, Is.EqualTo(GameHostState.Faulted));
        }

        private static async UniTask WaitUntil(Func<bool> condition)
        {
            double deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + 5;
            while (!condition())
            {
                Assert.That(UnityEngine.Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "Timed out awaiting observed network state.");
                await UniTask.Yield();
            }
        }

        [UnityTest]
        public IEnumerator SinglePlayer_Handshake_ReachesReadyState()
        {
            var logger = new NullLogger();
            var server = new ServerRuntime(
                () => NetCodeWorldFactory.CreateServerWorld(true));

            var client = new ClientRuntime(
                () => NetCodeWorldFactory.CreateClientWorld(true));

            var session = new NetCodeSession(
                NetworkSessionRole.ClientAndServer,
                new NetworkSessionOptions("127.0.0.1", 7979, 1),
                () => client.World,
                () => server.World,
                logger,
                () => 123ul);

            host = new GameHost(
                new IGameService[]
                {
                    server,
                    client,
                    session
                },
                logger);

            yield return host.StartAsync(
                CancellationToken.None).ToCoroutine();

            Assert.That(
                session.State,
                Is.EqualTo(NetworkSessionState.AwaitingWorldData));

            Assert.That(client.World, Is.Not.Null);
            Assert.That(server.World, Is.Not.Null);

            using (EntityQuery readyQuery =
                client.World.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkSessionReady>()))
            {
                Assert.That(readyQuery.CalculateEntityCount(), Is.EqualTo(1));
            }

            using (var approved = server.World.EntityManager.CreateEntityQuery(typeof(ConnectionApproved)))
                Assert.That(approved.CalculateEntityCount(), Is.EqualTo(1));
            using (var inGame = client.World.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame)))
                Assert.That(inGame.IsEmptyIgnoreFilter, Is.True);

            SendWrongDirection<ServerHelloRpc>(client.World);
            SendWrongDirection<ClientHelloRpc>(server.World);
            yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
            using (var unexpected = server.World.EntityManager.CreateEntityQuery(typeof(ServerHelloRpc), typeof(ReceiveRpcCommandRequest)))
                Assert.That(unexpected.IsEmptyIgnoreFilter, Is.True, "Server leaked a client-issued server hello.");
            using (var unexpected = client.World.EntityManager.CreateEntityQuery(typeof(ClientHelloRpc), typeof(ReceiveRpcCommandRequest)))
                Assert.That(unexpected.IsEmptyIgnoreFilter, Is.True, "Client leaked a server-issued client hello.");
            Assert.That(session.Peers.Count, Is.EqualTo(1));

            yield return host.StopAsync(
                CancellationToken.None).ToCoroutine();

            Assert.That(client.World, Is.Null);
            Assert.That(server.World, Is.Null);

            host = null;
        }

        private static void SendWrongDirection<T>(World world) where T : unmanaged, IComponentData
        {
            var manager = world.EntityManager;
            using var connection = manager.CreateEntityQuery(typeof(NetworkId));
            var rpc = manager.CreateEntity(typeof(T), typeof(SendRpcCommandRequest));
            manager.SetComponentData(rpc, new SendRpcCommandRequest { TargetConnection = connection.GetSingletonEntity() });
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = extraHosts.Count - 1; i >= 0; i--)
                yield return extraHosts[i].StopAsync(CancellationToken.None).ToCoroutine();
            extraHosts.Clear();
            if (host == null)
            {
                yield break;
            }

            yield return host.StopAsync(
                CancellationToken.None).ToCoroutine();

            host = null;
        }

        private sealed class NullLogger : IGameLogger
        {
            public IGameLogger CreateFor(string sourceName)
            {
                return this;
            }

            public void Log(
                string message,
                GameLogLevel level = GameLogLevel.Information,
                Exception exception = null)
            {
            }
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientHelloReceiveSystem))]
    public partial class StopOnAdmissionTestSystem : SystemBase
    {
        public Action Stop;
        protected override void OnCreate() => RequireForUpdate<NetworkSessionReady>();
        protected override void OnUpdate()
        {
            Enabled = false;
            Stop();
        }
    }
}
