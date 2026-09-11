using DigBlocks.Voxels;
using Unity.Entities;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// Which entity type this is, as a runtime id into <see cref="EntityTypeRegistry"/>. Replicated
    /// and never changes after spawn, so it costs a ghost field once rather than per snapshot.
    /// </summary>
    public struct EntityTypeId : IComponentData
    {
        /// <summary>Reserved: a default-constructed component names no type rather than the first one.</summary>
        public const ushort None = 0;

        public ushort Value;

        public bool IsValid => Value != None;
    }

    /// <summary>
    /// Collision box, taken from the registry on spawn. Not replicated: both ends resolve it from the
    /// same type id, and the registry fingerprint is what guarantees they agree.
    /// </summary>
    public struct AabbExtents : IComponentData
    {
        public float Width;
        public float Height;
    }

    /// <summary>
    /// The chunk this entity currently occupies. Server-side only; it drives which entities save with
    /// which chunk, and which peers a ghost is relevant to.
    /// </summary>
    public struct ChunkResidency : IComponentData
    {
        public ChunkAddress Address;
    }
}
