using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ClientHelloSendSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<SessionActive>();

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            if (context.Stopping || context.HelloSent || context.Failure != NetworkFailure.None) return;
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (connection, entity) in SystemAPI.Query<RefRO<NetworkStreamConnection>>().WithEntityAccess())
            {
                if (connection.ValueRO.CurrentState != ConnectionState.State.Approval) continue;
                context.ClientConnection = entity;
                context.HelloSent = true;
                context.State = NetworkSessionState.Approving;
                var rpc = commands.CreateEntity();
                commands.AddComponent(rpc, new ClientHelloRpc
                {
                    ProtocolVersion = context.Options.ProtocolVersion,
                    Nonce = context.Nonce,
                    OfflineXuid = context.OfflineXuid,
                    DisplayName = new FixedString32Bytes(context.Options.DisplayName)
                });
                commands.AddComponent(rpc, new SendRpcCommandRequest { TargetConnection = entity });
                break;
            }
            commands.Playback(EntityManager);
        }
    }
}
