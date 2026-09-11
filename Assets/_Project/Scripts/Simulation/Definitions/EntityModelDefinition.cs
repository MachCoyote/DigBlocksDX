using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using Unity.Mathematics;

namespace DigBlocks.Simulation.Definitions
{
    /// <summary>
    /// One box of an entity model, in the manner of a Minecraft entity model: a named part with its
    /// own pivot, so programmatic animation has something to rotate about.
    /// </summary>
    public sealed class EntityModelBox
    {
        public string Name { get; }
        /// <summary>Rotation origin of this part, in model units relative to the entity's feet.</summary>
        public float3 Pivot { get; }
        /// <summary>Minimum corner of the box, in model units relative to <see cref="Pivot"/>.</summary>
        public float3 Origin { get; }
        public float3 Size { get; }
        /// <summary>Top-left of the box's unwrapped texture region, in texture pixels.</summary>
        public int2 Uv { get; }

        public EntityModelBox(string name, float3 pivot, float3 origin, float3 size, int2 uv)
        {
            if (!math.all(math.isfinite(pivot)) || !math.all(math.isfinite(origin)))
                throw new ArgumentException("Pivot and origin must be finite.", nameof(pivot));
            if (!math.all(size > 0f) || !math.all(size <= EntityModelDefinition.MaxBoxUnits))
                throw new ArgumentOutOfRangeException(nameof(size), "Box size must be positive and within the model limit.");
            if (math.any(uv < 0)) throw new ArgumentOutOfRangeException(nameof(uv), "Texture offsets cannot be negative.");
            Name = ResourceKeys.ValidateToken(name, nameof(name));
            Pivot = pivot; Origin = origin; Size = size; Uv = uv;
        }
    }

    /// <summary>
    /// A box-list entity model. Authored as data rather than imported geometry, which is both what a
    /// blocky game actually wants and what keeps a dedicated server free of mesh assets.
    /// </summary>
    public sealed class EntityModelDefinition
    {
        /// <summary>Model units per block, matching the block model convention.</summary>
        public const float UnitsPerBlock = 16f;
        public const float MaxBoxUnits = 1024f;
        public const int MaxBoxes = 128;

        public string Key { get; }
        public string TextureKey { get; }
        public int2 TextureSize { get; }
        public IReadOnlyList<EntityModelBox> Boxes { get; }

        public EntityModelDefinition(string key, string textureKey, int2 textureSize, IReadOnlyList<EntityModelBox> boxes)
        {
            Key = ResourceKeys.Validate(key, nameof(key));
            TextureKey = ResourceKeys.Validate(textureKey, nameof(textureKey));
            if (math.any(textureSize <= 0)) throw new ArgumentOutOfRangeException(nameof(textureSize));
            TextureSize = textureSize;
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            if (boxes.Count == 0) throw new ArgumentException("A model needs at least one box.", nameof(boxes));
            if (boxes.Count > MaxBoxes) throw new ArgumentException("Too many boxes in one model.", nameof(boxes));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var box in boxes)
            {
                if (box == null) throw new ArgumentException("Null box.", nameof(boxes));
                //named parts are how animation addresses them, so two parts cannot share a name.
                if (!names.Add(box.Name)) throw new ArgumentException("Duplicate box name " + box.Name + ".", nameof(boxes));
            }
            Boxes = boxes;
        }
    }
}
