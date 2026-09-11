using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace DigBlocks.Simulation
{
    /// <summary>Behaviour keys the simulation implements, matching what entity content may declare.</summary>
    public static class SimulationBehaviors
    {
        public const string CircleFlight = "digblocks:circle_flight";
    }

    /// <summary>
    /// Flies a fixed horizontal circle about a centre. Server-owned: the client sees only the
    /// positions this produces, interpolated like any other mob's.
    /// </summary>
    public struct CircleFlight : IComponentData
    {
        public WorldPosition Center;
        public float Radius;
        /// <summary>Radians per second. Sign chooses the direction.</summary>
        public float AngularSpeed;
        public float Phase;
        /// <summary>Height above the centre, so a circle can be flown well clear of the ground.</summary>
        public float Height;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(EntityBehaviorSystemGroup))]
    [BurstCompile]
    public partial struct CircleFlightSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state) =>
            state.Dependency = new CircleFlightJob { DeltaTime = SystemAPI.Time.DeltaTime }
                .ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    internal partial struct CircleFlightJob : IJobEntity
    {
        public float DeltaTime;

        private void Execute(ref CircleFlight flight, ref WorldPosition position)
        {
            //wrapped so the phase of a long-lived entity never loses precision to a growing float.
            flight.Phase = Wrap(flight.Phase + flight.AngularSpeed * DeltaTime);
            math.sincos(flight.Phase, out float sin, out float cos);
            //offset from the centre, which is itself a sector position, so the circle works anywhere
            //in the world rather than only near the origin.
            position = SectorGrid.Offset(flight.Center, new float3(cos * flight.Radius, flight.Height, sin * flight.Radius));
        }

        private static float Wrap(float phase)
        {
            const float twoPi = 2f * math.PI;
            phase %= twoPi;
            return phase < 0f ? phase + twoPi : phase;
        }
    }
}
