using DigBlocks.Simulation;
using DigBlocks.Simulation.Definitions;
using Unity.Collections;
using Unity.Entities;

namespace DigBlocks.Client.Rendering
{
    /// <summary>
    /// Gives every newly replicated entity a view, once. Runs on the client only: a dedicated server
    /// never loads this assembly, so nothing here can reach the authoritative simulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class EntityViewSystem : SystemBase
    {
        private IEntityPresentationBackend backend;
        private CompiledEntityContent content;

        public int AttachedCount { get; private set; }

        protected override void OnCreate() => Enabled = false;

        public void Configure(IEntityPresentationBackend presentationBackend, CompiledEntityContent entityContent)
        {
            backend = presentationBackend;
            content = entityContent;
            Enabled = backend != null && content != null;
        }

        protected override void OnUpdate()
        {
            //structural changes, so the query is resolved first and the components added afterwards.
            using var entities = SystemAPI.QueryBuilder()
                .WithAll<EntityTypeId>().WithNone<EntityViewAttached>().Build()
                .ToEntityArray(Allocator.Temp);
            if (entities.Length == 0) return;

            foreach (var entity in entities)
            {
                ushort typeId = EntityManager.GetComponentData<EntityTypeId>(entity).Value;
                //a type with no model draws nothing, which is a legitimate thing to be: a trigger
                //volume or a marker still replicates and simulates.
                var model = typeId != EntityTypeId.None ? content.ModelOf(typeId) : null;
                if (model != null) { backend.Attach(EntityManager, entity, model); AttachedCount++; }
                EntityManager.AddComponent<EntityViewAttached>(entity);
            }
        }

        protected override void OnDestroy()
        {
            backend?.Dispose();
            backend = null;
        }
    }
}
