using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.ChunkProtocol;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Session;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using Unity.Entities;

namespace DigBlocks.Networking.NetCode
{
    public enum ChunkConnectionState { Stopped, AwaitingOffer, Binding, Bound, Listening, Faulted }

    public sealed class ChunkCompanionService : IGameService, ISessionReadinessSource
    {
        private readonly NetCodeSession session;
        private readonly ushort? bulkPort;
        private readonly BlockRegistry registry;
        private readonly double bindingTimeout;
        private readonly int maxPending;
        private ChunkWorldSystem serverStore, clientStore;
        private BulkCompanionSystem serverSystem, clientSystem;
        private BulkCompanionEndpoint serverEndpoint, clientEndpoint;
        private readonly ChunkStreamingOptions streamingOptions;
        private readonly IAuthoritativeChunkSource authoritativeSource;
        private bool started, stopped;
        private NetworkFailure startupFailure;

        public ChunkCompanionService(NetCodeSession session, ushort? bulkPort = null, BlockRegistry registry = null,
            double bindingTimeoutSeconds = 10, int maxPendingBindings = 16, ChunkStreamingOptions streamingOptions = null,
            IAuthoritativeChunkSource authoritativeSource = null)
        {
            this.streamingOptions = streamingOptions ?? new ChunkStreamingOptions();
            this.authoritativeSource = authoritativeSource;
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            if (!(bindingTimeoutSeconds > 0 && bindingTimeoutSeconds <= 120)) throw new ArgumentOutOfRangeException(nameof(bindingTimeoutSeconds));
            if (maxPendingBindings < 1 || maxPendingBindings > 128) throw new ArgumentOutOfRangeException(nameof(maxPendingBindings));
            if (bulkPort.HasValue && session.Role != NetworkSessionRole.Server) throw new ArgumentException("Only a remote server chooses a bulk port.", nameof(bulkPort));
            this.bulkPort = bulkPort; this.registry = registry ?? BlockRegistry.CreateDummy(); bindingTimeout = bindingTimeoutSeconds; maxPending = maxPendingBindings;
        }
        //residency follows the authored interest rather than a fixed number, with headroom so peers at
        //different anchors, and the moment an anchor moves, do not exhaust the store.
        internal static int Residency(ChunkStreamingOptions options)
        {
            long chunks = ChunkInterest.CountFor(options.HorizontalRadius, options.VerticalRadius);
            return (int)Math.Clamp(chunks + chunks / 2 + 16, 1, 65536);
        }

        /// <summary>
        /// Single player only, and off unless something sets it. Consulted per chunk, so flipping it
        /// changes how the next chunk arrives. The wire path stays the default deliberately: it is the
        /// one multiplayer depends on, and ordinary play is what exercises it.
        /// </summary>
        public Func<bool> DirectChunkDelivery { get; set; }

        public long DirectChunkDeliveries => serverEndpoint?.DirectChunkDeliveries ?? 0;

        private bool DirectDeliveryEnabled() =>
            session.Role == NetworkSessionRole.ClientAndServer && (DirectChunkDelivery?.Invoke() ?? false);

        //the bridge has to live here because this is the only place holding both stores; the server's
        //endpoint knows only its own world.
        private bool DeliverDirect(ulong peerId, ulong epoch, ChunkAddress address, ulong incarnation,
            ulong revision, PackedChannelData solids, PackedChannelData fluids)
        {
            var replica = clientStore?.Store;
            if (replica == null || replica.IsDisposed) return false;
            return replica.PublishReplica(epoch, address, incarnation, revision, solids, fluids);
        }

        public bool ClientDataReady => clientEndpoint?.DataReady ?? false;
        public long SentChunkBytes => serverEndpoint?.SentChunkBytes ?? 0;
        public long AppliedChunkAcknowledgements => serverEndpoint?.AppliedChunkAcknowledgements ?? 0;
        public long SentChunkSnapshots => serverEndpoint?.SentChunkSnapshots ?? 0;
        public long SentChunkDeltas => serverEndpoint?.SentChunkDeltas ?? 0;
        public double MaxAppliedAckSeconds => serverEndpoint?.MaxAppliedAckSeconds ?? 0;
        public int PeakEncodedPayloadBytes => serverEndpoint?.PeakEncodedPayloadBytes ?? 0;
        public int PendingChunkPayloads => serverEndpoint?.PendingChunkPayloads ?? 0;
        public bool SetServerInterest(ulong peerId, ChunkAddress anchor, int horizontalRadius, int verticalRadius) =>
            serverEndpoint?.SetInterest(peerId, anchor, horizontalRadius, verticalRadius) ?? false;
        public bool RequestClientInterest(ChunkAddress anchor) => clientEndpoint?.RequestInterest(anchor) ?? false;
        public uint WorldId => streamingOptions.WorldId;
        public string Name => nameof(ChunkCompanionService);
        public ushort ListeningPort => serverEndpoint?.Port ?? 0;
        public int BoundPeerCount => serverEndpoint?.BoundPeerCount ?? 0;
        public int PendingBindingCount => serverEndpoint?.PendingBindingCount ?? 0;
        public ChunkConnectionState ClientState => startupFailure != NetworkFailure.None ? ChunkConnectionState.Faulted : clientEndpoint?.State ?? ChunkConnectionState.Stopped;
        public NetworkFailure LastFailure => startupFailure != NetworkFailure.None ? startupFailure : clientEndpoint?.Failure ?? NetworkFailure.None;

