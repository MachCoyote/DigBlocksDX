using System;
using System.Collections.Generic;

namespace DigBlocks.Simulation.Definitions
{
    //carries the offending content key so a load failure names the file an author has to fix.
    public sealed class EntityContentException : Exception
    {
        public string ContentKey { get; }

        public EntityContentException(string contentKey, string message, Exception inner = null)
            : base(contentKey == null ? message : contentKey + ": " + message, inner)
        {
            ContentKey = contentKey;
        }
    }

    //nullable mirror of EntityTypeAttributes. Archetypes and types both author through this, so one
    //overlay rule produces both archetype inheritance and the additive per-type authoring model.
    public sealed class EntityAttributeOverrides
    {
        public bool? Persists, Gravity, Collides, Living;
        public EntityCategory? Category;
        public float? Width, Height;
        public EntityGhostMode? GhostMode;
        public EntityGhostOptimization? GhostOptimization;
        public byte? GhostImportance;

        public EntityAttributeOverrides Clone() => (EntityAttributeOverrides)MemberwiseClone();

        //later layers win field by field; an unset field leaves the inherited value untouched.
        public void Overlay(EntityAttributeOverrides other)
        {
            if (other == null) return;
            Persists = other.Persists ?? Persists;
            Gravity = other.Gravity ?? Gravity;
            Collides = other.Collides ?? Collides;
            Living = other.Living ?? Living;
            Category = other.Category ?? Category;
            Width = other.Width ?? Width;
            Height = other.Height ?? Height;
            GhostMode = other.GhostMode ?? GhostMode;
            GhostOptimization = other.GhostOptimization ?? GhostOptimization;
            GhostImportance = other.GhostImportance ?? GhostImportance;
        }

        public EntityTypeAttributes Resolve()
        {
            var defaults = EntityTypeAttributes.Default;
            var flags = EntityFlags.None;
            if (Persists ?? defaults.Has(EntityFlags.Persists)) flags |= EntityFlags.Persists;
            if (Gravity ?? defaults.Has(EntityFlags.Gravity)) flags |= EntityFlags.Gravity;
            if (Collides ?? defaults.Has(EntityFlags.Collides)) flags |= EntityFlags.Collides;
            if (Living ?? defaults.Has(EntityFlags.Living)) flags |= EntityFlags.Living;
            return new EntityTypeAttributes(flags, Category ?? defaults.Category,
                Width ?? defaults.Width, Height ?? defaults.Height,
                GhostMode ?? defaults.GhostMode, GhostOptimization ?? defaults.GhostOptimization,
                GhostImportance ?? defaults.GhostImportance);
        }
    }

    //one authored layer. An archetype and a concrete entity type differ only in whether the key names
    //something spawnable, so they share a shape and the merge walks a chain of them.
    public class EntityLayer
    {
        public string Key;
        public string ArchetypeKey;
        public string ModelKey;
        public List<string> Behaviors;
        public EntityAttributeOverrides Attributes = new EntityAttributeOverrides();
    }

    public sealed class EntityArchetypeDefinition : EntityLayer { }

    public sealed class EntityTypeLayer : EntityLayer { }
}
