using System;

namespace DigBlocks.Voxels.Definitions
{
    //nullable mirror of BlockAttributes. Archetypes and blocks both author through this type, so one
    //overlay rule produces both archetype inheritance and the additive per-block authoring model.
    public sealed class BlockAttributeOverrides
    {
        public bool? Opaque, FullCube, Collides, Replaceable, PermitsFluid, RequiresTool, Flammable, Unbreakable, RandomTicks;
        public float? Hardness, BlastResistance, Friction;
        public BlockToolClass? ToolClass;
        public byte? ToolTier, LightEmission, LightAttenuation, FlammabilityCatch, FlammabilitySpread;

        public BlockAttributeOverrides Clone() => (BlockAttributeOverrides)MemberwiseClone();

        //later layers win field by field; an unset field leaves the inherited value untouched.
        public void Overlay(BlockAttributeOverrides other)
        {
            if (other == null) return;
            Opaque = other.Opaque ?? Opaque;
            FullCube = other.FullCube ?? FullCube;
            Collides = other.Collides ?? Collides;
            Replaceable = other.Replaceable ?? Replaceable;
            PermitsFluid = other.PermitsFluid ?? PermitsFluid;
            RequiresTool = other.RequiresTool ?? RequiresTool;
            Flammable = other.Flammable ?? Flammable;
            Unbreakable = other.Unbreakable ?? Unbreakable;
            RandomTicks = other.RandomTicks ?? RandomTicks;
            Hardness = other.Hardness ?? Hardness;
            BlastResistance = other.BlastResistance ?? BlastResistance;
            Friction = other.Friction ?? Friction;
            ToolClass = other.ToolClass ?? ToolClass;
            ToolTier = other.ToolTier ?? ToolTier;
            LightEmission = other.LightEmission ?? LightEmission;
            LightAttenuation = other.LightAttenuation ?? LightAttenuation;
            FlammabilityCatch = other.FlammabilityCatch ?? FlammabilityCatch;
            FlammabilitySpread = other.FlammabilitySpread ?? FlammabilitySpread;
        }

        public BlockAttributes Resolve(bool hasBlockEntity)
        {
            var defaults = BlockAttributes.Default;
            var flags = BlockFlags.None;
            if (Opaque ?? defaults.Has(BlockFlags.Opaque)) flags |= BlockFlags.Opaque;
            if (FullCube ?? defaults.Has(BlockFlags.FullCube)) flags |= BlockFlags.FullCube;
            if (Collides ?? defaults.Has(BlockFlags.Collides)) flags |= BlockFlags.Collides;
            if (Replaceable ?? false) flags |= BlockFlags.Replaceable;
            if (PermitsFluid ?? false) flags |= BlockFlags.PermitsFluid;
            if (RequiresTool ?? false) flags |= BlockFlags.RequiresTool;
            if (Flammable ?? false) flags |= BlockFlags.Flammable;
            if (Unbreakable ?? false) flags |= BlockFlags.Unbreakable;
            if (RandomTicks ?? false) flags |= BlockFlags.RandomTicks;
            if (hasBlockEntity) flags |= BlockFlags.BlockEntity;
            //a non-opaque block passes light through unless it states an attenuation explicitly.
            byte attenuation = LightAttenuation ?? ((flags & BlockFlags.Opaque) != 0 ? defaults.LightAttenuation : (byte)0);
            //an unbreakable block still reports a hardness; mining rejects it on the flag, not a sentinel value.
            return new BlockAttributes(flags,
                Hardness ?? defaults.Hardness,
                BlastResistance ?? defaults.BlastResistance,
                Friction ?? defaults.Friction,
                ToolClass ?? defaults.ToolClass,
                ToolTier ?? defaults.ToolTier,
                LightEmission ?? defaults.LightEmission,
                attenuation,
                FlammabilityCatch ?? 0,
                FlammabilitySpread ?? 0);
        }
    }
}
