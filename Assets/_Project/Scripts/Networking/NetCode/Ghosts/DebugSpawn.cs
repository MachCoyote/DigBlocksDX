using DigBlocks.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Present in the server world only when this session is allowed to honour client-issued debug
    /// spawns. A client that can conjure entities on a real server is a plain exploit, so the gate
    /// lives here rather than on whether the client happens to show a debug menu.
    /// </summary>
    public struct DebugSpawnPermitted : IComponentData { }

    /// <summary>Asks the server to spawn one entity of a named type. Debug affordance, not gameplay.</summary>
    public struct SpawnDebugEntityRpc : IRpcCommand
    {
        public FixedString64Bytes TypeKey;
        public int3 Sector;
        public float3 Local;
        /// <summary>Circle radius, for a type that flies one. Zero takes the default.</summary>
        public float Radius;
        /// <summary>Radians per second. Zero takes the default.</summary>
        public float AngularSpeed;
        public float Height;
    }

    /// <summary>Spawning entities from their registered ghost prefabs.</summary>
    public static class EntitySpawn
    {
        public const float DefaultCircleRadius = 8f;
        public const float DefaultCircleAngularSpeed = 1f;

        private static uint WorldIdOf(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<SimulationWorldId>());
            return query.IsEmptyIgnoreFilter ? 1u : query.GetSingleton<SimulationWorldId>().Value;
        }

        /// <summary>
        /// Instantiates a type's ghost prefab at a position. Pins the replication origin to where the
        /// entity appears, which is what keeps the replicated offset small and continuous.
        /// </summary>
        public static Entity Spawn(EntityManager manager, EntityGhostPrefabSystem prefabs, ushort typeId,
            WorldPosition position, float radius = 0f, float angularSpeed = 0f, float height = 0f)
        {
            if (prefabs == null || !prefabs.TryGetPrefab(typeId, out Entity prefab)) return Entity.Null;
            position = SectorGrid.Normalize(position);

            Entity entity = manager.Instantiate(prefab);
            manager.SetComponentData(entity, position);
            manager.SetComponentData(entity, new ReplicatedPosition { Blocks = SectorGrid.ToBlocks(position) });
            //residency is set here rather than waiting for ChunkResidencySystem, because an entity
            //with a default residency reads as being in world zero, which no interest covers, and
            //would be swept straight back into the chunk store before it ever ticked.
            manager.SetComponentData(entity, new ChunkResidency { Address = SectorGrid.ChunkOf(WorldIdOf(manager), position) });

            var definition = prefabs.Registry[typeId];
            if (definition.HasBehavior(SimulationBehaviors.CircleFlight))
                manager.AddComponentData(entity, new CircleFlight
                {
                    Center = position,
                    Radius = radius > 0f ? radius : DefaultCircleRadius,
                    AngularSpeed = angularSpeed != 0f ? angularSpeed : DefaultCircleAngularSpeed,
                    Height = height
                });
            return entity;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(EntityGhostPrefabSystem))]
    public partial class ServerDebugSpawnSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<SessionActive>();

        protected override void OnUpdate()
        {
            var context = World.GetExistingSystemManaged<SessionContextSystem>().Context;
            var prefabs = World.GetExistingSystemManaged<EntityGhostPrefabSystem>();
            bool permitted = SystemAPI.HasSingleton<DebugSpawnPermitted>();

            using var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (request, spawn, entity) in
                SystemAPI.Query<RefRO<ReceiveRpcCommandRequest>, RefRO<SpawnDebugEntityRpc>>().WithEntityAccess())
            {
                commands.DestroyEntity(entity);
                if (context == null || context.Stopping) continue;
                //the same admission check the in-game gate applies: an unadmitted connection is not
                //a peer, whatever it sends.
                if (!context.ServerConnections.ContainsKey(request.ValueRO.SourceConnection)) continue;
                if (!permitted)
                {
                    context.Logger.Log("Refused a debug spawn: this session does not permit them.", Core.Hosting.GameLogLevel.Warning);
                    continue;
                }
                if (prefabs?.Registry == null || !prefabs.Built) continue;

                var command = spawn.ValueRO;
                if (!prefabs.Registry.TryGetId(command.TypeKey.ToString(), out ushort typeId))
                {
                    context.Logger.Log($"Refused a debug spawn: unknown entity type '{command.TypeKey}'.", Core.Hosting.GameLogLevel.Warning);
                    continue;
                }

                //played back immediately rather than through the buffer, because the spawn helper
                //reads the prefab's registry entry and adds behaviour components as it goes.
                Entity spawned = EntitySpawn.Spawn(EntityManager, prefabs, typeId,
                    new WorldPosition(command.Sector, command.Local), command.Radius, command.AngularSpeed, command.Height);
                if (spawned != Entity.Null)
                    context.Logger.Log($"Spawned {command.TypeKey} for peer {context.ServerConnections[request.ValueRO.SourceConnection]}.");
            }
            commands.Playback(EntityManager);
        }
    }
}