        public string ReadinessDescription => "Binding the world data channel...";

        //the explicit world-ready gate the session layer waits on. Startup already blocks until the
        //client bulk endpoint is bound, so this verifies the world data channel instead of inferring
        //readiness from the game connection. Tighten this to ClientDataReady once gameplay drives
        //server chunk interest, so a session is only playable with streamed chunks in hand.
        public UniTask WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LastFailure != NetworkFailure.None) throw new NetworkSessionException(LastFailure);
            if (session.Role != NetworkSessionRole.Server && ClientState != ChunkConnectionState.Bound)
                throw new NetworkSessionException(NetworkFailure.ChunkChannelFailed);
            return UniTask.CompletedTask;
        }

        public async UniTask StartAsync(CancellationToken token)
        {
            if (started || stopped) throw new InvalidOperationException("Companion services are one-shot.");
            started = true;
            try
            {
                token.ThrowIfCancellationRequested();
                bool ipc = session.Role == NetworkSessionRole.ClientAndServer;
                if (session.Role != NetworkSessionRole.Client)
                {
                    var world = session.ServerWorld;
                    if (world is not { IsCreated: true }) throw new InvalidOperationException("Server world is not available.");
                    var owner = world.GetOrCreateSystemManaged<ChunkWorldSystem>();
                    owner.Configure(registry, Residency(streamingOptions)); serverStore = owner;
                    ushort port = 0;
                    if (!ipc)
                    {
                        if (!bulkPort.HasValue && session.ListeningPort == ushort.MaxValue) throw new ArgumentException("Game port 65535 requires an explicit bulk port.");
                        port = bulkPort ?? (session.ListeningPort == 0 ? (ushort)0 : (ushort)(session.ListeningPort + 1));
                        if (port != 0 && port == session.ListeningPort) throw new ArgumentException("Game and bulk UDP ports must differ.");
                    }
                    serverSystem = world.GetOrCreateSystemManaged<BulkCompanionSystem>();
                    if (serverSystem.Endpoint != null) throw new InvalidOperationException("World already has a companion endpoint.");
                    try
                    {
                        serverEndpoint = new BulkCompanionEndpoint(world, true, ipc, port, registry, bindingTimeout, maxPending,
                            streamingOptions, authoritativeSource,
                            ipc ? DirectDeliveryEnabled : null, ipc ? DeliverDirect : null);
                    }
                    catch (InvalidOperationException) { throw new NetworkSessionException(NetworkFailure.ListenFailed); }
                    serverSystem.Endpoint = serverEndpoint;
                }
                if (session.Role != NetworkSessionRole.Server)
                {
                    var world = session.ClientWorld;
                    if (world is not { IsCreated: true }) throw new InvalidOperationException("Client world is not available.");
                    var owner = world.GetOrCreateSystemManaged<ChunkWorldSystem>();
                    owner.Configure(registry, Residency(streamingOptions)).EnableReplicas(); clientStore = owner;
                    clientSystem = world.GetOrCreateSystemManaged<BulkCompanionSystem>();
                    if (clientSystem.Endpoint != null) throw new InvalidOperationException("World already has a companion endpoint.");
                    clientEndpoint = new BulkCompanionEndpoint(world, false, ipc, 0, registry, bindingTimeout, maxPending, streamingOptions);
                    clientSystem.Endpoint = clientEndpoint;
                    while (clientEndpoint.State != ChunkConnectionState.Bound)
                    {
                        token.ThrowIfCancellationRequested();
                        if (stopped) throw new OperationCanceledException("Companion stopped during startup.");
                        if (clientEndpoint.Failure != NetworkFailure.None) throw new NetworkSessionException(clientEndpoint.Failure);
                        if (session.ClientWorld is not { IsCreated: true } || clientEndpoint.State == ChunkConnectionState.Stopped)
                            throw new NetworkSessionException(NetworkFailure.ChunkChannelFailed);
                        await UniTask.Yield();
                    }
                }
                token.ThrowIfCancellationRequested();
                if (stopped) throw new OperationCanceledException("Companion stopped during startup.");
            }
            catch (Exception exception)
            {
                startupFailure = exception is NetworkSessionException network ? network.Reason : exception is OperationCanceledException ? NetworkFailure.Cancelled : NetworkFailure.ChunkChannelFailed;
                await StopAsync(CancellationToken.None);
                throw;
            }
        }
        public UniTask StopAsync(CancellationToken token)
        {
            if (stopped) return UniTask.CompletedTask;
            stopped = true;
            clientEndpoint?.Dispose(); serverEndpoint?.Dispose();
            if (session.ClientWorld is { IsCreated: true }) { clientSystem?.ReleaseEndpoint(); clientStore?.ReleaseStore(); }
            if (session.ServerWorld is { IsCreated: true }) { serverSystem?.ReleaseEndpoint(); serverStore?.ReleaseStore(); }
            return UniTask.CompletedTask;
        }
    }
}
