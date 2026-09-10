using System.Collections.Generic;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// The first world type: one overworld layer of warped, spline-shaped ground with a sea and a
    /// rough bedrock floor.
    /// <para>
    /// Height comes from three control fields rather than one. Continentalness decides ocean, coast or
    /// inland; erosion decides how much of the local sharpness survives; peaks supplies that sharpness
    /// as ridges rather than blobs. That is what lets flat plains sit beside mountains without a
    /// second noise field arbitrating between them.
    /// </para>
    /// </summary>
    public sealed class ClassicGenerator : TerrainGeneratorDefinition
    {
        public const string Key = "digblocks:classic";
        public const string Amplification = "amplification";
        public const string TerrainScale = "terrainScale";
        public const string SeaLevel = "seaLevel";

        private const int RootY = 64;
        private const int BottomY = -64;
        private const int TopY = 320;

        public override string Id => Key;
        public override string DisplayName => "Classic";
        public override string Description => "Rolling continents, coasts and mountains around a sea.";

        public override GeneratorParameterSchema Schema { get; } = new GeneratorParameterSchema(
            GeneratorParameter.Number(Amplification, "Amplification", 0.25f, 6f, 1f,
                "Scales how far terrain departs from sea level. Higher makes deeper oceans and taller mountains."),
            GeneratorParameter.Number(TerrainScale, "Terrain scale", 0.25f, 4f, 1f,
                "Stretches every feature at once. Higher makes broader continents and longer mountain ranges."),
            GeneratorParameter.Integer(SeaLevel, "Sea level", 0, 200, 62,
                "The height the sea fills to."));

        protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
        {
            float amplification = context.Settings.Number(Amplification);
            float scale = context.Settings.Number(TerrainScale);
            int seaLevel = context.Settings.Integer(SeaLevel);

            var shape = TerrainShape.Create("overworld.shape", scale, warpStrength: 30f);

            //what a continentalness value means in height. The flat run either side of zero is the
            //coast, and it is deliberately wide: without it, shorelines are cliffs.
            var continents = shape.Continentalness.Spline(
                (-1f, -30f), (-0.45f, -14f), (-0.15f, -3f), (0.05f, 3f), (0.35f, 12f), (0.7f, 30f), (1f, 52f));

            //how much of the local sharpness survives. Heavily eroded ground keeps almost none of it,
            //which is where plains and plateaus come from.
            var erosion = shape.Erosion.Spline((-1f, 1.3f), (-0.25f, 0.85f), (0.35f, 0.3f), (1f, 0.08f));

            var peaks = shape.PeaksAndValleys.Spline((-1f, -7f), (-0.2f, -1f), (0.35f, 12f), (1f, 30f));

            //fine relief, sampled off the warped coordinates so it travels with the rest of the shape.
            var detail = NoiseExpr.Perlin2D("overworld.detail", shape.X, shape.Z, 1f / (26f * scale), 2) * 2.2f;

            var height = RootY + (continents + peaks * erosion + detail) * amplification;

            var ground = ColumnRecipe.Create()
                .Layer("digblocks:grass_block")
                .Layer("digblocks:dirt", 3)
                .Deep("digblocks:stone")
                //shores are decided by sea level, so they appear wherever the ground meets the water
                //line rather than having to be placed.
                .Submerged("digblocks:sand", 3);

            //the floor is uneven so it reads as bedrock rather than as the bottom of a box.
            var bedrockTop = BottomY + NoiseExpr.Perlin2D("overworld.bedrock", NoiseExpr.X, NoiseExpr.Z, 1f / 3f, 1)
                .Remap(-1f, 1f, 0f, 3.99f).Floor();

            return new[]
            {
                WorldLayer.Named("overworld")
                    .Bounds(BottomY, TopY)
                    .Root(RootY)
                    .Sea(seaLevel, "digblocks:water")
                    .Metadata(new WorldLayerMetadata("Overworld",
                        skyTint: new float4(0.45f, 0.65f, 1f, 1f),
                        fogTint: new float4(0.72f, 0.83f, 1f, 1f)))
                    .Band("terrain", BandDirection.Up, height, ground)
                    .Band("bedrock", BandDirection.Up, bedrockTop, ColumnRecipe.Solid("digblocks:bedrock"))
                    .Build()
            };
        }
    }

    /// <summary>
    /// A world type with nothing in the way: flat ground at a chosen height. Useful for testing
    /// anything that is not terrain, and the simplest possible worked example of a definition.
    /// </summary>
    public sealed class FlatGenerator : TerrainGeneratorDefinition
    {
        public const string Key = "digblocks:flat";
        public const string GroundLevel = "groundLevel";
        public const string Bedrock = "bedrock";

        public override string Id => Key;
        public override string DisplayName => "Flat";
        public override string Description => "Level ground at a chosen height, and nothing else.";

        public override GeneratorParameterSchema Schema { get; } = new GeneratorParameterSchema(
            GeneratorParameter.Integer(GroundLevel, "Ground level", -32, 200, 4, "The height of the surface."),
            GeneratorParameter.Toggle(Bedrock, "Bedrock floor", true, "Lay one course of bedrock underneath."));

        protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
        {
            int ground = context.Settings.Integer(GroundLevel);
            bool bedrock = context.Settings.Toggle(Bedrock);
            int bottom = bedrock ? ground - 4 : ground - 3;

            var layer = WorldLayer.Named("flat")
                .Bounds(bottom, ground)
                .Root(ground)
                .Metadata(new WorldLayerMetadata("Flat"))
                .Band("ground", BandDirection.Up, BandSurface.At(ground),
                    ColumnRecipe.Create().Layer("digblocks:grass_block").Layer("digblocks:dirt", 2).Deep("digblocks:stone"));

            if (bedrock)
                layer.Band("bedrock", BandDirection.Up, BandSurface.At(bottom), ColumnRecipe.Solid("digblocks:bedrock"));

            return new[] { layer.Build() };
        }
    }
}
