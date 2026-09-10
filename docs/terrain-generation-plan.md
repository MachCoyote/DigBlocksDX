# Terrain generation plan

## Status

Historical. This is the design the work was planned from; the implementation is
recorded in [terrain-generation.md](terrain-generation.md), which is current where
the two differ. Approved September 10, 2026.
Phase 1 delivers deterministic layered terrain and replaces the bootstrap
fixture. Biomes, trees, structures, ores and caves are explicitly out of scope
and are represented only as named seams.

## Goal

A `TerrainGenerator` subsystem that produces chunk contents as a pure function of
`(worldSeed, generatorSettings, ChunkAddress)`, composed from named world layers
that each own their own noise stack, block recipes and (later) feature
generation. One generator, `digblocks:classic`, is authored and hooked into the
existing authoritative chunk source so the current direct/streamed play path
renders real terrain.

Out of scope for this phase: biomes, trees, structures, ores, caves, cavities,
world persistence, the settings UI, and the editor preview window. Each has a
declared seam and no implementation.

## Decisions

Settled with the project owner before planning:

| Decision | Choice |
| --- | --- |
| Layer shape | 2D surface bands, with an opt-in 3D density stage per layer |
| Noise authoring | Compiled noise program, plus hand-written Burst overrides |
| Burst integration | Burst function pointers over the existing `uint[]` contract |
| Seed reproducibility | Cross-platform bit-identical |
| Height model | Direct amplitude by default, splines available as an op |
| World vertical extent | Unbounded; per-layer bounds drive an early-out |
| Block palette | Terrain essentials added now (water, sand, gravel, deep stone) |
| Extras included | Domain warping, column-plan cache, sea level and water fill |
| Extras deferred | Editor preview window (its own later task) |

## Constraints

- `IAuthoritativeChunkSource.Generate(ChunkAddress, uint[], uint[])` runs on a
  `ThreadPool` worker. No Unity APIs, no native containers owned by the store, no
  job-system participation. The contract does not change.
- `ChunkLayout.Index` is `x + 32 * (z + 32 * y)`, so a vertical column is strided
  by 1024. Generation must plan per column and write per y-slice.
- Buffers arrive cleared to air and are reused between calls; nothing may be
  retained past the call.
- Only the server generates. Clients receive streamed chunks, so client/server
  float agreement is not required, but seed sharing between machines is, which
  forces strict float behaviour anyway.
- `ChunkStreamingSettings` currently streams `VerticalRenderDistanceChunks: 2`,
  covering world Y -64..95. The first generator must be visible inside that band.
- Generation output is validated by the store: state ids must exist in the
  registry and fluids may only occupy cells whose solid `PermitsFluid`.

## Determinism rules

The whole assembly obeys these so a seed reproduces bit-identically on every
platform and Burst backend:

- `[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]`
  on every generation entry point. Strict mode forbids reassociation and FMA
  contraction.
- Permitted float operations: `+ - * /`, `min`, `max`, `abs`, `floor`, `ceil`,
  `round`, `sqrt`, `clamp`, `lerp`, `select`, comparisons. All are correctly
  rounded or exact under IEEE-754.
- Banned: `sin`, `cos`, `tan`, `exp`, `log`, `pow`, `rsqrt`, fast normalize, and
  `Unity.Mathematics.noise.*`. Transcendental accuracy is not guaranteed
  identical across backends, and Unity's noise gives no seeding hook.
- Octave summation and every other reduction runs in a fixed scalar order.
- All seeding is integer math (splitmix64 / FNV-1a), never float derived.

## Affected boundaries

New assembly `DigBlocks.Voxels.Generation` at
`Assets/_Project/Scripts/Voxels/Generation/`, referencing `DigBlocks.Voxels`,
`Unity.Collections`, `Unity.Mathematics`, `Unity.Burst`, with
`allowUnsafeCode: true` for function pointers and pinned buffer writes.

