using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Puts an admitted client in game once its world data has arrived. Adding
    /// <see cref="NetworkStreamInGame"/> is what lets NetCode exchange snapshots at all, so this is
    /// the gate every dynamic entity sits behind.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientHelloReceiveSystem))]
    public partial class ClientEnterGameSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<SessionActive>();
            RequireForUpdate<NetworkSessionReady>();
            RequireForUpdate<WorldDataReady>();
        }

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            if (context == null || context.Stopping || context.Failure != NetworkFailure.None) return;

            Entity connection = context.ClientConnection;
            if (!EntityManager.HasComponent<NetworkId>(connection)) return;
            if (EntityManager.HasComponent<NetworkStreamInGame>(connection)) return;

            using var commands = new EntityCommandBuffer(Allocator.Temp);
            //the client goes in game locally and asks the server to do the same. Snapshots only flow
            //once both ends agree, so the brief window where only this side is in game is harmless.
            commands.AddComponent<NetworkStreamInGame>(connection);
            var request = commands.CreateEntity();
            commands.AddComponent<EnterGameRpc>(request);
            commands.AddComponent(request, new SendRpcCommandRequest { TargetConnection = connection });
            commands.Playback(EntityManager);

            context.State = NetworkSessionState.InGame;
            context.Logger.Log($"Peer {context.PeerId} is in game; ghost replication is live.");
        }
    }
}
