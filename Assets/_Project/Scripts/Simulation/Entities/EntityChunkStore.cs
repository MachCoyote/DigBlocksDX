using System;
using System.Collections.Generic;
using DigBlocks.Voxels;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// One entity as it exists while its chunk is not loaded: type, position and whatever behaviour
    /// state has to survive the round trip. Deliberately a plain record rather than an entity, so an
    /// unloaded chunk costs no simulation at all.
    /// </summary>
    public readonly struct StoredEntity
    {
        public readonly ushort TypeId;
        public readonly WorldPosition Position;

        /// <summary>
        /// Behaviour state, captured by whichever <see cref="IEntityStateCodec"/> claimed it. Null
        /// when the type has none, which is most of them. Naming the components here instead would
        /// mean every new stateful behaviour edits this type, and one whose author forgets loses its
        /// state silently at the first chunk boundary.
        /// </summary>
        public readonly IReadOnlyList<EntityStateRecord> State;

        public StoredEntity(ushort typeId, WorldPosition position, IReadOnlyList<EntityStateRecord> state = null)
        {
            TypeId = typeId; Position = position; State = state;
        }
    }

    /// <summary>
    /// Where entities live while their chunk does not. The persisted record and the live ECS
    /// representation have one owner at a time and an explicit handoff between them, never two
    /// mutable copies of the same entity.
    /// </summary>
    /// <remarks>
    /// Chunk saving to disk is not implemented anywhere in the project yet, so the only implementation
    /// is in memory. It is not a stub: it makes the unload and reload round trip real and testable
    /// now, and a disk-backed store later swaps in behind this interface without disturbing anything
    /// that calls it.
    /// </remarks>
    public interface IEntityChunkStore
    {
        /// <summary>Takes ownership of the entities that were in a chunk as it unloads.</summary>
        void Store(ChunkAddress address, IReadOnlyList<StoredEntity> entities);

        /// <summary>
        /// Gives back what was stored for a chunk and forgets it, because the caller is about to
        /// become the owner. Returns false when the chunk holds nothing.
        /// </summary>
        bool TryTake(ChunkAddress address, out IReadOnlyList<StoredEntity> entities);

        /// <summary>Chunks currently holding stored entities. Diagnostics and tests.</summary>
        int StoredChunkCount { get; }
    }

    public sealed class InMemoryEntityChunkStore : IEntityChunkStore
    {
        private readonly Dictionary<ChunkAddress, List<StoredEntity>> chunks = new();

        public int StoredChunkCount => chunks.Count;

        public long StoredEntityCount
        {
            get { long total = 0; foreach (var entry in chunks.Values) total += entry.Count; return total; }
        }

        public void Store(ChunkAddress address, IReadOnlyList<StoredEntity> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (entities.Count == 0) return;
            if (!chunks.TryGetValue(address, out var list)) chunks.Add(address, list = new List<StoredEntity>(entities.Count));
            for (int i = 0; i < entities.Count; i++) list.Add(entities[i]);
        }

        public bool TryTake(ChunkAddress address, out IReadOnlyList<StoredEntity> entities)
        {
            if (!chunks.TryGetValue(address, out var list)) { entities = Array.Empty<StoredEntity>(); return false; }
            chunks.Remove(address);
            entities = list;
            return true;
        }

        /// <summary>Chunks currently holding something. Diagnostics and tests.</summary>
        public IReadOnlyCollection<ChunkAddress> StoredChunks => chunks.Keys;

        /// <summary>What a chunk holds, without taking ownership of it. Diagnostics and tests.</summary>
        public bool TryPeek(ChunkAddress address, out IReadOnlyList<StoredEntity> entities)
        {
            bool found = chunks.TryGetValue(address, out var list);
            entities = found ? list : Array.Empty<StoredEntity>();
            return found;
        }

        public void Clear() => chunks.Clear();
    }
}
