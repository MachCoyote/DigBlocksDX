using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One band of world at some height: the overworld, a roof of stone above it, a burning place a
    /// hundred blocks below. A layer owns its own shape, its own materials and its own features, so
    /// layers differ from one another rather than being variations on one world.
    /// <para>
    /// Bounds are the layer's business alone. Nothing stops two layers overlapping, and nothing stops
    /// a layer sitting five thousand blocks up; chunks that miss every layer cost nothing, because a
    /// generator tests bounds before it evaluates anything.
    /// </para>
    /// </summary>
    public sealed class WorldLayer
    {
        /// <summary>A stable name. Seeds derive from it, so renaming a layer regenerates its world.</summary>
        public string Name { get; }
        /// <summary>Where the layer's heightmaps are anchored. Authoring reads from here, outward.</summary>
        public int RootY { get; }
        public int BottomBound { get; }
        public int TopBound { get; }
        public WorldLayerMetadata Metadata { get; }
        public IReadOnlyList<SurfaceBand> Bands { get; }
        /// <summary>World Y the layer's fluid fills up to, or null for a dry layer.</summary>
        public int? SeaLevel { get; }
        public string SeaFluid { get; }
        public DensityStage Density { get; }
        public AquiferStage Aquifers { get; }
        public IReadOnlyList<IFeatureGenerator> Features { get; }
        public IBiomeSource Biomes { get; }

        internal WorldLayer(Builder builder)
        {
            Name = builder.LayerName;
            RootY = builder.RootHeight;
            BottomBound = builder.Bottom;
            TopBound = builder.Top;
            Metadata = builder.LayerMetadata ?? WorldLayerMetadata.Empty;
            Bands = builder.LayerBands.ToArray();
            SeaLevel = builder.SeaHeight;
            SeaFluid = builder.SeaFluidKey;
            Density = builder.DensityStage;
            Aquifers = builder.AquiferStage;
            Features = builder.LayerFeatures.ToArray();
            Biomes = builder.BiomeSource;

            if (Bands.Count == 0 && Density == null)
                throw new GenerationContentException($"World layer '{Name}' has neither bands nor a density stage, so it can never produce anything.");
            if (BottomBound > TopBound)
                throw new GenerationContentException($"World layer '{Name}' has a bottom bound above its top bound.");
            if (SeaLevel.HasValue && string.IsNullOrWhiteSpace(SeaFluid))
                throw new GenerationContentException($"World layer '{Name}' has a sea level but no fluid to fill it with.");
            if (Aquifers != null && Density == null)
                throw new GenerationContentException($"World layer '{Name}' has aquifers but no density stage, so nothing would ever be carved for them to fill.");
        }

        /// <summary>True when the world Y span of a chunk touches this layer at all.</summary>
        public bool Overlaps(int minY, int maxY) => minY <= TopBound && maxY >= BottomBound;

        public static Builder Named(string name) => new Builder(name);

        /// <summary>Assembles a layer. Every part but the name and the bounds is optional.</summary>
        public sealed class Builder
        {
            internal readonly string LayerName;
            internal int RootHeight, Bottom = int.MinValue / 2, Top = int.MaxValue / 2;
            internal WorldLayerMetadata LayerMetadata;
            internal readonly List<SurfaceBand> LayerBands = new List<SurfaceBand>();
            internal int? SeaHeight;
            internal string SeaFluidKey;
            internal DensityStage DensityStage;
            internal AquiferStage AquiferStage;
            internal readonly List<IFeatureGenerator> LayerFeatures = new List<IFeatureGenerator>();
            internal IBiomeSource BiomeSource;

            internal Builder(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A world layer needs a name.", nameof(name));
                LayerName = name;
            }

            /// <summary>The height the layer's surfaces are authored around.</summary>
            public Builder Root(int worldY) { RootHeight = worldY; return this; }

            public Builder Bounds(int bottom, int top)
            {
                Bottom = bottom; Top = top; return this;
            }

            public Builder Metadata(WorldLayerMetadata metadata) { LayerMetadata = metadata; return this; }

            /// <summary>Adds a band. Bands paint in the order they are added and later writes win.</summary>
            public Builder Band(SurfaceBand band)
            {
                LayerBands.Add(band ?? throw new ArgumentNullException(nameof(band)));
                return this;
            }

            public Builder Band(string name, BandDirection direction, BandSurface surface, ColumnRecipe fill,
                BandSurface extent = null)
                => Band(new SurfaceBand(name, direction, surface, fill, extent));

            public Builder Sea(int worldY, string fluidKey)
            {
                SeaHeight = worldY; SeaFluidKey = fluidKey; return this;
            }

            /// <summary>Opts the layer into three-dimensional shaping: overhangs, floating ground, caves.</summary>
            public Builder Density(DensityStage stage) { DensityStage = stage; return this; }

            /// <summary>Local water tables, so carved space below them holds fluid instead of air.</summary>
            public Builder Aquifers(AquiferStage stage) { AquiferStage = stage; return this; }

            public Builder Feature(IFeatureGenerator feature)
            {
                LayerFeatures.Add(feature ?? throw new ArgumentNullException(nameof(feature)));
                return this;
            }

            public Builder Biomes(IBiomeSource source) { BiomeSource = source; return this; }

            public WorldLayer Build() => new WorldLayer(this);
        }
    }
}
