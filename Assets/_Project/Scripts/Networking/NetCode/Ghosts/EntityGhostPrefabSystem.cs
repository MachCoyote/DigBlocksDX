using System;
using System.Collections.Generic;
using DigBlocks.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Builds one ghost prefab per registered entity type, in code, from the entity type registry.
    /// </summary>
    /// <remarks>
    /// The prefabs must come out identical on the client and the server: NetCode hashes the whole
    /// ghost collection and drops connections that disagree. That is why this walks the registry in
    /// runtime-id order, which is itself derived from sorted keys, and why nothing world-specific
    /// reaches the component set. Presentation is attached after spawn rather than baked in here,
    /// so a renderer change cannot perturb the hash.
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [CreateAfter(typeof(DefaultVariantSystemGroup))]
    public partial class EntityGhostPrefabSystem : SystemBase
    {
        private readonly Dictionary<ushort, Entity> prefabs = new Dictionary<ushort, Entity>();
        private EntityTypeRegistry registry;

        public EntityTypeRegistry Registry => registry;
        public bool Built { get; private set; }
        public int PrefabCount => prefabs.Count;

        protected override void OnCreate() => Enabled = false;

        /// <summary>Supplies the content this world replicates. Both worlds must be given the same registry.</summary>
        public void Configure(EntityTypeRegistry entityTypes)
        {
            if (registry != null) throw new InvalidOperationException("This world already has an entity type registry.");
            registry = entityTypes ?? throw new ArgumentNullException(nameof(entityTypes));
            Enabled = true;
        }

        public bool TryGetPrefab(ushort typeId, out Entity prefab) => prefabs.TryGetValue(typeId, out prefab);

        protected override void OnUpdate()
        {
            if (Built) { Enabled = false; return; }
            for (ushort id = 1; id <= registry.MaxTypeId; id++) prefabs.Add(id, Build(id, registry[id]));
            Built = true;
            Enabled = false;
        }

        private Entity Build(ushort typeId, EntityTypeDefinition definition)
        {
            var attributes = definition.Attributes;
            Entity prefab = EntityManager.CreateEntity();
            EntityManager.AddComponent<Prefab>(prefab);
            EntityManager.AddComponentData(prefab, new EntityTypeId { Value = typeId });
            EntityManager.AddComponentData(prefab, new WorldPosition());
            //how the position actually travels; the spawner pins its origin to where the entity appears.
            EntityManager.AddComponentData(prefab, new ReplicatedPosition());
            //derived every frame from WorldPosition, but it has to exist for transforms and rendering.
            EntityManager.AddComponentData(prefab, LocalTransform.Identity);
            EntityManager.AddComponentData(prefab, new LocalToWorld { Value = float4x4.identity });
            EntityManager.AddComponentData(prefab, new AabbExtents { Width = attributes.Width, Height = attributes.Height });
            EntityManager.AddComponentData(prefab, new ChunkResidency());

            //a single-entity prefab still needs its linked group, which is what the converter walks.
            var linked = EntityManager.AddBuffer<LinkedEntityGroup>(prefab);
            linked.Add(new LinkedEntityGroup { Value = prefab });

            GhostPrefabCreation.ConvertToGhostPrefab(EntityManager, prefab, new GhostPrefabCreation.Config
            {
                Name = new FixedString64Bytes(definition.Key),
                Importance = Math.Max(1, (int)attributes.GhostImportance),
                SupportedGhostModes = SupportedModesOf(attributes.GhostMode),
                DefaultGhostMode = DefaultModeOf(attributes.GhostMode),
                OptimizationMode = attributes.GhostOptimization == EntityGhostOptimization.Static
                    ? GhostOptimizationMode.Static : GhostOptimizationMode.Dynamic,
                UsePreSerialization = false
            });
            return prefab;
        }

        private static GhostModeMask SupportedModesOf(EntityGhostMode mode) => mode switch
        {
            EntityGhostMode.Predicted => GhostModeMask.Predicted,
            //owner prediction means predicted for the owner and interpolated for everyone else, so
            //the prefab has to support both rather than picking one.
            EntityGhostMode.OwnerPredicted => GhostModeMask.All,
            _ => GhostModeMask.Interpolated
        };

        private static GhostMode DefaultModeOf(EntityGhostMode mode) => mode switch
        {
            EntityGhostMode.Predicted => GhostMode.Predicted,
            EntityGhostMode.OwnerPredicted => GhostMode.OwnerPredicted,
            _ => GhostMode.Interpolated
        };
    }
}
