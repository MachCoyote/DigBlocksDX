using System;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// How far from a player the world is actually alive, in chunks, separately for horizontal and
    /// vertical distance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from render distance on purpose. A player can reasonably see much further than the
    /// world should be simulating: streaming a chunk costs bandwidth once, whereas keeping the
    /// entities in it alive costs ticks, pathfinding and snapshots every frame, for every peer. The
    /// two are separate settings for the same reason Minecraft separates them.
    /// </para>
    /// <para>
    /// Always clamped down to what the peer is actually streaming. Simulating an entity in a chunk a
    /// client does not have would replicate something it cannot place.
    /// </para>
    /// </remarks>
    public readonly struct EntitySimulationDistance
    {
        public const int MaxRadius = 4096;

        public readonly int Horizontal;
        public readonly int Vertical;

        public EntitySimulationDistance(int horizontal, int vertical)
        {
            if (horizontal < 0 || horizontal > MaxRadius) throw new ArgumentOutOfRangeException(nameof(horizontal));
            if (vertical < 0 || vertical > MaxRadius) throw new ArgumentOutOfRangeException(nameof(vertical));
            Horizontal = horizontal; Vertical = vertical;
        }

        /// <summary>
        /// Wide enough that the clamp against the peer's streaming distance always decides. Use this
        /// when a caller has no opinion, so behaviour matches "simulate everything streamed".
        /// </summary>
        public static EntitySimulationDistance Unbounded => new EntitySimulationDistance(MaxRadius, MaxRadius);

        public override string ToString() => $"h={Horizontal}, v={Vertical}";
    }
}
