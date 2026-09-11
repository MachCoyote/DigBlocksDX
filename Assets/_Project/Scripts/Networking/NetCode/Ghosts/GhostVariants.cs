using System.Collections.Generic;
using DigBlocks.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Replication for <see cref="WorldPosition"/>, declared here because
    /// <c>DigBlocks.Simulation</c> must not reference <c>Unity.NetCode</c>. This is the same
    /// mechanism NetCode uses for <see cref="LocalTransform"/> and for <c>PhysicsVelocity</c>.
    /// </summary>
    /// <remarks>
    /// Splitting the position costs nothing on the wire and saves a good deal: the sector is an
    /// integer that rarely changes, so it delta-compresses to almost nothing, and the offset stays
    /// small, which is where NetCode's compression is at its best. An absolute position would do the
    /// opposite, and would overflow the quantized int past about 2.1 million blocks besides.
    /// </remarks>
    [GhostComponentVariation(typeof(WorldPosition), "Sector Position")]
    [GhostComponent(PrefabType = GhostPrefabType.All, SendTypeOptimization = GhostSendType.AllClients)]
    public struct WorldPositionVariant
    {
        //exact, and clamped rather than interpolated because there is no sensible value between two
        //sectors. Known seam: when an entity crosses a sector boundary while a client is watching,
        //the offset is interpolated across the wrap while the sector snaps, so the reconstructed
        //position jumps for one tick. Interpolating the reconstructed value needs a custom ghost
        //field template, which is the recorded fix. Nothing in the world yet travels that far.
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public int3 Sector;

        //millimetre precision, and the value stays under one sector edge however far out the entity
        //is, so this never overflows the way an absolute position would.
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.InterpolateAndExtrapolate)]
        public float3 Local;
    }

    /// <summary>
    /// Project-wide replication defaults.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    //NetCode's own TransformDefaultVariantSystem claims LocalTransform, and the later rule wins only
    //if it is registered first, so this must be created before it.
    [CreateBefore(typeof(TransformDefaultVariantSystem))]
    public sealed partial class DigBlocksDefaultVariantSystem : DefaultVariantSystemBase
    {
        protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
        {
            //LocalTransform is derived from WorldPosition on both ends, so replicating it as well
            //would send the same position twice and invite the two to disagree. WorldPosition is the
            //only authority on where something is.
            defaultVariants.Add(typeof(LocalTransform), Rule.ForAll(typeof(DontSerializeVariant)));

            //server-owned state. Stripping it from the client prefab keeps client archetypes smaller
            //and makes the ownership obvious at the point it is declared.
            defaultVariants.Add(typeof(ChunkResidency), Rule.ForAll(typeof(ServerOnlyVariant)));
        }
    }
}
