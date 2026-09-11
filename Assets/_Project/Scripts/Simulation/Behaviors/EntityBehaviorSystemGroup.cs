using Unity.Entities;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// Everything that moves an entity of its own accord.
    /// </summary>
    /// <remarks>
    /// The group exists so that what an entity's position is, and what chunk it is therefore in, can
    /// be settled in that order every tick. <see cref="ChunkResidencySystem"/> runs after this group,
    /// and residency is what decides which chunk an entity is saved with, which chunk it is looked
    /// for in when that chunk loads again, and which peers its ghost is relevant to. A behaviour that
    /// moved an entity after residency had been derived would leave all three naming the chunk the
    /// entity was in a tick ago, which is a different chunk exactly when it matters most.
    /// <para>
    /// Server-authoritative: the client sees only the positions these produce, interpolated like any
    /// other ghost's.
    /// </para>
    /// </remarks>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class EntityBehaviorSystemGroup : ComponentSystemGroup { }
}