It does not reference `DigBlocks.Voxels.Runtime`, which would pull
`Unity.Entities` into a subsystem that has no use for it. The
`IAuthoritativeChunkSource` implementation is a thin adapter that stays in
`DigBlocks.Bootstrap`, where `TerrainFixtureChunkSource` lives today.

```text
DigBlocks.Bootstrap
    |  GeneratedTerrainChunkSource : IAuthoritativeChunkSource
    v
DigBlocks.Voxels.Generation  --->  DigBlocks.Voxels   (BlockRegistry, ChunkLayout)
```

Changed elsewhere: `DigBlocksBootstrap.ComposeSession` builds the generator
instead of the fixture; `Assets/StreamingAssets/content/digblocks/` gains block
definitions; `docs/architecture.md` gains a terrain generation section.

Ownership note: the built generator owns `Allocator.Persistent` native programs,
so the adapter is `IDisposable` and is registered as an `IGameService` in
`ComposeSession` ahead of the chunk companion. The current source is passed as a
bare object and never disposed; that gap closes with this change.

## Model

### Seeds

`GenSeed` wraps a `ulong` world seed. Sub-seeds derive by name, not by ordinal,
so inserting a noise node does not reshuffle every other node's seed and
invalidate existing worlds:

```csharp
GenSeed layer = world.Derive("overworld");
GenSeed node  = layer.Derive("continents");
```

A supplied seed of `0` resolves once to a random non-zero seed at world creation
and the resolved value is what the generator carries, is logged, and will later
be persisted.

### Noise expressions

Authoring is a fluent immutable expression tree. Coordinates are ordinary
expressions, which is what makes domain warping compose without a special case:

```csharp
var warpX      = NoiseExpr.Perlin2D("warp.x", NoiseExpr.X, NoiseExpr.Z, frequency: 1f / 220f, octaves: 2);
var warpZ      = NoiseExpr.Perlin2D("warp.z", NoiseExpr.X, NoiseExpr.Z, frequency: 1f / 220f, octaves: 2);
var wx         = NoiseExpr.X + warpX * 28f;
var wz         = NoiseExpr.Z + warpZ * 28f;

var continents = NoiseExpr.Perlin2D("continents", wx, wz, 1f / 512f, octaves: 4);
var hills      = NoiseExpr.Perlin2D("hills", wx, wz, 1f / 96f, octaves: 3, mode: FbmMode.Ridged);

var height     = 64f
               + continents.Spline((-1f, -22f), (-0.15f, 1f), (0.25f, 16f), (1f, 78f)) * amplification
               + hills * 11f;
```

`Spline` maps what a noise value means in height, which is how flat plains and
sharp mountains come from one field. It is optional; plain amplitude scaling is
the default and stays a one-liner.

### Compiled programs

`NoiseProgram.Compile(expr, seed)` flattens the tree into a register machine:

```csharp
enum NoiseOpCode { CoordX, CoordY, CoordZ, Const, Add, Sub, Mul, Div, Min, Max,
                   Abs, Neg, Floor, Sqrt, Clamp, Lerp, Remap, Select,
                   Perlin2D, Perlin3D, Value2D, Value3D, Spline, Terrace, CallExternal }

struct NoiseOp { public NoiseOpCode Code; public int Dst, A, B, C; public float P0, P1; public int Aux; }
```

The compiled program holds `NativeArray<NoiseOp>`, a constant pool, a spline
point pool, per-node resolved seeds, a slot count, and a table of
`FunctionPointer<NoiseExternal>` for `CallExternal`. Structurally identical
subtrees are deduplicated at compile time, so the warp field above is evaluated
once even though four expressions reference it.

Evaluation is columnar: one op runs over the whole batch of 1024 sample points
before the next op starts, so switch dispatch amortises to nothing and the
arithmetic ops vectorise. Scratch is `slotCount * 1024` floats, rented from a
per-call generation context.

### Hand-written Burst overrides

Two escape hatches, both requested:

