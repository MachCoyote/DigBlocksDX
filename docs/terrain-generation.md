# Terrain generation

## Status

Implemented and in use, September 10, 2026. `DigBlocks.Voxels.Generation` is the
authoritative source of chunk contents for single-player and dedicated servers.
The plan this was built from is [terrain-generation-plan.md](terrain-generation-plan.md);
where the two differ, this document is current.

Biomes, trees, structures, ores and caves are not implemented. Their seams are
declared and named below.

## What a world is

```text
TerrainGeneratorDefinition        a world type, written in code
        | Build(seed, settings)
        v
TerrainGenerator                  one world: seed, settings, compiled layers
        |
        v
WorldLayer[]                      painted in authored order, later writes win
        |
        +-- SurfaceBand[]         a solid interval per column, plus what fills it
        +-- DensityStage?         optional 3D shaping, carve or add
        +-- AquiferStage?         optional local water tables
        +-- sea level, metadata, feature and biome seams
```

A world type is a description and holds no state, so one definition serves every
world made from it. A built generator is immutable and shared across generation
workers; all mutable scratch lives in a `GenerationContext` rented per call.

## Determinism

A seed reproduces the same world on every platform, backend and Burst version.
That is a hard constraint, not an aspiration, and it shapes the code:

- Every generation entry point carries
  `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]`.
  Strict mode forbids reassociation and FMA contraction.
- Permitted float operations are `+ - * /`, `min`, `max`, `abs`, `floor`, `ceil`,
  `round`, `sqrt`, `clamp`, `lerp`, `select` and comparisons. All are exact or
  correctly rounded under IEEE-754.
- **Never** use `sin`, `cos`, `tan`, `exp`, `log`, `pow`, `rsqrt`, fast normalize,
  or `Unity.Mathematics.noise.*`. Transcendental accuracy is not contracted to be
  identical across backends, and Unity's noise offers no seed hook.
- Reductions run in a fixed scalar order.
- All seeding is integer arithmetic. `GenHash.SplitMix64` is pinned against the
  published reference vector by test.

Seeds derive **by name**, never by position:

```csharp
GenSeed node = world.Derive("overworld").Derive("continents");
```

Adding a node to a generator therefore leaves its siblings' worlds untouched.
Renaming one regenerates that node's world, which is the intended trade.

A supplied seed of `0` resolves once to a random non-zero value; the resolved
value is what the generator carries and what a save would record.

## Authoring a field

Noise is authored as an immutable expression tree and compiled once, when the
generator is built, into a flat instruction list a Burst kernel evaluates a batch
of 1024 sample points at a time.

Coordinates are ordinary expressions. That single decision is what lets domain
warping compose without a special case:

```csharp
var (wx, wz) = NoiseExpr.Warp2D("warp", NoiseExpr.X, NoiseExpr.Z, 1f / 220f, 28f);
var hills = NoiseExpr.Perlin2D("hills", wx, wz, 1f / 96f, octaves: 3);
var height = 64f + hills * 18f;
```

Identical subexpressions collapse to one instruction, so the warp field above is
evaluated once however many samplers read it. Slots are recycled once their last
consumer has run, so a long program still runs out of a small working set.

### Spread before you shape

Fractal noise averages its octaves, so its values crowd near zero and only brush
the ends of `[-1, 1]`. A spline authored across that range would only ever be
asked about its middle. `Spread` maps a field through `x / (|x| + k)`, which is
smooth, monotone and never saturates:

```csharp
var continents = NoiseExpr.Perlin2D("continents", wx, wz, 1f / 900f, 4).Spread(0.30f);
```

This is not a nicety. The first classic world had no oceans at all because
nothing ever reached the low end of its continentalness curve.

`Ridge()` folds a field about zero (`1 - 2|x|`) so its zero crossings become
crests. Apply it **after** `Spread`, not by using `FbmMode.Ridged`: folding a
field that still crowds around zero puts nearly every column on a crest and lifts
the whole world instead of shaping it.

### Splines

`Spline` maps what a noise value *means*, which is how flat plains sit beside
sharp mountains without a second field arbitrating between them. Tangents are
solved once, at build time, under the Fritsch-Carlson conditions, so a smooth
curve never overshoots its control points. Overshoot in a height curve is a spike
the author never asked for.

### Hand-written escapes

Two levels, both registered by id and compiled on the registering thread so no
generation worker discovers it needs to compile something:

- `NoiseExpr.External(id, name, a, b)` calls a registered `NoiseExternal` per sample.
- `BandSurface.Function(id, seedName)` replaces a band's whole heightmap with a
  registered `BandHeightFunction` that fills a batch.

Both must obey the determinism rules above.

## Shape

A `SurfaceBand` produces one solid interval per column from a free face and an
extent. `Up` fills from its extent up to its surface with depth measured
downward; `Down` fills from its surface up to its extent with depth measured
upward. A layer's stone body with rolling grass hanging off its underside is one
band of each.

`ColumnRecipe` paints that interval by depth from the free face rather than by
absolute height, because depth stays meaningful when the surface moves and is the
axis biome surface rules will key on:

