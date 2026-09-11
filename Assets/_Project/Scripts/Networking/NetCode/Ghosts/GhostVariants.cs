using System.Collections.Generic;
using DigBlocks.Simulation;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;

namespace DigBlocks.Networking.NetCode
{
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
            //Position reaches the client as ReplicatedPosition, which WorldPosition is rebuilt from
            //and LocalTransform is then derived from. Replicating LocalTransform as well would send
            //the same position a second time and invite the two to disagree.
            defaultVariants.Add(typeof(LocalTransform), Rule.ForAll(typeof(DontSerializeVariant)));

            //server-owned state. Stripping it from the client prefab keeps client archetypes smaller
            //and makes the ownership obvious at the point it is declared.
            defaultVariants.Add(typeof(ChunkResidency), Rule.ForAll(typeof(ServerOnlyVariant)));
        }
    }
}
