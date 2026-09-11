using System;

namespace DigBlocks.Simulation
{
    //simulation-visible booleans for an entity type. Presentation is deliberately absent.
    [Flags]
    public enum EntityFlags : uint
    {
        None = 0,
        /// <summary>Saved with its chunk and restored when that chunk loads again.</summary>
        Persists = 1 << 0,
        /// <summary>Falls. A fixed-path flier or a hovering marker does not.</summary>
        Gravity = 1 << 1,
        /// <summary>Collides with terrain and other collidable entities.</summary>
        Collides = 1 << 2,
        /// <summary>Can be damaged and killed; excludes markers and pure projectiles.</summary>
        Living = 1 << 3,
        All = (1u << 4) - 1
    }

    public enum EntityCategory : byte { Mob = 0, Item = 1, Projectile = 2, Player = 3, Marker = 4 }

    //parallel to NetCode's own ghost mode and optimization enums on purpose. Simulation must not
    //reference Unity.NetCode, so the netcode assembly maps these across when it builds ghost prefabs.
    public enum EntityGhostMode : byte { Interpolated = 0, Predicted = 1, OwnerPredicted = 2 }

    public enum EntityGhostOptimization : byte { Dynamic = 0, Static = 1 }

    /// <summary>
    /// Blittable per-type record. Jobs read this table rather than the managed definitions, so every
    /// field must stay unmanaged and free of references.
    /// </summary>
    public readonly struct EntityTypeAttributes : IEquatable<EntityTypeAttributes>
    {
        public const int SizeInBytes = 16;
        public const float MaxExtent = 64f;

        public readonly EntityFlags Flags;
        public readonly float Width;
        public readonly float Height;
        public readonly EntityCategory Category;
        public readonly EntityGhostMode GhostMode;
        public readonly EntityGhostOptimization GhostOptimization;
        /// <summary>Relative replication priority; NetCode spends its snapshot budget highest first.</summary>
        public readonly byte GhostImportance;

        public EntityTypeAttributes(EntityFlags flags, EntityCategory category, float width, float height,
            EntityGhostMode ghostMode, EntityGhostOptimization ghostOptimization, byte ghostImportance)
        {
            if ((flags & ~EntityFlags.All) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
            if (!Enum.IsDefined(typeof(EntityCategory), category)) throw new ArgumentOutOfRangeException(nameof(category));
            if (!Enum.IsDefined(typeof(EntityGhostMode), ghostMode)) throw new ArgumentOutOfRangeException(nameof(ghostMode));
            if (!Enum.IsDefined(typeof(EntityGhostOptimization), ghostOptimization)) throw new ArgumentOutOfRangeException(nameof(ghostOptimization));
            if (!(width > 0f && width <= MaxExtent)) throw new ArgumentOutOfRangeException(nameof(width));
            if (!(height > 0f && height <= MaxExtent)) throw new ArgumentOutOfRangeException(nameof(height));
            Flags = flags; Category = category; Width = width; Height = height;
            GhostMode = ghostMode; GhostOptimization = ghostOptimization; GhostImportance = ghostImportance;
        }

        /// <summary>An ordinary ground mob: falls, collides, can be hurt, and is saved with its chunk.</summary>
        public static EntityTypeAttributes Default => new EntityTypeAttributes(
            EntityFlags.Persists | EntityFlags.Gravity | EntityFlags.Collides | EntityFlags.Living,
            EntityCategory.Mob, 0.6f, 1.8f, EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1);

        public bool Has(EntityFlags flag) => (Flags & flag) == flag;

        public bool Equals(EntityTypeAttributes other) =>
            Flags == other.Flags && Category == other.Category && Width.Equals(other.Width)
            && Height.Equals(other.Height) && GhostMode == other.GhostMode
            && GhostOptimization == other.GhostOptimization && GhostImportance == other.GhostImportance;

        public override bool Equals(object obj) => obj is EntityTypeAttributes other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((uint)Flags, (byte)Category, Width, Height,
            (byte)GhostMode, (byte)GhostOptimization, GhostImportance);
    }
}