```csharp
ColumnRecipe.Create()
    .Layer("digblocks:grass_block")
    .Layer("digblocks:dirt", 3)
    .Deep("digblocks:stone")
    .DeepBelow(0, "digblocks:deepslate")
    .Submerged("digblocks:sand", 3)
    .Crest(180, "digblocks:snow", 2);
```

`Submerged` and `Crest` are tested ahead of the strata, so a beach or a snow cap
replaces the surface material rather than having to be woven into every recipe
that might meet water or altitude.

Nothing guards against overlapping bands or layers. Order is the contract.

## Three dimensions

A layer opts in with a `DensityStage`. The field is sampled on a coarse lattice
and interpolated between, not evaluated per cell: a chunk holds 32768 cells and a
four by eight by four lattice holds 405. The spacing must divide 32 evenly or
neighbouring chunks would interpolate between different sample points and the
result would step at the seam.

`DensityGradient` biases the field by height, so terrain closes off at the ends
of a layer without a hard clamp and stays free to overhang in between.

`AquiferStage` gives each region its own flat water level, drawn from the seed
and the region alone so two chunks sharing a region agree without consulting each
other. Where a layer has aquifers they own the water below their top and the sea
owns only what is above it; a blanket sea fill would otherwise flood every carved
cavern to the floor and no region could be dry.

## How a chunk is generated

1. Rule out layers. Layers are indexed by bottom bound with a running maximum of
   the top bounds, so a chunk finds its candidates with a binary search and a
   short walk. A chunk touching no layer returns having evaluated nothing, which
   is what keeps the world vertically unbounded rather than merely large.
2. Plan the columns. Each band's surface and extent are evaluated over the
   chunk's 1024 `(x, z)` points and quantised to integer Y. This does not depend
   on the chunk's Y.
3. Fill by y-slice. A chunk indexes as `x + 32 * (z + 32 * y)`, so a column
   strides by 1024 while a slice is 1024 contiguous cells.
4. Carve or add, if the layer has a density stage.
5. Fill the sea, then the aquifers.
6. Place features. Not implemented.

### Column plan reuse, measured

A plan is independent of the chunk's Y, so a vertical stack could share one. A
`GenerationContext` keeps its plans between calls, and the measured result is:

| Case | Plans computed |
| --- | --- |
| One worker, twelve chunks of one column | 1 |
| Eight workers, ten chunks of one column each | fewer than half of 80 |

`TerrainGenerator.PlansComputed` and `PlansReused` expose this. A shared cache
across workers would recover the remaining slice at the cost of locking; the
numbers above are what it should be judged against.

## Shipped world types

`BuiltInGenerators.RegisterAll()` registers them; registration is explicit rather
than discovered by reflection, so which world types exist is answerable by
reading one file.

- **`digblocks:classic`** — one overworld layer, root Y 64, bounds -64 to 320,
  sea level 62. Height comes from continentalness, erosion and peaks mapped
  through splines over warped coordinates. Parameters: `amplification`,
  `terrainScale`, `seaLevel`.
- **`digblocks:flat`** — level ground at a chosen height. For testing anything
  that is not terrain, and the smallest worked example of a definition.

`ShippedGeneratorTests` pins both against a recorded fingerprint. If a seed stops
producing that world, either a generator changed deliberately, in which case
update the constant, or something in the noise path drifted, in which case every
existing world has silently changed.

## Blocks and the water gap

Generation names blocks by key and resolves them once, at build time, through
`RegistryBlockResolver`.

Water is generated into the fluid channel, but **nothing meshes fluids yet**, so
it is invisible. `TerrainRenderer` accepts opaque materials only, and neither
`Voxels.Meshing` nor `Client.Rendering` reads the fluid channel at all. Water
therefore carries no appearance; giving it one would fail the renderer's own
check. Shores still work, because the water line decides them whether or not the
water can be seen.

Sand borrows the cobblestone texture slice until it has art of its own.

## Seams left for later

Declared, documented and deliberately unimplemented:

- `IFeatureGenerator` and `IChunkContext` — trees, ores, boulders, ruins. A
  feature never writes into a neighbouring chunk. It enumerates the feature
  origins in a radius around itself and draws whatever parts land inside its own
  bounds, so both chunks place a straddling feature identically whichever is
  generated first.
- `RegionGrid` — the deterministic enumeration that makes the above possible.
  Implemented and tested; nothing uses it yet.
- `IBiomeSource` — intended to read the same `TerrainShape` control fields the
  height already uses, so biome and terrain agree by construction rather than by
  two separately tuned stacks happening to line up.
- `WorldLayerMetadata` — carries a display name, sky tint, fog tint and ambient
  scale for presentation to interpolate across a layer boundary. Nothing reads it.

## Known follow-ups

- `ChunkStreamingSettings.VerticalRenderDistanceChunks` is 2. A second world
  layer roughly 100 blocks below the overworld needs about 5, which costs
  streaming bandwidth and residency.
- Worlds are not persisted, so a randomised seed resolves per session and the
  world type and seed are serialized fields on `DigBlocksBootstrap`.
- Fluid meshing and a transparent render pass, without which oceans read as
  empty basins.
- An editor preview window for authoring generators without entering play mode.
