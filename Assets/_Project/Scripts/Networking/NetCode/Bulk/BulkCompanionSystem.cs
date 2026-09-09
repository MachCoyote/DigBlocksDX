using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

namespace DigBlocks.Networking.NetCode
{
    public struct BulkConnectionOfferRpc : IRpcCommand
    {
        public ulong PeerId, Generation, TicketHigh, TicketLow;
        public ushort Port, Edge;
        public FixedString128Bytes Fingerprint;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    //admission systems run in the ordinary simulation phase in their respective role worlds.
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class BulkCompanionSystem : SystemBase
    {
        internal BulkCompanionEndpoint Endpoint;
        private BulkConnectionOfferRpc? pendingOffer;
        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>()?.Context;
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (offer, request, entity) in SystemAPI.Query<RefRO<BulkConnectionOfferRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                if (context == null || context.Registry != null || context.Stopping || context.Failure != NetworkFailure.None ||
                    request.ValueRO.SourceConnection != context.ClientConnection) continue;
                //bounded early receipt: the session can finish admission before the following service starts.
                if (!pendingOffer.HasValue) pendingOffer = offer.ValueRO;
            }
            commands.Playback(EntityManager);
            if (context == null || context.Stopping) pendingOffer = null;
            if (Endpoint != null && pendingOffer.HasValue)
            { Endpoint.ReceiveOffer(pendingOffer.Value); pendingOffer = null; }
            Endpoint?.Tick();
        }
        internal void ReleaseEndpoint() { Endpoint?.Dispose(); Endpoint = null; pendingOffer = null; }
        protected override void OnDestroy() => ReleaseEndpoint();
    }

    internal sealed class BulkCompanionEndpoint : IDisposable
    {
        private readonly World world;
        private readonly bool server, ipc;
        private readonly BlockRegistry registry;
        private readonly double timeout;
        private readonly int maxPending;
        private readonly BulkTickets tickets;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly Dictionary<NetworkConnection, ulong> bound = new();
        private readonly Dictionary<NetworkConnection, double> pending = new();
        private readonly List<ulong> removedPeers = new();
        private readonly List<NetworkConnection> removedConnections = new();
        private BulkDriver driver;
        private readonly ChunkStreamingOptions streamingOptions;
        private readonly ResidentChunkStore store;
        private ChunkStreamingServer streamingServer;
        private ChunkStreamingClient streamingClient;
        private NetworkConnection clientConnection;
        private BulkConnectionOfferRpc? clientOffer;
        private readonly double clientDeadline;
        private bool disposed;
        private sealed class Peer
        { public Entity Entity; public ulong Generation; public double Deadline; public NetworkConnection Connection; }
        private SessionContext Context => world.GetExistingSystemManaged<SessionContextSystem>()?.Context;
        public ChunkConnectionState State { get; private set; }
        public NetworkFailure Failure { get; private set; }
        public ushort Port { get; }
        public int BoundPeerCount => bound.Count;
        public int PendingBindingCount => pending.Count;
        public bool DataReady => !disposed && store.DataReady;
        public long SentChunkBytes => streamingServer?.SentBytes ?? 0;
        public long AppliedChunkAcknowledgements => streamingServer?.AppliedAcknowledgements ?? 0;
        public long SentChunkSnapshots => streamingServer?.SentSnapshots ?? 0;
        public long SentChunkDeltas => streamingServer?.SentDeltas ?? 0;
        public double MaxAppliedAckSeconds => streamingServer?.MaxAppliedAckSeconds ?? 0;
        public int PeakEncodedPayloadBytes => streamingServer?.PeakEncodedPayloadBytes ?? 0;
        public int PendingChunkPayloads => streamingServer?.PayloadCount ?? 0;
        public bool SetInterest(ulong id, ChunkAddress anchor, int horizontal, int vertical) =>
            !disposed && streamingServer != null && peers.TryGetValue(id, out var peer) && Live(Context, id, peer) &&
            streamingServer.SetInterest(id, anchor, horizontal, vertical, Now);
        public bool RequestInterest(ChunkAddress anchor) =>
            !disposed && !server && streamingClient != null && streamingClient.RequestInterest(anchor);

        public BulkCompanionEndpoint(World world, bool server, bool ipc, ushort port, BlockRegistry registry, double timeout, int maxPending,
            ChunkStreamingOptions streamingOptions, IAuthoritativeChunkSource source = null)
        {
            this.world = world; this.server = server; this.ipc = ipc; this.registry = registry; this.timeout = timeout; this.maxPending = maxPending;
            this.streamingOptions = streamingOptions;
            store = world.GetExistingSystemManaged<ChunkWorldSystem>()?.Store ?? throw new InvalidOperationException("Companion requires a chunk store.");
            var context = Context ?? throw new InvalidOperationException("Companion requires an active NetCode session.");
            if (server)
            {
                tickets = new BulkTickets(context.Options.Capacity);
                var endpoint = ipc ? NetworkEndpoint.LoopbackIpv4.WithPort(0) : NetworkEndpoint.Parse(context.Options.BindAddress, port);
                driver = new BulkDriver(ipc, true, endpoint, Math.Min(1024, context.Options.Capacity + maxPending), streamingOptions.Simulation);
                Port = driver.LocalPort; State = ChunkConnectionState.Listening;
                streamingServer = new ChunkStreamingServer(store, streamingOptions,
                    (id, packet) => peers.TryGetValue(id, out var peer) && Live(Context, id, peer) && driver.TrySend(peer.Connection, packet),
                    id => FailPeer(id, NetworkFailure.ChunkChannelFailed), source,
                    //slice to what this connection's pipeline actually accepts; the path MTU decides it.
                    id => peers.TryGetValue(id, out var peer) && peer.Connection.IsCreated ? driver.PayloadCapacity(peer.Connection) : 0);
            }
            else { State = ChunkConnectionState.AwaitingOffer; clientDeadline = Now + timeout; }
        }
        private static double Now => Time.realtimeSinceStartupAsDouble;

        public void ReceiveOffer(BulkConnectionOfferRpc offer)
        {
            if (disposed || server) return;
            var context = Context;
            if (context == null || context.Stopping || !context.Accepted || context.Failure != NetworkFailure.None) return;
            if (clientOffer.HasValue)
            {
                var previous = clientOffer.Value;
                if (previous.PeerId != offer.PeerId || previous.Generation != offer.Generation || previous.TicketHigh != offer.TicketHigh ||
                    previous.TicketLow != offer.TicketLow || previous.Port != offer.Port || previous.Edge != offer.Edge || !previous.Fingerprint.Equals(offer.Fingerprint))
                    FailClient(NetworkFailure.InvalidResponse);
                return;
            }
            if (offer.PeerId != context.PeerId || offer.PeerId == 0 || offer.Generation == 0 || offer.Port == 0 ||
                (offer.TicketHigh == 0 && offer.TicketLow == 0) || !BulkBindingFrames.ValidFingerprint(offer.Fingerprint.ToString()))
            { FailClient(NetworkFailure.InvalidResponse); return; }
            if (offer.Edge != ChunkLayout.Edge) { FailClient(NetworkFailure.ChunkLayoutMismatch); return; }
            if (offer.Fingerprint.ToString() != registry.Fingerprint) { FailClient(NetworkFailure.ChunkRegistryMismatch); return; }
            clientOffer = offer;
            try
            {
                driver = new BulkDriver(ipc, false, NetworkEndpoint.LoopbackIpv4.WithPort(0), 1, streamingOptions.Simulation);
                clientConnection = driver.Connect(NetworkEndpoint.Parse(ipc ? "127.0.0.1" : context.Options.Address, offer.Port));
                State = ChunkConnectionState.Binding;
            }
            catch (Exception) { FailClient(NetworkFailure.ChunkChannelFailed); }
        }

        public void Tick()
        {
            if (disposed) return;
            var context = Context;
            if (context == null || context.Stopping) { Dispose(); return; }
            if (!server && context.Failure != NetworkFailure.None) { FailClient(context.Failure); return; }
            if (server) RefreshPeers(context);
            else if (State != ChunkConnectionState.Bound && Now >= clientDeadline) { FailClient(NetworkFailure.ChunkBindingTimedOut); return; }
            if (driver == null) return;
            driver.Update();
            while (!disposed && driver.TryPopEvent(out var item))
            {
                if (server) ServerEvent(item);
                else ClientEvent(item);
            }
            if (disposed) return;
            streamingServer?.Tick(Now); streamingClient?.Tick(Now);
            if (disposed || !server) return;
            removedConnections.Clear();
            foreach (var pair in pending) if (Now >= pair.Value) removedConnections.Add(pair.Key);
            foreach (var connection in removedConnections) RejectPending(connection);
        }

        private bool Live(SessionContext context, ulong id, Peer peer)
        {
            var manager = world.EntityManager;
            return context != null && !context.Stopping && id != 0 && context.ServerConnections.TryGetValue(peer.Entity, out ulong current) && current == id &&
                manager.HasComponent<NetworkId>(peer.Entity) && manager.HasComponent<NetworkStreamConnection>(peer.Entity) &&
                manager.GetComponentData<NetworkStreamConnection>(peer.Entity).CurrentState == ConnectionState.State.Connected &&
                !manager.HasComponent<PendingDisconnect>(peer.Entity) && !manager.HasComponent<NetworkStreamRequestDisconnect>(peer.Entity);
        }
        private void RefreshPeers(SessionContext context)
        {
            removedPeers.Clear();
            foreach (var pair in peers)
            {
                if (world.EntityManager.HasComponent<PendingDisconnect>(pair.Value.Entity) &&
                    world.EntityManager.GetComponentData<PendingDisconnect>(pair.Value.Entity).Deadline > Now)
                { tickets.Revoke(pair.Key); continue; }
                if (!Live(context, pair.Key, pair.Value)) { removedPeers.Add(pair.Key); continue; }
                if (!pair.Value.Connection.IsCreated && Now >= pair.Value.Deadline)
                { FailPeer(pair.Key, NetworkFailure.ChunkBindingTimedOut); removedPeers.Add(pair.Key); }
            }
            foreach (ulong id in removedPeers) RemovePeer(id);
            int offered = 0;
            foreach (var pair in context.ServerConnections)
            {
                if (pair.Value == 0 || peers.ContainsKey(pair.Value)) continue;
                var peer = new Peer { Entity = pair.Key, Generation = SessionContext.ConnectionKey(pair.Key), Deadline = Now + timeout };
                if (!Live(context, pair.Value, peer)) continue;
                var ticket = tickets.Issue(pair.Value, peer.Generation, Now, timeout);
                peers.Add(pair.Value, peer);
                var rpc = world.EntityManager.CreateEntity(typeof(BulkConnectionOfferRpc), typeof(SendRpcCommandRequest));
                world.EntityManager.SetComponentData(rpc, new BulkConnectionOfferRpc
                {
                    PeerId = pair.Value, Generation = peer.Generation, TicketHigh = ticket.High, TicketLow = ticket.Low,
                    Port = Port, Edge = ChunkLayout.Edge, Fingerprint = registry.Fingerprint
                });
                world.EntityManager.SetComponentData(rpc, new SendRpcCommandRequest { TargetConnection = pair.Key });
                if (++offered == 16) break;
            }
        }
        private void ServerEvent(BulkDriverEvent item)
        {
            if (item.Type == BulkEventType.Connected)
            {
                if (pending.Count >= maxPending) driver.Disconnect(item.Connection);
                else pending.Add(item.Connection, Now + Math.Min(timeout, 5));
                return;
            }
            if (item.Type == BulkEventType.Disconnected)
            {
                pending.Remove(item.Connection);
                if (bound.TryGetValue(item.Connection, out ulong id))
                { FailPeer(id, NetworkFailure.ChunkChannelFailed); RemovePeer(id); }
                return;
            }
            if (!pending.TryGetValue(item.Connection, out double pendingDeadline))
            {
                if (bound.TryGetValue(item.Connection, out ulong id) && peers.TryGetValue(id, out var livePeer) && Live(Context, id, livePeer))
                {
                    try { streamingServer.Receive(id, item.Payload, Now); }
                    catch (Exception exception) when (exception is FormatException || exception is ArgumentException || exception is InvalidOperationException)
                    { FailPeer(id, NetworkFailure.ChunkChannelFailed); }
                    return;
                }
                driver.Disconnect(item.Connection); return;
            }
            if (Now >= pendingDeadline) { RejectPending(item.Connection); return; }
            BulkBindRequest request;
            try { request = BulkBindingFrames.DecodeRequest(item.Payload); }
            catch (FormatException) { RejectPending(item.Connection); return; }
            if (!peers.TryGetValue(request.PeerId, out var peer) || !Live(Context, request.PeerId, peer) ||
                Now >= peer.Deadline || request.Generation != peer.Generation ||
                !tickets.TryConsume(request.Ticket, request.PeerId, Now, out ulong generation) || generation != peer.Generation)
            { RejectPending(item.Connection); return; }
            //only possession of the live one-use ticket authorizes a failure against this peer.
            if (request.Edge != ChunkLayout.Edge || request.Fingerprint != registry.Fingerprint)
            {
                FailPeer(request.PeerId, request.Edge != ChunkLayout.Edge ? NetworkFailure.ChunkLayoutMismatch : NetworkFailure.ChunkRegistryMismatch);
                RejectPending(item.Connection); return;
            }
            pending.Remove(item.Connection); peer.Connection = item.Connection; bound.Add(item.Connection, request.PeerId);
            if (!driver.TrySend(item.Connection, BulkBindingFrames.EncodeAccepted(request.PeerId, peer.Generation)))
            { FailPeer(request.PeerId, NetworkFailure.ChunkChannelFailed); RemovePeer(request.PeerId); return; }
            if (!streamingServer.Add(request.PeerId, Now))
            { FailPeer(request.PeerId, NetworkFailure.ChunkChannelFailed); RemovePeer(request.PeerId); }
        }
        private void ClientEvent(BulkDriverEvent item)
        {
            if (item.Connection != clientConnection) { FailClient(NetworkFailure.InvalidResponse); return; }
            if (item.Type == BulkEventType.Disconnected) { FailClient(NetworkFailure.ChunkChannelFailed); return; }
            var offer = clientOffer.Value;
            if (item.Type == BulkEventType.Connected)
            {
                if (!driver.TrySend(clientConnection, BulkBindingFrames.EncodeRequest(new BulkBindRequest(offer.PeerId, offer.Generation,
                    new BulkTicket(offer.TicketHigh, offer.TicketLow), ChunkLayout.Edge, registry.Fingerprint)))) FailClient(NetworkFailure.ChunkChannelFailed);
                return;
            }
            if (State == ChunkConnectionState.Bound)
            {
                try { streamingClient.Receive(item.Payload, Now); }
                catch (Exception exception) when (exception is FormatException || exception is ArgumentException || exception is InvalidOperationException)
                { FailClient(NetworkFailure.ChunkChannelFailed); }
                return;
            }
            try
            {
                BulkBindingFrames.DecodeAccepted(item.Payload, out ulong peer, out ulong generation);
                if (peer != offer.PeerId || generation != offer.Generation) { FailClient(NetworkFailure.InvalidResponse); return; }
                State = ChunkConnectionState.Bound;
                streamingClient = new ChunkStreamingClient(store, registry, streamingOptions,
                    packet => driver.TrySend(clientConnection, packet), () => FailClient(NetworkFailure.ChunkChannelFailed), Now);
            }
            catch (FormatException) { FailClient(NetworkFailure.ChunkChannelFailed); }
        }
        private void RejectPending(NetworkConnection connection) { pending.Remove(connection); driver.Disconnect(connection); }
        private void FailPeer(ulong id, NetworkFailure reason)
        { tickets.Revoke(id); if (peers.TryGetValue(id, out var peer)) NetCodeSession.QueueClose(world.EntityManager, peer.Entity, reason); }
        private void RemovePeer(ulong id)
        {
            tickets.Revoke(id); streamingServer?.Remove(id);
            if (!peers.TryGetValue(id, out var peer)) return;
            if (peer.Connection.IsCreated) { bound.Remove(peer.Connection); driver.Disconnect(peer.Connection); }
            peers.Remove(id);
        }
        private void FailClient(NetworkFailure reason)
        {
            if (Failure == NetworkFailure.None) Failure = reason;
            Context?.Fail(Failure); Dispose(); State = ChunkConnectionState.Faulted;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; streamingClient?.Dispose(); streamingServer?.Dispose(); driver?.Dispose(); tickets?.Clear(); peers.Clear(); bound.Clear(); pending.Clear();
            if (State != ChunkConnectionState.Faulted) State = ChunkConnectionState.Stopped;
        }
    }
}
