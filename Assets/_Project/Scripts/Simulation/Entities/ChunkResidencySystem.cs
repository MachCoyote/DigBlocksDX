using DigBlocks.Voxels;
using Unity.Burst;
using Unity.Entities;

namespace DigBlocks.Simulation
{
    /// <summary>The world whose chunks entity residency is measured against.</summary>
    public struct SimulationWorldId : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// Keeps every entity's <see cref="ChunkResidency"/> in step with where it actually is. This is
    /// what lets entities save with the right chunk and be relevant to the right peers.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    //after everything that moves an entity, so residency names the chunk an entity is in rather than
    //the one it was in. Residency decides which chunk an entity is filed under when it is put away,
    //and so which chunk brings it back; a stale one files it somewhere the player has to walk to.
    [UpdateAfter(typeof(EntityBehaviorSystemGroup))]
    [BurstCompile]
    public partial struct ChunkResidencySystem : ISystem
    {
        public void OnCreate(ref SystemState state) => state.RequireForUpdate<SimulationWorldId>();

        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new ResidencyJob { World = SystemAPI.GetSingleton<SimulationWorldId>().Value }
                .ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    internal partial struct ResidencyJob : IJobEntity
    {
        public uint World;

        private void Execute(in WorldPosition position, ref ChunkResidency residency)
        {
            var address = SectorGrid.ChunkOf(World, position);
            //a stationary entity writes nothing, which keeps the change filter downstream meaningful:
            //relevancy only has to be recomputed for entities that actually moved between chunks.
            if (!residency.Address.Equals(address)) residency.Address = address;
        }
    }
}
