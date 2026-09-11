using DigBlocks.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// How a dynamic entity's position travels over the wire: an integer origin fixed for the life of
    /// the ghost, plus an offset from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious encoding, replicating <see cref="WorldPosition"/>'s sector and offset directly,
    /// tears. NetCode interpolates each ghost field on its own, so the offset would be lerped across
    /// the wrap while the sector snapped, and every sector crossing would throw the entity a sector's
    /// width and back within one tick.
    /// </para>
    /// <para>
    /// Fixing that by interpolating the reconstructed position needs a custom ghost field template,
    /// and registering one requires compiling into the Unity.NetCode assembly, which Unity will not
    /// rebuild while the package is immutable in the package cache.
    /// </para>
    /// <para>
    /// Pinning the origin at spawn avoids the problem rather than patching it. The origin never
    /// changes, so there is nothing to tear against, and the offset is continuous everywhere
    /// including across sector boundaries. The cost is that precision now decays with distance
    /// travelled from the spawn point rather than from the world origin: sub-millimetre for the first
    /// few thousand blocks, a centimetre at a hundred thousand. Entities are stored and reloaded with
    /// their chunk, and a reloaded entity is a new ghost with a fresh origin, so nothing accumulates
    /// error indefinitely. Deliberately no automatic re-origining: it would reintroduce exactly the
    /// discontinuity this avoids, and graceful decay beats a jump.
    /// </para>
    /// </remarks>
    public struct ReplicatedPosition : IComponentData
    {
        //never changes after spawn, so its lack of interpolation cannot be seen.
        [GhostField] public int3 OriginSector;

        //millimetre precision, and continuous across every sector boundary the entity crosses.
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Offset;
    }

    /// <summary>Publishes the authoritative position into its replicated form once per tick.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GhostSendSystem))]
    [BurstCompile]
    public partial struct ServerPositionPublishSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new PublishJob().ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    internal partial struct PublishJob : IJobEntity
    {
        private void Execute(in WorldPosition position, ref ReplicatedPosition replicated) =>
            replicated.Offset = SectorGrid.Delta(new WorldPosition(replicated.OriginSector, float3.zero), position);
    }

    /// <summary>
    /// Rebuilds the authoritative position on the client from what arrived. Runs after NetCode has
    /// applied snapshots and before transforms derive anything from it.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSimulationSystemGroup))]
    [BurstCompile]
    public partial struct ClientPositionApplySystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new ApplyJob().ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    internal partial struct ApplyJob : IJobEntity
    {
        private void Execute(in ReplicatedPosition replicated, ref WorldPosition position) =>
            position = SectorGrid.Offset(new WorldPosition(replicated.OriginSector, float3.zero), replicated.Offset);
    }
}
