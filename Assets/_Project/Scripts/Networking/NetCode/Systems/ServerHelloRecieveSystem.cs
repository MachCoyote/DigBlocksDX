using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace DigBlocks.Networking.NetCode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ServerHelloReceiveSystem : SystemBase
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
            foreach (var evt in SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick)
            {
                if (evt.State != ConnectionState.State.Disconnected) continue;
                context.Registry.Release(SessionContext.ConnectionKey(evt.ConnectionEntity));
                if (context.ServerConnections.Remove(evt.ConnectionEntity))
                    context.Logger.Log($"Peer disconnected ({evt.DisconnectReason}); {context.Registry.Peers.Count} slots occupied.");
            }

            foreach (var (hello, request, entity) in SystemAPI.Query<RefRO<ClientHelloRpc>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                Entity connection = request.ValueRO.SourceConnection;
                if (!EntityManager.HasComponent<NetworkStreamConnection>(connection) || context.ServerConnections.ContainsKey(connection) || EntityManager.HasComponent<AdmissionHandled>(connection)) continue;
                if (EntityManager.GetComponentData<NetworkStreamConnection>(connection).CurrentState != ConnectionState.State.Approval) continue;
                PeerRecord peer = default;
                var failure = context.Stopping ? NetworkFailure.ServerStopping : context.Registry.Admit(
                    SessionContext.ConnectionKey(connection), hello.ValueRO.ProtocolVersion, hello.ValueRO.OfflineXuid, hello.ValueRO.DisplayName.ToString(), out peer);
                //reserve the per-connection guard immediately, before the command buffer plays back.
                context.ServerConnections.Add(connection, peer.PeerId);
                commands.AddComponent<AdmissionHandled>(connection);
                var response = commands.CreateEntity();
                commands.AddComponent(response, new ServerHelloRpc
                {
                    ProtocolVersion = context.Options.ProtocolVersion, Nonce = hello.ValueRO.Nonce,
                    PeerId = peer.PeerId, OfflineXuid = hello.ValueRO.OfflineXuid, Failure = failure
                });
                commands.AddComponent(response, new SendRpcCommandRequest { TargetConnection = connection });
                if (failure == NetworkFailure.None)
                {
                    commands.AddComponent<ConnectionApproved>(connection);
                    context.Logger.Log($"Reserved peer {peer.PeerId} ({peer.DisplayName}); {context.Registry.Peers.Count}/{context.Options.Capacity} slots occupied.");
                }
                else
                {
                    commands.AddComponent(connection, new PendingDisconnect { Deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + 0.25, Reason = failure });
                    context.Logger.Log($"Admission rejected: {failure}.");
                }
            }

            foreach (var (pending, entity) in SystemAPI.Query<RefRO<PendingDisconnect>>().WithNone<NetworkStreamRequestDisconnect>().WithEntityAccess())
                if (UnityEngine.Time.realtimeSinceStartupAsDouble >= pending.ValueRO.Deadline)
                    commands.AddComponent(entity, new NetworkStreamRequestDisconnect { Reason = NetworkStreamDisconnectReason.ApprovalFailure });

            //server never accepts client-issued close reasons as control commands.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<SessionCloseRpc>>().WithAll<ReceiveRpcCommandRequest>().WithEntityAccess()) commands.DestroyEntity(entity);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ServerHelloRpc>>().WithAll<ReceiveRpcCommandRequest>().WithEntityAccess()) commands.DestroyEntity(entity);
            commands.Playback(EntityManager);
        }
    }
}
