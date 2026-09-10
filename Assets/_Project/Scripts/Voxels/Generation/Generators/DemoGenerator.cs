using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// A reference world type that uses every part of the generation system at least once, with a
    /// comment on each explaining what it is and why it is there.
    /// <para>
    /// This exists to be read, not to be played. It is deliberately busy: five layers spread from far
    /// underground to a kilometre up, one of them overlapping another to show what that does. A real
    /// world type would use a fraction of this.
    /// </para>
    /// <para>
    /// The layers, in the order they are painted, and what each is here to demonstrate:
    /// </para>
    /// <list type="number">
    /// <item><description><b>deeplands</b> — a hand-written Burst heightmap, a carved density field with
    /// gradients, and aquifers.</description></item>
    /// <item><description><b>overworld</b> — control fields, splines, warping, the full surface-rule
    /// recipe, a sea, and the feature and biome seams.</description></item>
    /// <item><description><b>landmark</b> — deliberately overlaps the overworld, to show that a later
    /// layer simply paints over an earlier one.</description></item>
    /// <item><description><b>roof</b> — a slab with rolling ground hanging from its underside.</description></item>
    /// <item><description><b>skyislands</b> — a layer with no bands at all, shaped purely by a density
    /// field, sitting a kilometre above everything else at no cost to it.</description></item>
    /// </list>
    /// </summary>
    public sealed class DemoGenerator : TerrainGeneratorDefinition
    {
        public const string Key = "digblocks:demo";

        //Parameter keys are constants so settings code and this file cannot drift apart on a typo.
        public const string Amplification = "amplification";
        public const string SeaLevel = "seaLevel";
        public const string SkyIslands = "skyIslands";
        public const string SurfaceStyle = "surfaceStyle";

        public override string Id => Key;
        public override string DisplayName => "Demo";
        public override string Description => "Every generation feature at once, as a worked reference.";

        /// <summary>
        /// The knobs this world type exposes. A settings screen can be built from this alone: each
        /// entry carries its own kind, range, default and display text, so the UI never needs to know
        /// what a particular world type means.
        /// <para>All four parameter kinds appear here, one of each.</para>
        /// </summary>
        public override GeneratorParameterSchema Schema { get; } = new GeneratorParameterSchema(
            //Number: a continuous value. Coerced into range on read, so a setting saved before the
            //range changed still loads instead of failing the world.
            GeneratorParameter.Number(Amplification, "Amplification", 0.25f, 6f, 1f,
                "Scales how far terrain departs from sea level."),

            //Integer: rounded rather than truncated, so 61.7 becomes 62.
            GeneratorParameter.Integer(SeaLevel, "Sea level", 0, 200, 62,
                "The height the overworld's sea fills to."),

            //Toggle: a bool. Used below to drop a whole layer.
            GeneratorParameter.Toggle(SkyIslands, "Sky islands", true,
                "Generate the floating islands a kilometre up."),

            //Choice: a named option set. The index is what is stored; the name is what is read.
            GeneratorParameter.Choice(SurfaceStyle, "Surface style",
                new[] { "grassy", "rocky", "sandy" }, defaultIndex: 0,
                "What the overworld's surface is made of."));

        protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
        {
            //Hand-written Burst functions have to be registered before anything that names them is
            //compiled. Registering here rather than at startup keeps the demo self-contained.
            DemoBurstFunctions.EnsureRegistered();

            float amplification = context.Settings.Number(Amplification);
            int seaLevel = context.Settings.Integer(SeaLevel);
            bool skyIslands = context.Settings.Toggle(SkyIslands);
            string surfaceStyle = context.Settings.Choice(SurfaceStyle);

            var layers = new List<WorldLayer>
            {
                Deeplands(),
                Overworld(amplification, seaLevel, surfaceStyle),
                Landmark(),
                Roof()
            };

            //A world type can decide it has fewer layers. Nothing downstream cares how many there are.
            if (skyIslands) layers.Add(SkyIslands_());
            return layers;
        }

        // ---------------------------------------------------------------------------------------
        // 1. deeplands
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A slab of rock far below everything else, hollowed out by a three-dimensional field and
        /// partly flooded. Demonstrates hand-written band heights, density carving, height gradients
        /// and aquifers.
        /// </summary>
        private static WorldLayer Deeplands()
        {
            const int bottom = -320, top = -160, root = -240;

            //A band's height can be a registered Burst function instead of an expression. This is the
            //escape hatch for a surface you would rather write outright than assemble from
            //instructions: an imported heightmap, a hand-tuned fractal, anything.
            //The second argument is the seed name, which is what the function receives.
            var ceiling = BandSurface.Function(DemoBurstFunctions.DomeId, "deeplands.ceiling");

            //A recipe describes what an interval is made of, by depth from its free face.
            var rock = ColumnRecipe.Create()
                //Strata are listed from the free face inward. This one is a single course of gravel
                //scree lying on the rock.
                .Layer("digblocks:gravel", 1)
                //Everything past the named strata.
                .Deep("digblocks:stone")
                //Swap the deep fill below a height, so depth reads as depth rather than one mass.
                //A real world type would use a distinct deep stone here; the shipped palette has none
                //yet, so cobblestone stands in.
                .DeepBelow(-260, "digblocks:cobblestone");

            return WorldLayer.Named("deeplands")
                //Bounds are the layer's own business. Nothing outside them is ever written, and a
                //chunk that misses them entirely costs a binary search and nothing else.
                .Bounds(bottom, top)
                //Root is where the layer's surfaces are authored around. It is documentation for the
                //author; generation itself does not read it.
                .Root(root)

                //Metadata is for presentation. Nothing in generation reads any of it. The named fields
                //are the ones already spoken for; Values carries anything else an author invents.
                .Metadata(new WorldLayerMetadata("The Deeplands",
                    skyTint: new float4(0.08f, 0.05f, 0.10f, 1f),
                    fogTint: new float4(0.14f, 0.10f, 0.16f, 1f),
                    ambientScale: 0.35f,
                    values: new Dictionary<string, string> { { "music", "digblocks:music/deep" } }))

                //An Up band's free face is on top and its fill runs down from it. The extent says
                //where the fill stops; without one it runs to the layer's bound on that side.
                .Band("body", BandDirection.Up, ceiling, rock, extent: BandSurface.At(bottom))

                //A density stage carves the solid the bands just painted. This is where caves,
                //cavities and overhangs come from, and it is the only way to make shapes a heightmap
                //cannot describe.
                .Density(new DensityStage("caverns",
                    //A three-dimensional field. Positive is solid, negative is carved away.
                    NoiseExpr.Perlin3D("caverns", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z, 1f / 34f, 3),
                    DensityMode.Carve,
                    //Everything below this becomes air. Raising it carves more.
                    threshold: 0.06f,
                    //Lattice spacing in blocks. The field is sampled on this grid and interpolated
                    //between, so a chunk costs 405 samples instead of 32768. It must divide 32 evenly
                    //or neighbouring chunks would interpolate between different points and the carve
                    //would step at the seam.
                    resolution: new int3(4, 8, 4),
                    //Gradients bias the field by height. This is how a layer is made to close off at
                    //its ends without a hard clamp, which would cut it flat instead of resolving it
                    //into ground and open space.
                    gradients: new[]
                    {
                        //Solid at the floor, fading out over 24 blocks above it.
                        DensityGradient.Floor(bottom, fade: 24, strength: 2.5f),
                        //Open at the ceiling, fading in over 24 blocks below it.
                        DensityGradient.Ceiling(top, fade: 24, strength: 2.5f),
                        //The general form, for anything the two helpers do not cover.
                        new DensityGradient(fromY: root - 20, toY: root + 20, fromBias: 0.05f, toBias: -0.05f)
                    }))

                //Aquifers give each region of the world its own flat water level, so some caverns hold
                //lakes and some are dry. Without them a layer with caves has two possible looks: every
                //cavern flooded, or none.
                //Where a layer has aquifers they own the water below their top, and the sea owns only
                //what is above it.
                .Aquifers(new AquiferStage("watertable", "digblocks:water",
                    minY: bottom, maxY: top,
                    //The height regional levels vary around, and how far either side they may fall.
                    baseLevel: root - 40, levelJitter: 26,
                    //The fraction of regions holding no water at all.
                    dryChance: 0.4f,
                    //How large a region sharing one level is. Water has to be flat to read as water,
                    //so a region holds one level throughout and neighbours simply differ.
                    cellSize: new int3(24, 20, 24)))
                .Build();
        }

        // ---------------------------------------------------------------------------------------
        // 2. overworld
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Ordinary ground with a sea. Demonstrates the control-field approach to height, most of the
        /// instruction set, the full surface-rule recipe, and the feature and biome seams.
        /// </summary>
        private static WorldLayer Overworld(float amplification, int seaLevel, string surfaceStyle)
        {
            const int bottom = -64, top = 320, root = 64;

            //TerrainShape builds the three control fields a layer's character is usually made from,
            //over a shared set of warped coordinates. Warping the coordinates once, rather than each
            //field separately, keeps the three registered with one another: warping them apart would
            //decorrelate the very relationships the splines below are authored around.
            //Every field it hands back is already spread, so all three genuinely span [-1, 1].
            var shape = TerrainShape.Create("overworld.shape", scale: 1f, warpStrength: 30f);

            //A spline says what a noise value *means*. This one turns continentalness into height:
            //deep ocean at one end, high inland at the other, with a wide flat run through the middle
            //that becomes the coast. Without that flat run, shorelines are cliffs.
            //Tangents are solved when the spline is built, under conditions that stop a smooth curve
            //overshooting its own knots — overshoot in a height curve is a spike nobody asked for.
            var continents = shape.Continentalness.Spline(
                (-1f, -30f), (-0.45f, -14f), (-0.15f, -3f), (0.05f, 3f), (0.35f, 12f), (0.7f, 30f), (1f, 52f));

            //Erosion decides how much of the local sharpness survives. Heavily eroded ground keeps
            //almost none, which is where plains and plateaus come from.
            var erosion = shape.Erosion.Spline((-1f, 1.3f), (-0.25f, 0.85f), (0.35f, 0.3f), (1f, 0.08f));

            //Peaks supplies that sharpness. TerrainShape has already folded this field about zero, so
            //its crests are lines rather than blobs.
            var peaks = shape.PeaksAndValleys.Spline((-1f, -7f), (-0.2f, -1f), (0.35f, 12f), (1f, 30f));

            //Linear splines exist too, for when straight segments are what you want. Their slope
            //breaks at every knot, which is visible in terrain but fine for a modulation like this.
            var detailStrength = shape.Erosion.Spline(Spline.Linear((-1f, 3.5f), (1f, 0.8f)));

            //Fine relief, sampled off the shape's own warped coordinates so it travels with everything
            //else rather than sitting on top as an independent layer of fuzz.
            var detail = NoiseExpr.Perlin2D("overworld.detail", shape.X, shape.Z, 1f / 26f, 2) * detailStrength;

            //Warping a fresh pair of coordinates, to show the general form. Warp2D returns the two
            //offset coordinate expressions; feed them to any sampler and its output stops looking like
            //noise, because coordinates are ordinary expressions and a warped field is just a field
            //sampled somewhere else.
            var (plateauX, plateauZ) = NoiseExpr.Warp2D("overworld.plateauwarp", NoiseExpr.X, NoiseExpr.Z,
                frequency: 1f / 300f, strength: 40f);

            //Terrace quantizes a field into flat steps, softened by the second argument: 0 is a hard
            //stair, 1 leaves the field untouched. Mesas and plateaus.
            var plateaus = NoiseExpr.Perlin2D("overworld.plateaus", plateauX, plateauZ, 1f / 220f, 2)
                .Spread(0.4f)          //spread first, so the terrace steps land across the whole range
                .Terrace(0.18f, 0.35f) //about five steps per unit, a third of the original slope kept
                * 9f;

            //Select is the one branch in the instruction set: value < threshold ? a : b. Here it turns
            //the plateau term off wherever the ground is well eroded, so mesas only appear in rugged
            //country. This is also the shape a biome selection will take.
            var gatedPlateaus = NoiseExpr.Select(shape.Erosion, NoiseExpr.Constant(0.1f), plateaus, NoiseExpr.Constant(0f));

            //An external node calls a registered Burst function per sample. The escape hatch for
            //anything the instruction set cannot express. It receives the sample coordinate, its own
            //derived seed, and the two operand values given here.
            var ripples = NoiseExpr.External(DemoBurstFunctions.RippleId, "overworld.ripples",
                a: NoiseExpr.Constant(1f / 90f), b: NoiseExpr.Constant(1.5f));

            //Everything assembled. Amplification scales the whole departure from the root, which is
            //what makes an amplified world look amplified rather than merely noisy.
            var height = root + (continents + peaks * erosion + gatedPlateaus + detail + ripples) * amplification;

            //A handful of operations that have not appeared yet, kept in one place. They are folded
            //into the height so they are actually exercised rather than merely mentioned.
            var trim = NoiseExpr.Value2D("overworld.trim", NoiseExpr.X, NoiseExpr.Z, 1f / 48f, 2, mode: FbmMode.Billow)
                .Unsigned()                    //[-1, 1] rescaled to [0, 1]
                .Remap(0f, 1f, -1.5f, 1.5f)    //and then onto any other range
                .Clamp(-1f, 1f)                //bounded, so a stray extreme cannot spike the terrain
                .Abs()                         //folded positive
                .Min(NoiseExpr.Constant(0.9f)) //and capped
                .Max(NoiseExpr.Constant(0.1f));//and floored

            //Lerp blends two expressions by a third: a + t * (b - a).
            var finalHeight = height.Lerp(height + trim, NoiseExpr.Constant(0.5f));

            //The surface style parameter picks which materials the recipe uses. This is the simplest
            //possible demonstration of a setting changing content rather than just numbers.
            string topBlock = surfaceStyle == "rocky" ? "digblocks:stone"
                : surfaceStyle == "sandy" ? "digblocks:sand"
                : "digblocks:grass_block";
            string subsurface = surfaceStyle == "sandy" ? "digblocks:sand" : "digblocks:dirt";

            var ground = ColumnRecipe.Create()
                //Depth 0: the free face itself.
                .Layer(topBlock, 1)
                //Depths 1 to 3.
                .Layer(subsurface, 3)
                //Everything below that.
                .Deep("digblocks:stone")
                .DeepBelow(0, "digblocks:cobblestone")
                //Submerged and Crest are tested ahead of the strata, so a shore or a summit replaces
                //the surface material instead of having to be woven into every recipe that might meet
                //water or altitude.
                //Submerged applies where the free face sits at or below the layer's sea level. A layer
                //with no sea ignores it.
                .Submerged("digblocks:sand", 3)
                //Crest applies where the free face sits at or above a height. Bare scree on summits;
                //snow, once there is a snow block.
                .Crest(140, "digblocks:gravel", 2);

            return WorldLayer.Named("overworld")
                .Bounds(bottom, top)
                .Root(root)
                .Metadata(new WorldLayerMetadata("Overworld",
                    skyTint: new float4(0.45f, 0.65f, 1f, 1f),
                    fogTint: new float4(0.72f, 0.83f, 1f, 1f)))

                //A sea fills whatever solid did not claim, up to this height. It is also what decides
                //where the Submerged rule applies, so shores appear at the water line whether or not
                //the water can currently be seen.
                .Sea(seaLevel, "digblocks:water")

                //No extent, so the fill runs down to the layer's bottom bound.
                .Band("terrain", BandDirection.Up, finalHeight, ground)

                //A second band, painted after the first and therefore over it. An uneven floor so the
                //bottom of the world reads as bedrock rather than as the bottom of a box.
                .Band("bedrock", BandDirection.Up,
                    bottom + NoiseExpr.Perlin2D("overworld.bedrock", NoiseExpr.X, NoiseExpr.Z, 1f / 3f, 1)
                        .Remap(-1f, 1f, 0f, 3.99f)
                        .Floor(),
                    ColumnRecipe.Solid("digblocks:bedrock"))

                //SEAM. Features are stored and never run: nothing calls Place yet. This is here so the
                //shape of the contract is visible from an authored world type. See DemoFeature below.
                .Feature(new DemoFeature())

                //SEAM. Biomes are stored and never consulted. See DemoBiomes below.
                .Biomes(new DemoBiomes(shape))
                .Build();
        }

        // ---------------------------------------------------------------------------------------
        // 3. landmark
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A plinth near the origin that deliberately overlaps the overworld.
        /// <para>
        /// There is no protection against layers overlapping, and this is why: order is the contract.
        /// This layer is authored after the overworld, so where the two meet, this one wins. An author
        /// who wants that gets it; an author who does not simply does not overlap their bounds.
        /// </para>
        /// </summary>
        private static WorldLayer Landmark()
        {
            //Distance from the world origin, built out of ordinary arithmetic on the coordinates.
            //There is nothing special about X and Z: they are leaf expressions like any other.
            var distance = (NoiseExpr.X * NoiseExpr.X + NoiseExpr.Z * NoiseExpr.Z).Sqrt();

            //Inside 48 blocks the surface is at 70; outside it the surface drops below the extent,
            //which makes the interval empty and paints nothing at all. An empty interval is the
            //idiomatic way to say "not here".
            var surface = NoiseExpr.Select(distance, NoiseExpr.Constant(48f),
                whenBelow: NoiseExpr.Constant(70f),
                whenAbove: NoiseExpr.Constant(-9999f));

            return WorldLayer.Named("landmark")
                .Bounds(60, 72)
                .Root(70)
                .Metadata(new WorldLayerMetadata("Landmark"))
                .Band("plinth", BandDirection.Up, surface, ColumnRecipe.Solid("digblocks:testblock"),
                    extent: BandSurface.At(60))
                .Build();
        }

        // ---------------------------------------------------------------------------------------
        // 4. roof
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// A slab of stone above the overworld with rolling grass hanging from its underside.
        /// <para>
        /// This is the case Down bands exist for. A Down band's free face is on its <em>bottom</em>,
        /// and depth is measured upward from it, so a recipe written the ordinary way — surface, then
        /// subsurface, then deep — comes out upside down without the recipe knowing anything about it.
        /// </para>
        /// </summary>
        private static WorldLayer Roof()
        {
            const int bottom = 360, top = 430;

            //An ordinary rolling heightmap, used here as a ceiling rather than as ground.
            var underside = bottom + 14f
                + NoiseExpr.Perlin2D("roof.underside", NoiseExpr.X, NoiseExpr.Z, 1f / 52f, 3) * 11f;

            var hanging = ColumnRecipe.Create()
                .Layer("digblocks:grass_block", 1) //depth 0, the free face: the upside-down surface
                .Layer("digblocks:dirt", 3)        //depths 1 to 3, above it
                .Deep("digblocks:stone");          //and rock above that

            return WorldLayer.Named("roof")
                .Bounds(bottom, top)
                .Root(bottom + 14)
                .Metadata(new WorldLayerMetadata("The Roof",
                    skyTint: new float4(0.10f, 0.12f, 0.18f, 1f), ambientScale: 0.6f))

                //The body: solid from the underside up to the top bound.
                .Band("body", BandDirection.Up, BandSurface.At(top), ColumnRecipe.Solid("digblocks:stone"),
                    extent: underside)

                //The underside, painted after the body and therefore over it. Down direction, so the
                //fill runs upward from the free face and depth is measured the same way.
                .Band("underside", BandDirection.Down, underside, hanging,
                    //The extent is where the fill stops, which here is a fixed number of blocks up.
                    extent: underside + 6f)
                .Build();
        }

        // ---------------------------------------------------------------------------------------
        // 5. skyislands
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Floating ground a kilometre up, shaped entirely by a density field.
        /// <para>
        /// Two things to notice. A layer needs no bands at all if it has a density stage, because
        /// <see cref="DensityMode.Add"/> places solid rather than removing it. And a layer this far
        /// from everything else costs the rest of the world nothing: chunks that do not touch its
        /// bounds are ruled out before anything is evaluated, so the world is vertically unbounded
        /// rather than merely tall.
        /// </para>
        /// </summary>
        private static WorldLayer SkyIslands_()
        {
            const int bottom = 950, top = 1100, root = 1020;

            //Warp3D is the three-dimensional counterpart, returning all three offset coordinates. It
            //makes the islands lumpy and undercut rather than smoothly blobby.
            var (wx, wy, wz) = NoiseExpr.Warp3D("sky.warp", NoiseExpr.X, NoiseExpr.Y, NoiseExpr.Z,
                frequency: 1f / 60f, strength: 12f);

            var field = NoiseExpr.Perlin3D("sky.islands", wx, wy, wz, 1f / 45f, 3);

            return WorldLayer.Named("skyislands")
                .Bounds(bottom, top)
                .Root(root)
                .Metadata(new WorldLayerMetadata("Sky Islands",
                    skyTint: new float4(0.62f, 0.76f, 1f, 1f), ambientScale: 1.2f))

                //No bands. The density stage is the whole shape of this layer.
                .Density(new DensityStage("islands", field, DensityMode.Add,
                    //Add places solid where the field reaches the threshold. A high threshold leaves
                    //only the peaks of the field, which is what makes islands rather than a ceiling.
                    threshold: 0.36f,
                    resolution: new int3(4, 4, 4),
                    gradients: new[]
                    {
                        //Biased against solid at both ends, so the islands thin out into open sky
                        //instead of being sliced flat by the layer's bounds.
                        new DensityGradient(bottom, root, -0.5f, 0f),
                        new DensityGradient(root, top, 0f, -0.5f)
                    },
                    //Add mode needs to know what to place. Carve mode ignores this.
                    addBlock: "digblocks:stone"))
                .Build();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Hand-written Burst functions
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The two escape hatches, implemented.
    /// <para>
    /// Both must obey the same determinism rules as the rest of generation: strict float mode, and
    /// nothing transcendental. A <c>sin</c> here would quietly make the world differ between
    /// platforms, and no test would catch it until someone compared two machines.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class DemoBurstFunctions
    {
        public const string RippleId = "digblocks.demo:ripple";
        public const string DomeId = "digblocks.demo:dome";

        private static readonly object Gate = new object();
        private static bool registered;

        /// <summary>
        /// Registers both, once. Compilation happens here rather than at first use, because first use
        /// is on a chunk-generation worker and a compile stall there is a hitch in terrain streaming.
        /// </summary>
        public static void EnsureRegistered()
        {
            lock (Gate)
            {
                if (registered) return;
                NoiseExternalRegistry.Register(RippleId, Ripple);
                BandHeightRegistry.Register(DomeId, Dome);
                registered = true;
            }
        }

        /// <summary>
        /// A per-sample noise node. Receives the sample position, the seed derived for this node from
        /// the world seed, and the two operand values the expression supplied — here a frequency and
        /// an amplitude.
        /// <para>Concentric ripples, built from a distance and a fold rather than a trigonometric wave.</para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(NoiseExternal))]
        private static float Ripple(float x, float y, float z, uint seed, float frequency, float amplitude)
        {
            //The seed shifts the pattern's centre, so two nodes with this function do not coincide.
            float centreX = GenHash.SignedFloat(seed) * 512f;
            float centreZ = GenHash.SignedFloat(GenHash.Mix32(seed)) * 512f;
            float dx = x - centreX, dz = z - centreZ;

            float distance = math.sqrt(dx * dx + dz * dz) * frequency;
            //A triangle wave: fract, folded about a half. Exact under IEEE-754, unlike a sine.
            float phase = distance - math.floor(distance);
            return (1f - math.abs(phase * 2f - 1f) * 2f) * amplitude;
        }

        /// <summary>
        /// A whole band heightmap, filling a batch of columns at once. This is the level to work at
        /// when the surface itself is what you want to write by hand.
        /// <para>A broad dome, so the deeplands have a ceiling that rises toward the middle.</para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(BandHeightFunction))]
        private static void Dome(float* x, float* z, float* result, int count, uint seed)
        {
            //One draw from the seed, hoisted out of the loop: the whole batch shares it.
            float wobble = GenHash.SignedFloat(seed) * 6f;
            for (int index = 0; index < count; index++)
            {
                float dx = x[index] * 0.0025f, dz = z[index] * 0.0025f;
                float falloff = math.max(0f, 1f - (dx * dx + dz * dz));
                result[index] = -190f + falloff * 24f + wobble;
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // Seams
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// SEAM, NOT WIRED UP. A layer stores its features and nothing calls <see cref="Place"/> yet.
    /// This is written out so the contract's shape is visible from a real world type.
    /// <para>
    /// The rule that matters: a feature must never write into a neighbouring chunk. Instead it
    /// enumerates the feature origins in a radius around itself and draws whatever parts of them land
    /// inside its own bounds. Both chunks then place a straddling feature identically, whichever is
    /// generated first, with no communication and no ordering requirement.
    /// </para>
    /// </summary>
    public sealed class DemoFeature : IFeatureGenerator
    {
        //A jittered region grid: the world is divided into regions, each of which either holds one
        //placement or does not, decided by hashing the region against the seed. Position within the
        //region is jittered so it does not read as a grid, while separation stays bounded because two
        //placements can never share a region.
        private readonly RegionGrid grid = new RegionGrid("demo.monoliths", spacing: 96, jitter: 80, density: 0.35f);

        public string Name => "demo.monoliths";

        /// <summary>
        /// How far away a placement can still reach into this chunk. The generator is asked about
        /// every origin within this many chunks, which is what makes a straddling feature agree.
        /// </summary>
        public int ChunkRadius => 1;

        public void Place(IChunkContext chunk)
        {
            int baseX = chunk.ChunkX * ChunkLayout.Edge, baseZ = chunk.ChunkZ * ChunkLayout.Edge;
            int reach = ChunkRadius * ChunkLayout.Edge + ChunkLayout.Edge / 2;

            //Ask about every region that could reach this chunk, including ones centred outside it.
            grid.Around(chunk.Seed, baseX + ChunkLayout.Edge / 2, baseZ + ChunkLayout.Edge / 2, reach, placement =>
            {
                //Where does the ground sit under this placement? The column plan already knows, so a
                //feature never has to search for the surface.
                if (!chunk.TryGetSurfaceY(placement.WorldX, placement.WorldZ, out int surfaceY)) return;

                //Each placement carries its own seed, derived from the grid and its region, so a
                //feature's own random choices are as reproducible as its position.
                int height = 4 + (int)(GenHash.UnitFloat((uint)placement.Seed.Value) * 8f);

                for (int step = 0; step < height; step++)
                {
                    //Convert to this chunk's local space and skip anything outside it. This is the
                    //clipping that lets a feature originating in a neighbour be drawn here safely.
                    int localX = placement.WorldX - baseX;
                    int localZ = placement.WorldZ - baseZ;
                    int localY = surfaceY + 1 + step - chunk.MinY;
                    if ((uint)localX >= ChunkLayout.Edge || (uint)localZ >= ChunkLayout.Edge) continue;
                    if ((uint)localY >= ChunkLayout.Edge) continue;

                    //Only grow into open space, so a monolith never eats terrain it happens to meet.
                    if (chunk.GetSolid(localX, localY, localZ) != 0u) continue;
                    chunk.SetSolid(localX, localY, localZ, 0u); //placeholder: a real feature writes a block id
                }
            });
        }
    }

    /// <summary>
    /// SEAM, NOT WIRED UP. A layer stores its biome source and nothing consults it yet.
    /// <para>
    /// The intended shape: read the same control fields the height already uses, so biome and terrain
    /// agree by construction rather than by two separately tuned noise stacks happening to line up.
    /// A biome source that sampled its own independent fields would put deserts on mountainsides.
    /// </para>
    /// </summary>
    public sealed class DemoBiomes : IBiomeSource
    {
        private readonly TerrainShape shape;

        public DemoBiomes(TerrainShape shape) => this.shape = shape ?? throw new ArgumentNullException(nameof(shape));

        public string Name => "demo.biomes";

        /// <summary>
        /// Will resolve a world position to a biome id. The eventual implementation reads the shape's
        /// compiled fields at this position rather than re-deriving anything, which is why the source
        /// is handed the shape rather than building its own.
        /// </summary>
        public int Resolve(int worldX, int worldY, int worldZ) => 0;
    }
}
