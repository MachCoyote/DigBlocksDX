using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DigBlocks.Voxels;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace DigBlocks.Simulation
{
    /// <summary>
    /// One piece of behaviour state as it exists while its entity does not: which behaviour it
    /// belongs to, and the component itself as bytes.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a typed component, because this is what a chunk file has to hold once chunk
    /// saving exists. Keeping the in-memory record in the shape the disk record will need is what
    /// stops the disk version from being a second, diverging representation of the same thing.
    /// </remarks>
    public readonly struct EntityStateRecord
    {
        public readonly string Behavior;
        public readonly byte[] State;

        public EntityStateRecord(string behavior, byte[] state)
        {
            Behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
            State = state ?? throw new ArgumentNullException(nameof(state));
        }
    }

    /// <summary>
    /// Saves and restores whatever one behaviour needs to survive its chunk unloading.
    /// </summary>
    /// <remarks>
    /// A behaviour with no state across an unload needs no codec; most will not have one. The
    /// alternative, naming each stateful component in the stored record itself, means every new
    /// behaviour edits the record type and the residency system, and a behaviour whose author
    /// forgets to loses its state silently on the first chunk boundary.
    /// </remarks>
    public interface IEntityStateCodec
    {
        /// <summary>The behaviour this state belongs to, matching the key entity content declares.</summary>
        string Behavior { get; }

        /// <summary>Reads the state off a live entity. False when this entity carries none.</summary>
        bool TryCapture(EntityManager manager, Entity entity, out byte[] state);

        /// <summary>Puts captured state back on a freshly spawned entity.</summary>
        void Restore(EntityManager manager, Entity entity, byte[] state);
    }

    /// <summary>A behaviour whose entire state is one unmanaged component, which is the usual case.</summary>
    public sealed class ComponentStateCodec<T> : IEntityStateCodec where T : unmanaged, IComponentData
    {
        public ComponentStateCodec(string behavior) =>
            Behavior = ResourceKeys.Validate(behavior, nameof(behavior));

        public string Behavior { get; }

        public bool TryCapture(EntityManager manager, Entity entity, out byte[] state)
        {
            if (!manager.HasComponent<T>(entity)) { state = null; return false; }
            T value = manager.GetComponentData<T>(entity);
            state = new byte[UnsafeUtility.SizeOf<T>()];
            MemoryMarshal.Write(state, ref value);
            return true;
        }

        public void Restore(EntityManager manager, Entity entity, byte[] state)
        {
            //a record written by a different build can be the wrong length; dropping it leaves the
            //entity on the behaviour's own defaults rather than reinterpreting whatever is there.
            if (state == null || state.Length != UnsafeUtility.SizeOf<T>()) return;
            if (!manager.HasComponent<T>(entity)) return;
            manager.SetComponentData(entity, MemoryMarshal.Read<T>(state));
        }
    }

    /// <summary>
    /// The behaviour state this build knows how to carry across an unload.
    /// </summary>
    /// <remarks>
    /// One registration point, in the same assembly as the behaviours themselves, so adding a
    /// stateful behaviour is one line here next to the component it saves rather than an edit to
    /// the chunk store, the stored record and the residency system.
    /// </remarks>
    public sealed class EntityStateCodecs
    {
        private readonly Dictionary<string, IEntityStateCodec> byBehavior;
        private readonly IEntityStateCodec[] codecs;

        /// <summary>Every stateful behaviour the simulation implements.</summary>
        public static EntityStateCodecs Default => new EntityStateCodecs(
            new ComponentStateCodec<CircleFlight>(SimulationBehaviors.CircleFlight));

        public EntityStateCodecs(params IEntityStateCodec[] entityStateCodecs)
        {
            codecs = entityStateCodecs ?? throw new ArgumentNullException(nameof(entityStateCodecs));
            byBehavior = new Dictionary<string, IEntityStateCodec>(codecs.Length, StringComparer.Ordinal);
            foreach (var codec in codecs)
            {
                if (codec == null) throw new ArgumentException("Null state codec.", nameof(entityStateCodecs));
                if (!byBehavior.TryAdd(codec.Behavior, codec))
                    throw new ArgumentException("Duplicate state codec: " + codec.Behavior, nameof(entityStateCodecs));
            }
        }

        public int Count => codecs.Length;

        /// <summary>
        /// Everything worth keeping about an entity that is about to be destroyed. Null rather than
        /// an empty list when it has nothing, because most entities will not.
        /// </summary>
        public IReadOnlyList<EntityStateRecord> Capture(EntityManager manager, Entity entity)
        {
            List<EntityStateRecord> captured = null;
            for (int i = 0; i < codecs.Length; i++)
            {
                if (!codecs[i].TryCapture(manager, entity, out byte[] state)) continue;
                captured ??= new List<EntityStateRecord>(1);
                captured.Add(new EntityStateRecord(codecs[i].Behavior, state));
            }
            return captured;
        }

        /// <summary>
        /// Puts captured state back. A record naming a behaviour this build does not implement is
        /// skipped rather than refused: content can name behaviours a given build has not got.
        /// </summary>
        public void Restore(EntityManager manager, Entity entity, IReadOnlyList<EntityStateRecord> state)
        {
            if (state == null) return;
            for (int i = 0; i < state.Count; i++)
                if (byBehavior.TryGetValue(state[i].Behavior, out var codec))
                    codec.Restore(manager, entity, state[i].State);
        }
    }
}
