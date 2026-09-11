using DigBlocks.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Sends a debug spawn from outside ECS, which is where a menu command lives.
    /// </summary>
    public static class DebugSpawnRequest
    {
        /// <summary>
        /// Asks the server to spawn one entity. Returns false when there is no client world in game
        /// to ask through; the server decides separately whether to honour it.
        /// </summary>
        public static bool Send(World clientWorld, string typeKey, WorldPosition position,
            float radius = 0f, float angularSpeed = 0f, float height = 0f)
        {
            if (clientWorld is not { IsCreated: true } || string.IsNullOrEmpty(typeKey)) return false;
            var manager = clientWorld.EntityManager;

            //only a connection that is in game can carry this, and there is exactly one on a client.
            using var connections = manager.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamInGame>());
            if (connections.IsEmptyIgnoreFilter) return false;

            position = SectorGrid.Normalize(position);
            Entity request = manager.CreateEntity(typeof(SpawnDebugEntityRpc), typeof(SendRpcCommandRequest));
            manager.SetComponentData(request, new SpawnDebugEntityRpc
            {
                TypeKey = new FixedString64Bytes(typeKey),
                Sector = position.Sector,
                Local = position.Local,
                Radius = radius,
                AngularSpeed = angularSpeed,
                Height = height
            });
            //a client has one connection, so the default target broadcasts to exactly the server.
            manager.SetComponentData(request, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }
    }
}
