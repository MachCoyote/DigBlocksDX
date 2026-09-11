using DigBlocks.Simulation;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// How a dynamic entity's position travels: absolute block coordinates, in double precision,
    /// quantized to a millimetre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A quantized <c>double</c> ghost field is stored in a <c>long</c> and delta-compressed with
    /// <c>WritePackedLongDelta</c>. That is sub-millimetre precision across a range of about nine
    /// quadrillion blocks, and the precision is the same everywhere: it does not decay with distance
    /// from the origin, from a spawn point, or from anything else.
    /// </para>
    /// <para>
    /// Simulation still keeps position as an exact sector plus a small offset, because physics and
    /// rendering need a frame-local float and the sector grid is what supplies one. That split must
    /// not reach the wire, though. NetCode interpolates each ghost field independently, so
    /// replicating the sector and the offset as separate fields would lerp the offset across its wrap
    /// while the sector snapped, and every sector crossing would throw the entity a full sector's
    /// width and back inside one tick. An absolute coordinate has no wrap to tear at.
    /// </para>
    /// <para>
    /// Bandwidth is not the obvious loss it looks like. The value delta-compresses against the
    /// previous snapshot, so what actually goes on the wire is the movement since the last tick, in
    /// millimetres, which for anything moving at a plausible speed is a handful of bits.
    /// </para>
    /// </remarks>
    public struct ReplicatedPosition : IComponentData
    {
        //three fields rather than a double3, because double3 has no ghost field template while
        //double does. Quantization must stay in step with SectorGrid's own precision.
        [GhostField(Quantization = Quantization, Smoothing = SmoothingAction.InterpolateAndExtrapolate)]
        public double X;
        [GhostField(Quantization = Quantization, Smoothing = SmoothingAction.InterpolateAndExtrapolate)]
        public double Y;
        [GhostField(Quantization = Quantization, Smoothing = SmoothingAction.InterpolateAndExtrapolate)]
        public double Z;

        /// <summary>
        /// Steps per block: 1/1024 of a block, a shade under a millimetre.
        /// </summary>
        /// <remarks>
        /// A power of two, and that matters. The generated deserializer dequantizes with the scale
        /// written as a float literal widened to a double, so the scale is only as exact as a float
        /// can express it. At 1000 the reciprocal is 0.001000000047..., a relative error of about
        /// 4.8e-8, which is under a micron near the origin but two whole blocks fifty million out
        /// and would quietly undo the point of replicating doubles at all. 1/1024 is exact in both
        /// float and double, so the scale contributes no error at any distance. Keep this a power of
        /// two; PrecisionDoesNotDecayFiftyMillionBlocksFromTheOrigin fails if it stops being one.
        /// </remarks>
        public const int Quantization = 1024;

        /// <summary>
        /// Blocks addressable before the quantized long overflows: about nine quadrillion, which is
        /// three orders of magnitude past what the sector grid itself can address.
        /// </summary>
        public const double MaxAddressableBlocks = long.MaxValue / (double)Quantization;

        public double3 Blocks
        {
            get => new double3(X, Y, Z);
            set { X = value.x; Y = value.y; Z = value.z; }
        }
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
            replicated.Blocks = SectorGrid.ToBlocks(position);
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
            position = SectorGrid.FromBlocks(replicated.Blocks);
    }
}
