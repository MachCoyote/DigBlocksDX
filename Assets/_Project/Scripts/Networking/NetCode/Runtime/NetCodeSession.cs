using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using UnityEngine;

namespace DigBlocks.Networking.NetCode
{
    //one session per pair of worlds. Rejoining creates a new host, session and worlds.
    public sealed class NetCodeSession : INetworkSession
    {
        private readonly Func<World> getClientWorld;
        private readonly Func<World> getServerWorld;
        private readonly Func<ulong> loadIdentity;
        private readonly IGameLogger logger;
        private readonly NetworkSessionOptions options;
        private SessionContext client;
        private SessionContext server;
        private NetworkSessionState lifecycle;
        private NetworkFailure failure;
        private bool started;
        private bool stopping;
        private UniTaskCompletionSource stopCompletion;

        public NetCodeSession(NetworkSessionRole role, NetworkSessionOptions options, Func<World> getClientWorld,
            Func<World> getServerWorld, IGameLogger logger, Func<ulong> loadIdentity = null)
        {
            if (!Enum.IsDefined(typeof(NetworkSessionRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
            if (options.ProtocolVersion == 0) throw new ArgumentException("Construct valid session options.", nameof(options));
            if (role != NetworkSessionRole.Server && getClientWorld == null) throw new ArgumentNullException(nameof(getClientWorld));
            if (role != NetworkSessionRole.Client && getServerWorld == null) throw new ArgumentNullException(nameof(getServerWorld));
            if (role == NetworkSessionRole.Client && options.Port == 0) throw new ArgumentException("Remote port cannot be zero.", nameof(options));
            Role = role;
            this.options = options;
            this.getClientWorld = getClientWorld;
            this.getServerWorld = getServerWorld;
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger))).CreateFor(nameof(NetCodeSession));
            this.loadIdentity = loadIdentity ?? (() => OfflineIdentityStore.LoadOrCreate(Path.Combine(Application.persistentDataPath, "uid.dat")));
        }

        internal World ClientWorld => client == null ? null : getClientWorld?.Invoke();
        internal World ServerWorld => server == null ? null : getServerWorld?.Invoke();
        internal NetworkSessionOptions Options => options;
        public string Name => nameof(NetCodeSession);
        public NetworkSessionRole Role { get; }
        public NetworkSessionState State => lifecycle is NetworkSessionState.Stopped or NetworkSessionState.Stopping or NetworkSessionState.Faulted
            ? lifecycle : client?.State ?? lifecycle;
        public NetworkFailure LastFailure => failure != NetworkFailure.None ? failure : client?.Failure ?? NetworkFailure.None;
        public string LastTransportDisconnectReason => client?.TransportDisconnectReason;
        public IReadOnlyList<PeerRecord> Peers => server?.Registry.Peers ?? Array.Empty<PeerRecord>();
        public ulong LocalPeerId => client?.PeerId ?? 0;
        public ushort ListeningPort { get; private set; }

