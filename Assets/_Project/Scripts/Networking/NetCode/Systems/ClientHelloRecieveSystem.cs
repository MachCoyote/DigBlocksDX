using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(ClientHelloSendSystem))]
    public partial class ClientHelloReceiveSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SessionActive>();
            RequireForUpdate<NetworkStreamDriver>();
        }

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (hello, request, entity) in SystemAPI.Query<RefRO<ServerHelloRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                if (context.Stopping || context.Accepted || context.Failure != NetworkFailure.None || request.ValueRO.SourceConnection != context.ClientConnection) continue;
                var response = hello.ValueRO;
                if (response.Nonce != context.Nonce) context.Fail(NetworkFailure.InvalidResponse);
                else if (response.Failure != NetworkFailure.None) context.Fail(response.Failure);
                else if (response.ProtocolVersion != context.Options.ProtocolVersion) context.Fail(NetworkFailure.ProtocolMismatch);
                else if (response.PeerId == 0 || response.OfflineXuid != context.OfflineXuid) context.Fail(NetworkFailure.InvalidResponse);
                else { context.Accepted = true; context.PeerId = response.PeerId; }
            }

            foreach (var (close, request, entity) in SystemAPI.Query<RefRO<SessionCloseRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                if (request.ValueRO.SourceConnection == context.ClientConnection)
                    context.Fail(close.ValueRO.Reason == NetworkFailure.None ? NetworkFailure.TransportClosed : close.ValueRO.Reason);
            }

            //events must be consumed inside simulation; Update polling can miss entire network ticks.
            foreach (var evt in SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick)
                if (evt.State == ConnectionState.State.Disconnected)
                {
                    context.TransportDisconnectReason = evt.DisconnectReason.ToString();
                    context.Fail(MapFailure(evt.DisconnectReason));
                }

            if (context.Accepted && EntityManager.HasComponent<NetworkId>(context.ClientConnection) && !context.Stopping)
            {
                if (context.State != NetworkSessionState.AwaitingWorldData)
                {
                    context.State = NetworkSessionState.AwaitingWorldData;
                    context.Logger.Log($"Admitted as peer {context.PeerId}; awaiting world data.");
                    var ready = commands.CreateEntity();
                    commands.AddComponent(ready, new NetworkSessionReady { ProtocolVersion = context.Options.ProtocolVersion, Nonce = context.Nonce });
                }
            }

            if (context.Failure != NetworkFailure.None || context.Stopping)
            {
                foreach (var (_, entity) in SystemAPI.Query<RefRO<NetworkSessionReady>>().WithEntityAccess()) commands.DestroyEntity(entity);
                if (EntityManager.HasComponent<NetworkStreamConnection>(context.ClientConnection) && !EntityManager.HasComponent<NetworkStreamRequestDisconnect>(context.ClientConnection))
                    commands.AddComponent(context.ClientConnection, new NetworkStreamRequestDisconnect());
            }
            //schema compatibility does not enforce sender roles; consume wrong-direction messages too.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ClientHelloRpc>>().WithAll<ReceiveRpcCommandRequest>().WithEntityAccess()) commands.DestroyEntity(entity);
            commands.Playback(EntityManager);
        }

        private static NetworkFailure MapFailure(NetworkStreamDisconnectReason reason) => reason switch
        {
            NetworkStreamDisconnectReason.BadProtocolVersion or NetworkStreamDisconnectReason.InvalidRpc => NetworkFailure.ProtocolMismatch,
            NetworkStreamDisconnectReason.Timeout or NetworkStreamDisconnectReason.MaxConnectionAttempts or NetworkStreamDisconnectReason.ApprovalTimeout or NetworkStreamDisconnectReason.HandshakeTimeout => NetworkFailure.TimedOut,
            NetworkStreamDisconnectReason.ApprovalFailure => NetworkFailure.Rejected,
            _ => NetworkFailure.TransportClosed
        };
    }
}
