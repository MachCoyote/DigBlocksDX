using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// What a world layer is, beyond its shape. Presentation reads this to know what standing inside a
    /// layer should look like, and nothing in generation depends on any of it.
    /// <para>
    /// The named fields are the ones already spoken for. Anything else an author wants to carry goes
    /// in <see cref="Values"/>, so a new idea does not need a change here to be usable.
    /// </para>
    /// </summary>
    public sealed class WorldLayerMetadata
    {
        public static readonly WorldLayerMetadata Empty = new WorldLayerMetadata(null);

        public string DisplayName { get; }
        /// <summary>Linear RGB plus strength. Presentation may interpolate between layers across a boundary.</summary>
        public float4 SkyTint { get; }
        public float4 FogTint { get; }
        public float AmbientScale { get; }
        public IReadOnlyDictionary<string, string> Values { get; }

        public WorldLayerMetadata(string displayName, float4 skyTint = default, float4 fogTint = default,
            float ambientScale = 1f, IReadOnlyDictionary<string, string> values = null)
        {
            DisplayName = displayName;
            SkyTint = skyTint;
            FogTint = fogTint;
            AmbientScale = ambientScale;
            Values = values ?? EmptyValues;
        }

        public string Get(string key, string fallback = null)
            => key != null && Values.TryGetValue(key, out string value) ? value : fallback;

        private static readonly IReadOnlyDictionary<string, string> EmptyValues =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
