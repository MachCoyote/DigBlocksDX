using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Puts a connection in game when it asks, provided it actually finished admission. A client
    /// cannot talk its way past the registry with this: an unadmitted or stopping connection is
    /// ignored, exactly as <see cref="ServerHelloReceiveSystem"/> ignores client-issued control RPCs.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerHelloReceiveSystem))]
    public partial class ServerEnterGameSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<SessionActive>();

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (request, entity) in SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>>().WithAll<EnterGameRpc>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                if (context == null || context.Stopping) continue;
                Entity connection = request.ValueRO.SourceConnection;
                if (!context.ServerConnections.ContainsKey(connection)) continue;
                if (!EntityManager.HasComponent<NetworkId>(connection)) continue;
                if (EntityManager.HasComponent<PendingDisconnect>(connection)) continue;
                //repeat requests are not a protocol error; a client may ask again after a hiccup.
                if (EntityManager.HasComponent<NetworkStreamInGame>(connection)) continue;
                commands.AddComponent<NetworkStreamInGame>(connection);
                context.Logger.Log($"Peer {context.ServerConnections[connection]} entered the game.");
            }
            commands.Playback(EntityManager);
        }
    }
}
