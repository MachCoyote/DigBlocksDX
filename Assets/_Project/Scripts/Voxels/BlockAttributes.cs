using System;

namespace DigBlocks.Voxels
{
    //simulation-visible booleans. Render material selection is appearance data and is deliberately absent.
    [Flags]
    public enum BlockFlags : uint
    {
        None = 0,
        //hides the touching neighbour face and stops light; a render material never implies this.
        Opaque = 1 << 0,
        FullCube = 1 << 1,
        Collides = 1 << 2,
        Replaceable = 1 << 3,
        PermitsFluid = 1 << 4,
        RequiresTool = 1 << 5,
        BlockEntity = 1 << 6,
        Flammable = 1 << 7,
        Unbreakable = 1 << 8,
        RandomTicks = 1 << 9,
        All = (1u << 10) - 1
    }

    public enum BlockToolClass : byte { None = 0, Pickaxe = 1, Axe = 2, Shovel = 3, Hoe = 4, Shears = 5, Sword = 6 }

    //blittable 24-byte per-state record. Meshing and simulation jobs read this table rather than
    //managed definitions, so every field must stay unmanaged and free of string or object references.
    public readonly struct BlockAttributes : IEquatable<BlockAttributes>
    {
        public const byte MaxLight = 15;
        public const int SizeInBytes = 24;

        public readonly BlockFlags Flags;
        public readonly float Hardness;
        public readonly float BlastResistance;
        public readonly float Friction;
        public readonly BlockToolClass ToolClass;
        public readonly byte ToolTier;
        public readonly byte LightEmission;
        public readonly byte LightAttenuation;
        public readonly byte FlammabilityCatch;
        public readonly byte FlammabilitySpread;
        private readonly byte reservedLow, reservedHigh;

        public BlockAttributes(BlockFlags flags, float hardness, float blastResistance, float friction,
            BlockToolClass toolClass, byte toolTier, byte lightEmission, byte lightAttenuation,
            byte flammabilityCatch, byte flammabilitySpread)
        {
            if ((flags & ~BlockFlags.All) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
            if (!(hardness >= 0f) || !(blastResistance >= 0f) || !(friction > 0f))
                throw new ArgumentOutOfRangeException(nameof(hardness), "Hardness and resistance must be non-negative and friction positive.");
            if (!Enum.IsDefined(typeof(BlockToolClass), toolClass)) throw new ArgumentOutOfRangeException(nameof(toolClass));
            if (lightEmission > MaxLight || lightAttenuation > MaxLight)
                throw new ArgumentOutOfRangeException(nameof(lightEmission), "Light values must not exceed " + MaxLight + ".");
            Flags = flags; Hardness = hardness; BlastResistance = blastResistance; Friction = friction;
            ToolClass = toolClass; ToolTier = toolTier; LightEmission = lightEmission; LightAttenuation = lightAttenuation;
            FlammabilityCatch = flammabilityCatch; FlammabilitySpread = flammabilitySpread;
            reservedLow = 0; reservedHigh = 0;
        }

        //solid, mineable, ordinary stone-like behaviour; overrides narrow this rather than build from nothing.
        public static BlockAttributes Default => new BlockAttributes(
            BlockFlags.Opaque | BlockFlags.FullCube | BlockFlags.Collides, 1f, 1f, 0.6f,
            BlockToolClass.None, 0, 0, MaxLight, 0, 0);

        //empty space: no occlusion, no collision, always overwritable, always fluid-compatible.
        public static BlockAttributes Air => new BlockAttributes(
            BlockFlags.Replaceable | BlockFlags.PermitsFluid, 0f, 0f, 0.6f,
            BlockToolClass.None, 0, 0, 0, 0, 0);

        public BlockAttributes WithFlags(BlockFlags flags) => new BlockAttributes(flags, Hardness, BlastResistance,
            Friction, ToolClass, ToolTier, LightEmission, LightAttenuation, FlammabilityCatch, FlammabilitySpread);

        public bool Has(BlockFlags flag) => (Flags & flag) == flag;
        public bool PermitsFluid => Has(BlockFlags.PermitsFluid);

        public bool Equals(BlockAttributes other) =>
            Flags == other.Flags && Hardness.Equals(other.Hardness) && BlastResistance.Equals(other.BlastResistance)
            && Friction.Equals(other.Friction) && ToolClass == other.ToolClass && ToolTier == other.ToolTier
            && LightEmission == other.LightEmission && LightAttenuation == other.LightAttenuation
            && FlammabilityCatch == other.FlammabilityCatch && FlammabilitySpread == other.FlammabilitySpread;

        public override bool Equals(object obj) => obj is BlockAttributes other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Flags;
                hash = hash * 397 ^ Hardness.GetHashCode();
                hash = hash * 397 ^ BlastResistance.GetHashCode();
                hash = hash * 397 ^ Friction.GetHashCode();
                hash = hash * 397 ^ ((int)ToolClass << 24 | ToolTier << 16 | LightEmission << 8 | LightAttenuation);
                return hash * 397 ^ (FlammabilityCatch << 8 | FlammabilitySpread);
            }
        }
    }
}
