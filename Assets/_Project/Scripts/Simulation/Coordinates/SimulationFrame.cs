using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// The integer origin every <see cref="LocalTransform"/> in this world is expressed against.
    /// Physics and rendering only ever see offsets from it, so their float inputs stay small however
    /// far from the world origin the frame sits.
    /// </summary>
    //there is exactly one frame for now, pinned at the origin. Several frames, entity migration
    //between them and a physics world per frame arrive when something can travel far enough to need
    //them; the contract that makes that additive is that LocalTransform is derived, never authored.
    public struct SimulationFrame : IComponentData
    {
        public int3 Origin;
    }

    /// <summary>
    /// Derives every entity's frame-local <see cref="LocalTransform"/> from its authoritative
    /// <see cref="WorldPosition"/>. Nothing else may write the position of a world-positioned entity.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    public partial struct FrameTransformSystem : ISystem
    {
        //not Burst compiled: creating the singleton is a structural change.
        public void OnCreate(ref SystemState state)
        {
            state.EntityManager.CreateSingleton(new SimulationFrame { Origin = int3.zero });
            state.RequireForUpdate<SimulationFrame>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new DeriveFrameTransformJob { Origin = SystemAPI.GetSingleton<SimulationFrame>().Origin }
                .ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    internal partial struct DeriveFrameTransformJob : IJobEntity
    {
        public int3 Origin;

        private void Execute(in WorldPosition position, ref LocalTransform transform) =>
            transform.Position = SectorGrid.ToFrame(position, Origin);
    }
}
