using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Server.Runtime;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class ChunkCompanionTests
    {
        private readonly List<GameHost> hosts = new();
        private readonly NullLogger logger = new();
        [UnityTest]
        public IEnumerator IpcBindsAfterAdmissionOwnsWorldStoresAndStopsBeforeWorlds() => UniTask.ToCoroutine(async () =>
        {
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer, new NetworkSessionOptions("127.0.0.1", 0, 1), () => client.World, () => server.World, logger, () => 100);
            var bulk = new ChunkCompanionService(session);
            var host = Host(server, client, session, bulk);
            await host.StartAsync(CancellationToken.None);
            Assert.That(bulk.ClientState, Is.EqualTo(ChunkConnectionState.Bound));
            Assert.That(bulk.BoundPeerCount, Is.EqualTo(1)); Assert.That(bulk.ListeningPort, Is.Not.Zero);
            Assert.That(server.World.GetExistingSystemManaged<ChunkWorldSystem>().Store.Count, Is.EqualTo(9));
            await Until(() => bulk.ClientDataReady);
            Assert.That(client.World.GetExistingSystemManaged<ChunkWorldSystem>().Store.Count, Is.EqualTo(9));
            using (var query = client.World.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame))) Assert.That(query.IsEmpty, Is.True);
            Assert.That(session.State, Is.EqualTo(NetworkSessionState.AwaitingWorldData));
            ushort port = bulk.ListeningPort;
            await host.StopAsync(CancellationToken.None);
            Assert.That(server.World, Is.Null); Assert.That(client.World, Is.Null);
            using var replacement = new BulkDriver(true, true, NetworkEndpoint.LoopbackIpv4.WithPort(port));
        });

        [UnityTest]
        public IEnumerator IpcStreamingAppliesEditsAndEvictionReentryUnderNewEpoch() => UniTask.ToCoroutine(async () =>
        {
            var server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(true));
            var client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(true));
            var session = new NetCodeSession(NetworkSessionRole.ClientAndServer, new NetworkSessionOptions("127.0.0.1", 0, 1), () => client.World, () => server.World, logger, () => 120);
            var bulk = new ChunkCompanionService(session);
            await Host(server, client, session, bulk).StartAsync(CancellationToken.None);
            await Until(() => bulk.ClientDataReady && bulk.AppliedChunkAcknowledgements >= 9);
            var source = server.World.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            var replica = client.World.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            var address = new ChunkAddress(1, default);
            ulong incarnation;
            using (var lease = source.Acquire(address))
            {
                incarnation = lease.Incarnation;
                lease.Apply(new[] { new CellEdit(7, 1, 0), new CellEdit(8, 0, 1) });
                await Until(() => replica.TryReadReplica(address, out var image) && image.Revision == lease.Revision && bulk.SentChunkDeltas > 0);
                Assert.That(replica.TryReadReplica(address, out var updated), Is.True);
                for (int i = 0; i < ChunkLayout.Volume; i++)
                { Assert.That(updated.SolidAt(i), Is.EqualTo(lease.SolidAt(i))); Assert.That(updated.FluidAt(i), Is.EqualTo(lease.FluidAt(i))); }
            }
            Assert.That(bulk.SetServerInterest(session.LocalPeerId, new ChunkAddress(1, new Unity.Mathematics.int3(5)), 0, 0), Is.True);
            await Until(() => replica.InterestEpoch == 2 && bulk.ClientDataReady);
            Assert.That(replica.TryReadReplica(address, out _), Is.False); Assert.That(source.Count, Is.EqualTo(1));
            Assert.That(bulk.SetServerInterest(session.LocalPeerId, address, 0, 0), Is.True);
            await Until(() => replica.InterestEpoch == 3 && bulk.ClientDataReady);
            Assert.That(replica.TryReadReplica(address, out var returned), Is.True);
            Assert.That(returned.Incarnation, Is.GreaterThan(incarnation)); Assert.That(returned.SolidAt(7), Is.Zero);
            using var query = client.World.EntityManager.CreateEntityQuery(typeof(NetworkStreamInGame));
            Assert.That(query.IsEmpty, Is.True);
        });

        [UnityTest]
        public IEnumerator UdpLateJoinSharesResidencyAndDisjointInterestReleasesOnlyDepartingPeer() => UniTask.ToCoroutine(async () =>
        {
            var bulk = CreateServer(out var session, out var host); await host.StartAsync(CancellationToken.None);
            var first = CreateClient(session.ListeningPort, 130, null, out var firstSession, out var firstHost);
            await firstHost.StartAsync(CancellationToken.None); await Until(() => first.ClientDataReady);
            var source = session.ServerWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            var address = new ChunkAddress(1, default);
            using var lease = source.Acquire(address);
            lease.Apply(new[] { new CellEdit(42, 1, 0) });
            var second = CreateClient(session.ListeningPort, 131, null, out var secondSession, out var secondHost);
            await secondHost.StartAsync(CancellationToken.None); await Until(() => second.ClientDataReady);
            var secondReplica = secondSession.ClientWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            Assert.That(secondReplica.TryReadReplica(address, out var late), Is.True);
            Assert.That(late.Revision, Is.EqualTo(lease.Revision)); Assert.That(late.SolidAt(42), Is.EqualTo(1));
            Assert.That(source.Count, Is.EqualTo(9));
            Assert.That(bulk.SetServerInterest(firstSession.LocalPeerId, new ChunkAddress(2, new Unity.Mathematics.int3(-4, 3, -8)), 0, 1), Is.True);
            var firstReplica = firstSession.ClientWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            await Until(() => firstReplica.InterestEpoch == 2 && first.ClientDataReady);
            Assert.That(firstReplica.Count, Is.EqualTo(3)); Assert.That(source.Count, Is.EqualTo(12));
            Assert.That(firstReplica.TryReadReplica(address, out _), Is.False);
            await firstHost.StopAsync(CancellationToken.None);
            await Until(() => bulk.BoundPeerCount == 1 && source.Count == 9);
            Assert.That(second.ClientDataReady, Is.True); Assert.That(secondReplica.TryReadReplica(address, out _), Is.True);
        });

        [UnityTest]
        public IEnumerator UdpEditsDuringLargeSnapshotCatchUpAfterAppliedAck() => UniTask.ToCoroutine(async () =>
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            var bulk = new ChunkCompanionService(session, 0, streamingOptions: new ChunkStreamingOptions(0, 0, 1024, 1024));
            await Host(runtime, session, bulk).StartAsync(CancellationToken.None);
            var source = session.ServerWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            var address = new ChunkAddress(1, default);
            using var lease = source.Acquire(address);
            var edits = new CellEdit[ChunkLayout.Volume];
            for (int i = 0; i < edits.Length; i++) edits[i] = new CellEdit(i, (uint)(i % 2), 0);
            lease.Apply(edits);
            var client = CreateClient(session.ListeningPort, 132, null, out var clientSession, out var clientHost);
            await clientHost.StartAsync(CancellationToken.None);
            await Until(() => bulk.SentChunkSnapshots > 0);
            Assert.That(bulk.AppliedChunkAcknowledgements, Is.Zero, "Edit while the captured snapshot is still in flight.");
            lease.Apply(new[] { new CellEdit(0, 0, 1), new CellEdit(1, 0, 0) });
            var replica = clientSession.ClientWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
            await Until(() => replica.TryReadReplica(address, out var image) && image.Revision == lease.Revision && bulk.AppliedChunkAcknowledgements >= 2);
            Assert.That(bulk.SentChunkDeltas, Is.GreaterThan(0));
            replica.TryReadReplica(address, out var final);
            for (int i = 0; i < ChunkLayout.Volume; i++)
            { Assert.That(final.SolidAt(i), Is.EqualTo(lease.SolidAt(i))); Assert.That(final.FluidAt(i), Is.EqualTo(lease.FluidAt(i))); }
        });

        [UnityTest]
        public IEnumerator UdpKickRevokesOnlyAffectedBinding() => UniTask.ToCoroutine(async () =>
        {
            var server = CreateServer(out var serverSession, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var first = CreateClient(serverSession.ListeningPort, 101, null, out var firstSession, out var firstHost);
            var second = CreateClient(serverSession.ListeningPort, 102, null, out _, out var secondHost);
            await firstHost.StartAsync(CancellationToken.None); await secondHost.StartAsync(CancellationToken.None);
            Assert.That(server.BoundPeerCount, Is.EqualTo(2));
            Assert.That(serverSession.Kick(firstSession.LocalPeerId), Is.True);
            await Until(() => server.BoundPeerCount == 1 && first.ClientState == ChunkConnectionState.Faulted);
            Assert.That(second.ClientState, Is.EqualTo(ChunkConnectionState.Bound));
            Assert.That(first.LastFailure, Is.EqualTo(NetworkFailure.Kicked));
        });

        [UnityTest]
        public IEnumerator RegistryMismatchRollsBackClientAndReleasesAdmission() => UniTask.ToCoroutine(async () =>
        {
            CreateServer(out var serverSession, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var registry = new BlockRegistry(new[] {
                new StateDefinition("digblocks:air", "digblocks:static", "digblocks:cube", Array.Empty<string>(), true),
                new StateDefinition("digblocks:stone", "digblocks:different", "digblocks:cube", Array.Empty<string>(), false)
            }, new[] { new StateDefinition("digblocks:empty", "digblocks:static", "digblocks:cube", Array.Empty<string>(), false) });
            var bulk = CreateClient(serverSession.ListeningPort, 103, registry, out _, out var host);
            bool failed = false;
            try { await host.StartAsync(CancellationToken.None); }
            catch (NetworkSessionException exception) { failed = true; Assert.That(exception.Reason.ToString(), Is.EqualTo("ChunkRegistryMismatch")); }
            Assert.That(failed, Is.True); Assert.That(bulk.ClientState, Is.EqualTo(ChunkConnectionState.Faulted));
            await Until(() => serverSession.Peers.Count == 0);
        });

        [UnityTest]
        public IEnumerator OccupiedBulkPortRollsBackNativeSessionAndWorld() => UniTask.ToCoroutine(async () =>
        {
            using var occupied = new BulkDriver(false, true, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            var host = Host(runtime, session, new ChunkCompanionService(session, occupied.LocalPort));
            bool failed = false;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Failed to bind UDP socket because the address is already in use.*"));
            try { await host.StartAsync(CancellationToken.None); } catch (NetworkSessionException) { failed = true; }
            Assert.That(failed, Is.True); Assert.That(runtime.World, Is.Null);
        });

        [UnityTest]
        public IEnumerator MissingCompanionTimesOutAndReleasesPeer() => UniTask.ToCoroutine(async () =>
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var server = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            await Host(runtime, server).StartAsync(CancellationToken.None);
            var client = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", server.ListeningPort, 1), () => client.World, null, logger, () => 104);
            var host = Host(client, session, new ChunkCompanionService(session, bindingTimeoutSeconds: 0.5));
            bool failed = false;
            try { await host.StartAsync(CancellationToken.None); }
            catch (NetworkSessionException exception) { failed = true; Assert.That(exception.Reason.ToString(), Is.EqualTo("ChunkBindingTimedOut")); }
            Assert.That(failed, Is.True); Assert.That(client.World, Is.Null);
            await Until(() => server.Peers.Count == 0);
        });

        [UnityTest]
        public IEnumerator InvalidTicketCannotDisconnectTheClaimedAdmittedPeer() => UniTask.ToCoroutine(async () =>
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var serverSession = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            var server = new ChunkCompanionService(serverSession, 0);
            var serverHost = Host(runtime, serverSession, server);
            await serverHost.StartAsync(CancellationToken.None);
            var legitimate = CreateClient(serverSession.ListeningPort, 105, null, out var session, out var host);
            await host.StartAsync(CancellationToken.None);
            using var attacker = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var connection = attacker.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(server.ListeningPort));
            bool ready = false, rejected = false;
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!ready && Time.realtimeSinceStartupAsDouble < deadline)
            {
                attacker.Update(); while (attacker.TryPopEvent(out var item)) ready |= item.Type == BulkEventType.Connected;
                await UniTask.Yield();
            }
            Assert.That(ready, Is.True);
            ulong generation = 0;
            foreach (var pair in runtime.World.GetExistingSystemManaged<SessionContextSystem>().Context.ServerConnections)
                if (pair.Value == session.LocalPeerId) generation = SessionContext.ConnectionKey(pair.Key);
            Assert.That(generation, Is.Not.Zero);
            Assert.That(attacker.TrySend(connection, BulkBindingFrames.EncodeRequest(new BulkBindRequest(session.LocalPeerId, generation,
                new BulkTicket(1, 2), 32, BlockRegistry.CreateDummy().Fingerprint))), Is.True);
            while (!rejected && Time.realtimeSinceStartupAsDouble < deadline)
            {
                attacker.Update(); while (attacker.TryPopEvent(out var item)) rejected |= item.Type == BulkEventType.Disconnected;
                await UniTask.Yield();
            }
            Assert.That(rejected, Is.True); Assert.That(server.BoundPeerCount, Is.EqualTo(1));
            Assert.That(legitimate.ClientState, Is.EqualTo(ChunkConnectionState.Bound));
            Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.None));
        });

        [UnityTest]
        public IEnumerator LosingOneBulkConnectionDisconnectsOnlyItsNativePeer() => UniTask.ToCoroutine(async () =>
        {
            var server = CreateServer(out var serverSession, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var first = CreateClient(serverSession.ListeningPort, 106, null, out var firstSession, out var firstHost);
            var second = CreateClient(serverSession.ListeningPort, 107, null, out _, out var secondHost);
            await firstHost.StartAsync(CancellationToken.None); await secondHost.StartAsync(CancellationToken.None);
            await first.StopAsync(CancellationToken.None);
            await Until(() => server.BoundPeerCount == 1 && serverSession.Peers.Count == 1 && firstSession.LastFailure != NetworkFailure.None);
            Assert.That(firstSession.LastFailure, Is.EqualTo(NetworkFailure.ChunkChannelFailed));
            Assert.That(second.ClientState, Is.EqualTo(ChunkConnectionState.Bound));
        });

        [UnityTest]
        public IEnumerator CancelWhileWaitingForOfferRollsBackOwnedStoreAndWorld() => UniTask.ToCoroutine(async () =>
        {
            var serverRuntime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var serverSession = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => serverRuntime.World, logger);
            await Host(serverRuntime, serverSession).StartAsync(CancellationToken.None);
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", serverSession.ListeningPort, 1), () => runtime.World, null, logger, () => 108);
            var host = Host(runtime, session, new ChunkCompanionService(session));
            using var cancellation = new CancellationTokenSource();
            var startup = host.StartAsync(cancellation.Token).Preserve();
            await Until(() => runtime.World?.GetExistingSystemManaged<ChunkWorldSystem>()?.Store != null);
            cancellation.Cancel();
            bool cancelled = false;
            try { await startup; } catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True); Assert.That(runtime.World, Is.Null);
            await Until(() => serverSession.Peers.Count == 0);
        });

        [UnityTest]
        public IEnumerator ServerDeadlineReleasesAdmittedPeerThatNeverBinds() => UniTask.ToCoroutine(async () =>
        {
            var serverRuntime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var serverSession = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => serverRuntime.World, logger);
            var bulk = new ChunkCompanionService(serverSession, 0, bindingTimeoutSeconds: 0.5);
            await Host(serverRuntime, serverSession, bulk).StartAsync(CancellationToken.None);
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", serverSession.ListeningPort, 1), () => runtime.World, null, logger, () => 109);
            await Host(runtime, session).StartAsync(CancellationToken.None);
            await Until(() => serverSession.Peers.Count == 0);
            Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.ChunkBindingTimedOut));
            Assert.That(bulk.BoundPeerCount, Is.Zero);
        });

        [UnityTest]
        public IEnumerator PendingConnectionsAreCappedExpireAndReleaseCapacity() => UniTask.ToCoroutine(async () =>
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            var session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            var bulk = new ChunkCompanionService(session, 0, bindingTimeoutSeconds: 2, maxPendingBindings: 1);
            await Host(runtime, session, bulk).StartAsync(CancellationToken.None);
            using var first = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            using var second = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(bulk.ListeningPort);
            bool firstClosed = false, secondClosed = false;
            async UniTask PumpUntil(Func<bool> condition)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    first.Update(); second.Update();
                    while (first.TryPopEvent(out var item)) firstClosed |= item.Type == BulkEventType.Disconnected;
                    while (second.TryPopEvent(out var item)) secondClosed |= item.Type == BulkEventType.Disconnected;
                    await UniTask.Yield();
                }
                Assert.That(condition(), Is.True);
            }
            first.Connect(endpoint);
            await PumpUntil(() => bulk.PendingBindingCount == 1);
            second.Connect(endpoint);
            await PumpUntil(() => secondClosed);
            Assert.That(firstClosed, Is.False); Assert.That(bulk.PendingBindingCount, Is.EqualTo(1));
            await PumpUntil(() => firstClosed && bulk.PendingBindingCount == 0);
            first.Connect(endpoint);
            await PumpUntil(() => bulk.PendingBindingCount == 1);
            Assert.That(session.Peers.Count, Is.Zero);
            Assert.That(runtime.World.GetExistingSystemManaged<ChunkWorldSystem>().Store.Count, Is.Zero);
        });

        [UnityTest]
        public IEnumerator WrongGenerationPreservesTicketAndReplayCannotReplaceBinding() => UniTask.ToCoroutine(async () =>
        {
            var server = CreateServer(out var serverSession, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var (session, offer) = await ProbeClient(serverSession.ListeningPort, 110);
            using var raw = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var connection = await ConnectRaw(raw, offer.Port);
            var ticket = new BulkTicket(offer.TicketHigh, offer.TicketLow);
            await Exchange(raw, connection, BulkBindingFrames.EncodeRequest(new BulkBindRequest(offer.PeerId, offer.Generation + 1,
                ticket, offer.Edge, offer.Fingerprint.ToString())), false);
            connection = await ConnectRaw(raw, offer.Port);
            var request = BulkBindingFrames.EncodeRequest(new BulkBindRequest(offer.PeerId, offer.Generation, ticket, offer.Edge, offer.Fingerprint.ToString()));
            var accepted = await Exchange(raw, connection, request, true);
            BulkBindingFrames.DecodeAccepted(accepted, out ulong peer, out ulong generation);
            Assert.That(peer, Is.EqualTo(offer.PeerId)); Assert.That(generation, Is.EqualTo(offer.Generation));
            using var replay = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var replayConnection = await ConnectRaw(replay, offer.Port);
            await Exchange(replay, replayConnection, request, false);
            Assert.That(server.BoundPeerCount, Is.EqualTo(1)); Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.None));
        });

        [UnityTest]
        public IEnumerator ServerChecksCompatibilityEvenWhenClientBypassesOfferChecks() => UniTask.ToCoroutine(async () =>
        {
            var server = CreateServer(out var serverSession, out var serverHost);
            await serverHost.StartAsync(CancellationToken.None);
            var (session, offer) = await ProbeClient(serverSession.ListeningPort, 111);
            using var raw = new BulkDriver(false, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var connection = await ConnectRaw(raw, offer.Port);
            await Exchange(raw, connection, BulkBindingFrames.EncodeRequest(new BulkBindRequest(offer.PeerId, offer.Generation,
                new BulkTicket(offer.TicketHigh, offer.TicketLow), offer.Edge, new string('0', 64))), false);
            await Until(() => session.LastFailure != NetworkFailure.None && serverSession.Peers.Count == 0);
            Assert.That(session.LastFailure, Is.EqualTo(NetworkFailure.ChunkRegistryMismatch));
            Assert.That(server.BoundPeerCount, Is.Zero);
        });

        private async UniTask<(NetCodeSession, BulkConnectionOfferRpc)> ProbeClient(ushort port, ulong identity)
        {
            BulkOfferProbeSystem probe = null;
            var runtime = new ClientRuntime(() =>
            {
                var world = NetCodeWorldFactory.CreateClientWorld();
                probe = world.GetOrCreateSystemManaged<BulkOfferProbeSystem>();
                var group = world.GetExistingSystemManaged<SimulationSystemGroup>();
                group.AddSystemToUpdateList(probe); group.SortSystems(); return world;
            });
            var session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", port, 1), () => runtime.World, null, logger, () => identity);
            await Host(runtime, session).StartAsync(CancellationToken.None);
            await Until(() => probe.Offer.HasValue);
            return (session, probe.Offer.Value);
        }
        private static async UniTask<NetworkConnection> ConnectRaw(BulkDriver driver, ushort port)
        {
            var connection = driver.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(port));
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                driver.Update();
                while (driver.TryPopEvent(out var item)) if (item.Type == BulkEventType.Connected) return connection;
                await UniTask.Yield();
            }
            Assert.Fail("Raw companion did not connect."); return default;
        }
        private static async UniTask<byte[]> Exchange(BulkDriver driver, NetworkConnection connection, byte[] request, bool accepted)
        {
            Assert.That(driver.TrySend(connection, request), Is.True);
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                driver.Update();
                while (driver.TryPopEvent(out var item))
                {
                    Assert.That(item.Type, Is.EqualTo(accepted ? BulkEventType.Data : BulkEventType.Disconnected));
                    return item.Payload;
                }
                await UniTask.Yield();
            }
            Assert.Fail("Companion did not accept or reject the request."); return null;
        }

        private ChunkCompanionService CreateServer(out NetCodeSession session, out GameHost host)
        {
            var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
            session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
            var bulk = new ChunkCompanionService(session, 0); host = Host(runtime, session, bulk); return bulk;
        }
        private ChunkCompanionService CreateClient(ushort port, ulong identity, BlockRegistry registry, out NetCodeSession session, out GameHost host)
        {
            var runtime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
            session = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", port, 1), () => runtime.World, null, logger, () => identity);
            var bulk = new ChunkCompanionService(session, registry: registry); host = Host(runtime, session, bulk); return bulk;
        }
        private GameHost Host(params IGameService[] services) { var host = new GameHost(services, logger); hosts.Add(host); return host; }
        private static async UniTask Until(Func<bool> condition)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
            Assert.That(condition(), Is.True);
        }
        [UnityTearDown] public IEnumerator Cleanup() => UniTask.ToCoroutine(async () =>
        { for (int i = hosts.Count - 1; i >= 0; i--) await hosts[i].StopAsync(CancellationToken.None); hosts.Clear(); });
        private sealed class NullLogger : IGameLogger
        { public IGameLogger CreateFor(string sourceName) => this; public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null) { } }
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class BulkOfferProbeSystem : SystemBase
    {
        public BulkConnectionOfferRpc? Offer;
        protected override void OnUpdate()
        {
            foreach (var offer in SystemAPI.Query<RefRO<BulkConnectionOfferRpc>>().WithAll<ReceiveRpcCommandRequest>()) Offer = offer.ValueRO;
        }
    }
}