        public async UniTask StartAsync(CancellationToken cancellationToken)
        {
            if (started || stopping) throw new InvalidOperationException("Sessions are one-shot; compose a new host to rejoin.");
            started = true;
            lifecycle = NetworkSessionState.Starting;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Role != NetworkSessionRole.Client) StartServer();
                if (Role != NetworkSessionRole.Server)
                {
                    StartClient();
                    double deadline = Time.realtimeSinceStartupAsDouble + options.StartupTimeoutSeconds;
                    //admission completes at AwaitingWorldData, but the world-data gate can carry the
                    //client on to InGame in the same frame, so accept either as "admitted".
                    while (client.State is not (NetworkSessionState.AwaitingWorldData or NetworkSessionState.InGame))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stopping) throw new OperationCanceledException("Session stopped during startup.");
                        if (client.Failure != NetworkFailure.None) throw new NetworkSessionException(client.Failure);
                        if (Time.realtimeSinceStartupAsDouble >= deadline) throw new NetworkSessionException(NetworkFailure.TimedOut);
                        await UniTask.Yield(PlayerLoopTiming.Update);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopping) throw new OperationCanceledException("Session stopped during startup.");
                }
            }
            catch (Exception exception)
            {
                failure = exception is NetworkSessionException network ? network.Reason : exception is OperationCanceledException ? NetworkFailure.Cancelled : NetworkFailure.TransportClosed;
                //the host rolls back previously started services, not this partially started one.
                await StopAsync(CancellationToken.None);
                lifecycle = NetworkSessionState.Faulted;
                throw;
            }
        }

        private void StartServer()
        {
            var world = RequireWorld(getServerWorld, "server");
            server = Install(world);
            server.Registry = new AdmissionRegistry(options);
            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            var driver = query.GetSingletonRW<NetworkStreamDriver>();
            var endpoint = Role == NetworkSessionRole.ClientAndServer
                ? NetworkEndpoint.LoopbackIpv4.WithPort(0)
                : NetworkEndpoint.Parse(options.BindAddress, options.Port);
            if (!driver.ValueRW.Listen(endpoint)) throw new NetworkSessionException(NetworkFailure.ListenFailed);
            ListeningPort = driver.ValueRO.GetLocalEndPoint().Port;
            lifecycle = NetworkSessionState.Listening;
            server.State = lifecycle;
            logger.Log($"Listening on {driver.ValueRO.GetLocalEndPoint()}; capacity {options.Capacity}.");
        }

        private void StartClient()
        {
            var world = RequireWorld(getClientWorld, "client");
            ulong id = loadIdentity();
            if (id == 0) throw new NetworkSessionException(NetworkFailure.InvalidIdentity);
            client = Install(world);
            client.OfflineXuid = id;
            client.Nonce = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            client.State = NetworkSessionState.Connecting;
            lifecycle = NetworkSessionState.Connecting;
            Entity request = world.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
            world.EntityManager.SetComponentData(request, new NetworkStreamRequestConnect
            {
                Endpoint = Role == NetworkSessionRole.ClientAndServer
                    ? NetworkEndpoint.LoopbackIpv4.WithPort(ListeningPort)
                    : NetworkEndpoint.Parse(options.Address, options.Port)
            });
            logger.Log($"Connecting as {options.DisplayName} (offline, unauthenticated).");
        }

        private SessionContext Install(World world)
        {
            var owner = world.GetOrCreateSystemManaged<SessionContextSystem>();
            if (owner.Context != null) throw new InvalidOperationException("World already belongs to a session.");
            using var drivers = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamDriver));
            drivers.GetSingletonRW<NetworkStreamDriver>().ValueRW.RequireConnectionApproval = true;
            var context = new SessionContext { Options = options, Logger = logger };
            owner.Context = context;
            world.EntityManager.CreateSingleton<SessionActive>();
            return context;
        }

        public bool Kick(ulong peerId)
        {
            if (server == null || server.Stopping || peerId == 0) return false;
            var world = RequireWorld(getServerWorld, "server");
            foreach (var pair in server.ServerConnections)
            {
                if (pair.Value != peerId) continue;
                QueueClose(world.EntityManager, pair.Key, NetworkFailure.Kicked);
                return true;
            }
            return false;
        }

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (!stopping)
            {
                stopping = true;
                stopCompletion = new UniTaskCompletionSource();
                CompleteStopAsync().Forget();
            }
            //cleanup has its own deadline and must still happen when the caller is cancelled.
            return stopCompletion.Task;
        }

        private async UniTask CompleteStopAsync()
        {
            //unlike Preserve, a completion source supports concurrent in-flight stop awaiters.
            try { await StopCoreAsync(); stopCompletion.TrySetResult(); }
            catch (Exception exception) { stopCompletion.TrySetException(exception); }
        }

        private async UniTask StopCoreAsync()
        {
            lifecycle = NetworkSessionState.Stopping;
            if (client != null) client.Stopping = true;
            if (server != null) server.Stopping = true;
            World clientWorld = client == null ? null : getClientWorld?.Invoke();
            World serverWorld = server == null ? null : getServerWorld?.Invoke();
            double deadline = Time.realtimeSinceStartupAsDouble + options.ShutdownTimeoutSeconds;
            if (serverWorld is { IsCreated: true })
                foreach (var pair in server.ServerConnections) QueueClose(serverWorld.EntityManager, pair.Key, NetworkFailure.ServerStopping);
            RequestDisconnects(clientWorld);
            do
            {
                //also catches late transport accepts while the server world is draining.
                RequestDisconnects(serverWorld, respectGrace: true);
                if (!HasConnections(clientWorld) && !HasConnections(serverWorld)) break;
                await UniTask.Yield(PlayerLoopTiming.Update);
            } while (Time.realtimeSinceStartupAsDouble < deadline);

            RequestDisconnects(serverWorld);
            RemoveContext(clientWorld);
            RemoveContext(serverWorld);
            server?.Registry.Clear();
            server?.ServerConnections.Clear();
            if (client != null) { client.Accepted = false; client.PeerId = 0; }
            lifecycle = NetworkSessionState.Stopped;
            //runtime services dispose the drivers/worlds next, releasing listening sockets as well.
        }

        internal static void QueueClose(EntityManager manager, Entity connection, NetworkFailure reason)
        {
            if (!manager.HasComponent<NetworkStreamConnection>(connection) || manager.HasComponent<PendingDisconnect>(connection)) return;
            var rpc = manager.CreateEntity(typeof(SessionCloseRpc), typeof(SendRpcCommandRequest));
            manager.SetComponentData(rpc, new SessionCloseRpc { Reason = reason });
            manager.SetComponentData(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            manager.AddComponentData(connection, new PendingDisconnect { Reason = reason, Deadline = Time.realtimeSinceStartupAsDouble + 0.25 });
        }

        private static void RequestDisconnects(World world, bool respectGrace = false)
        {
            if (world is not { IsCreated: true }) return;
            var manager = world.EntityManager;
            using var query = manager.CreateEntityQuery(typeof(NetworkStreamConnection));
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                if (manager.HasComponent<NetworkStreamRequestDisconnect>(entity)) continue;
                if (respectGrace && manager.HasComponent<PendingDisconnect>(entity) && manager.GetComponentData<PendingDisconnect>(entity).Deadline > Time.realtimeSinceStartupAsDouble) continue;
                manager.AddComponentData(entity, new NetworkStreamRequestDisconnect());
            }
            using var requests = manager.CreateEntityQuery(typeof(NetworkStreamRequestConnect));
            manager.DestroyEntity(requests);
        }

        private static bool HasConnections(World world)
        {
            if (world is not { IsCreated: true }) return false;
            using var query = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
            return !query.IsEmptyIgnoreFilter;
        }

        private static void RemoveContext(World world)
        {
            if (world is not { IsCreated: true }) return;
            world.GetExistingSystemManaged<SessionContextSystem>().Context = null;
            using var contexts = world.EntityManager.CreateEntityQuery(typeof(SessionActive));
            world.EntityManager.DestroyEntity(contexts);
            using var ready = world.EntityManager.CreateEntityQuery(typeof(NetworkSessionReady));
            world.EntityManager.DestroyEntity(ready);
            using var worldData = world.EntityManager.CreateEntityQuery(typeof(WorldDataReady));
            world.EntityManager.DestroyEntity(worldData);
        }

        private static World RequireWorld(Func<World> getWorld, string role)
        {
            World world = getWorld?.Invoke();
            return world is { IsCreated: true } ? world : throw new InvalidOperationException($"The {role} world is unavailable.");
        }
    }
}