- Node level: `NoiseExpr.External(id, a, b)` compiles to `CallExternal`, invoking
  a registered `[BurstCompile]` static
  `float NoiseExternal(float3 coord, ulong seed, float a, float b)`.
- Band level: a `SurfaceBand` may supply a `FunctionPointer<BandHeightFunction>`
  instead of a `NoiseExpr`, bypassing the program entirely for that surface.

Function pointers compile eagerly when the generator is built, on the main
thread, so no worker pays first-call compilation latency.

### World layers and bands

```csharp
sealed class WorldLayer
{
    public string Name;
    public int RootY, BottomBound, TopBound;
    public WorldLayerMetadata Metadata;   //display name, sky tint, fog, ambient - free-form typed bag
    public SurfaceBand[] Bands;           //painted in order; later bands overwrite
    public int? SeaLevel; public string SeaFluid;
    public DensityStage Density;          //null unless the layer opts into 3D
    public IFeatureGenerator[] Features;  //seam - empty in phase 1
    public IBiomeSource Biomes;           //seam - null in phase 1
}

sealed class SurfaceBand
{
    public string Name;
    public BandDirection Direction;  //Up: free face on top. Down: free face underneath.
    public NoiseExpr Surface;        //world Y of the free face
    public NoiseExpr Extent;         //world Y the fill runs to; defaults to the layer bound
    public ColumnRecipe Fill;
}
```

A band produces a solid interval per column. `Up` fills `[Extent, Surface]` with
depth measured downward from `Surface`; `Down` fills `[Surface, Extent]` with
depth measured upward from `Surface`. The owner's example, stone with rolling
upside-down grass on its underside, is one `Up` band plus one `Down` band whose
surface sits near the layer bottom.

No protection against overlapping layers or bands is provided. Order is the
contract: later writes win.

### Column recipes

Painting is by depth from the band's free face, which is the surface-rule model
that later carries biomes:

```csharp
ColumnRecipe.Create()
    .Layer("digblocks:grass_block", 1)
    .Layer("digblocks:dirt", 3)
    .Deep("digblocks:stone")
    .BelowY(0, "digblocks:deepslate")
    .UnderSeaLevel("digblocks:sand", depth: 3);
```

Block keys resolve to `uint` state ids once at generator build time. The compiled
recipe is unmanaged; the hot loop compares a cell's Y against precomputed
per-column boundary heights rather than walking strata.

## Generation algorithm

Layers are held sorted by `BottomBound`. For a chunk spanning world
`[minY, minY + 31]`, a binary search finds the first candidate and the walk stops
once `BottomBound > maxY`. A chunk that overlaps no layer returns immediately
having evaluated nothing, so a layer 5000 blocks in the air costs nothing to the
rest of the world and the world stays vertically unbounded.

Per overlapping layer:

1. Plan the columns. Batch-evaluate each band's `Surface` and `Extent` programs
   over the chunk's 1024 `(x, z)` points, quantise to integer Y, and derive each
   column's recipe boundary heights. This is the unit the column-plan cache
   stores, keyed by `(layer, chunkX, chunkZ)` and shared by every chunk in a
   vertical stack.
2. Fill by y-slice. For each of the 32 y values, walk the bands in order and
   write the 1024 contiguous cells of that slice. Writes stay sequential in the
   chunk's memory layout instead of striding by 1024 per column.
3. Density stage, when the layer opts in. Sample the 3D density program on a
   coarse grid (4x8x4 by default, 9x5x9 = 405 samples instead of 32768) and
   trilinearly interpolate, then carve or add. This is where caves, cavities and
   floating islands attach later.
4. Sea fill. Below `SeaLevel`, cells left as air within the layer's bounds take
   the sea fluid, subject to the registry's `PermitsFluid`.
5. Features. No-op in phase 1. The declared contract passes a chunk context that
   can enumerate deterministic feature origins within a chunk radius, which is
   how cross-chunk trees and structures stay order-independent.

## Generators and settings

```csharp
abstract class TerrainGeneratorDefinition
{
    public abstract string Id { get; }            //"digblocks:classic"
    public abstract string DisplayName { get; }
    public abstract GeneratorParameterSchema Schema { get; }
    public abstract TerrainGenerator Build(GeneratorBuildContext context);
}
```

`GeneratorParameterSchema` declares named, typed, ranged, defaulted parameters
with display text. That is enough for the eventual settings UI to be generated
without reflection, while generators stay plain code. `GeneratorSettings` is a
validated value bag. `TerrainGeneratorRegistry` is a static code registry.

`digblocks:classic`, the only generator this phase, exposes `amplification` (the
amplified-mode knob, scaling the height spline rather than raw noise),
`terrainScale` and `seaLevel`. It defines one `overworld` layer rooted at Y 64
with a `terrain` band and a `bedrock` band, sea level 62, and metadata carrying a
display name and a sky tint for later use.

## Implementation steps

Each step compiles and is verified on its own.

1. Determinism core. Assembly and asmdef, `GenSeed`, integer hashing, seeded
   2D/3D gradient and value noise, the determinism rules above. Tests: golden
   hash and noise values, seed derivation stability under node insertion.
2. Noise programs. `NoiseExpr` builder and operators, compiler with
   subexpression deduplication, Burst columnar evaluator, the full op set
   including `Spline` and `CallExternal`. Tests: compiled output equals a
   reference managed evaluation of the same tree; batch equals single sample;
   deduplication does not change results.
3. Columns. `SurfaceBand`, `ColumnRecipe` and its compiled form, column planning
   and y-slice filling. Tests: flat and ramped synthetic surfaces, `Up` and
   `Down` bands, depth painting, band overwrite order.
4. Layers and generators. `WorldLayer`, metadata, sorted bounds with the
   binary-search early-out, `TerrainGeneratorDefinition`, registry, parameter
   schema, seed-0 randomisation. Tests: a chunk outside all bounds evaluates no
   program; overlapping layers resolve by order; settings validation.
5. Sea level and fluids. Fill stage honouring `PermitsFluid`.
6. Block content. `water` (fluid), `sand`, `gravel` and a deep stone definition,
   with archetypes, materials and textures.
7. The classic generator and wiring. `ClassicGenerator`, the
   `GeneratedTerrainChunkSource` adapter registered as a disposable
   `IGameService`, replacing `TerrainFixtureChunkSource` in `ComposeSession`.
   Golden-hash test over a fixed chunk set. PlayMode: streaming, direct delivery
   and meshing still produce terrain.
8. 3D density stage. Coarse grid plus trilinear interpolation, carve and add.
   Tested against a synthetic density that carves a known sphere. Unused by
   `classic` at first.
9. Column-plan cache. Shared bounded cache with striped locking, measured before
   and after. Independently revertible.
10. Seams. `IFeatureGenerator`, `IBiomeSource`, `ChunkContext`,
    `WorldLayerMetadata` defined and documented, deliberately unimplemented.

Steps 1 through 7 are the deliverable. Steps 8 and 9 may be reordered or
deferred on measurement.

## Verification

Behavioural contracts under test:

- Same seed and address gives identical output across repeated calls, across
  worker threads, and against a checked-in golden hash.
- A column's terrain is identical regardless of which chunk generated it, both
  horizontally and vertically, so chunk boundaries are seamless by construction.
- Chunks outside every layer's bounds are all air and evaluate nothing.
- Fluids only occupy cells the registry permits.
- Compiled programs agree with reference evaluation of the same expression tree.

Focused checks during development are the Generation EditMode assembly. The
blast radius widens at step 7, where the existing chunk streaming, direct
delivery and meshing PlayMode tests must stay green, plus a fresh compile.

## Known follow-ups

- `VerticalRenderDistanceChunks` is 2. A second world layer roughly 100 blocks
  below the overworld needs about 5, which costs streaming bandwidth and
  residency; that is a measured change, not part of this phase.
- Worlds are not persisted, so a randomised seed currently resolves per session.
- The editor preview window is a later task and is not designed here.
