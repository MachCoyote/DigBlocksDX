# Terrain generation implementation summary

## Status

Complete and verified, September 10, 2026. Terrain generation replaced the
bootstrap fixture as the authoritative source of chunk contents.

Reference documentation is [terrain-generation.md](terrain-generation.md). The
design this was built from is [terrain-generation-plan.md](terrain-generation-plan.md),
kept as a historical record.

## What shipped

`DigBlocks.Voxels.Generation`, a new assembly under `Scripts/Voxels/Generation/`
referencing `DigBlocks.Voxels` alone. It holds no ECS, transport or engine
knowledge, so it stays usable from EditMode tests and would be usable from
offline tools.

| Piece | Where |
| --- | --- |
| Seeds, integer hashing, seeded gradient and value noise | `GenSeed`, `GenHash`, `GenNoise` |
| Expression authoring, compiler, Burst kernel | `NoiseExpr`, `NoiseCompiler`, `NoiseKernel`, `NoiseProgram` |
| Splines with monotone tangents | `Spline` |
| Bands, recipes, column planning, fill | `SurfaceBand`, `ColumnRecipe`, `LayerColumnPlan`, `ColumnFillKernel` |
| Three-dimensional shaping, gradients, aquifers | `DensityStage`, `DensityGradient`, `AquiferStage`, `DensityKernel` |
| Layers, world types, settings schema | `WorldLayer`, `TerrainGeneratorDefinition`, `GeneratorSettings` |
| Runtime | `TerrainGenerator`, `CompiledLayer`, `GenerationContext` |
| Authoring helpers | `TerrainShape`, `RegionGrid` |
| Shipped world types | `ClassicGenerator`, `FlatGenerator`, `DemoGenerator` |
| Bootstrap binding | `GeneratedTerrainChunkSource` |

Block content gained `water` (fluid), `sand`, `gravel` and `cobblestone`, and the
opaque material's declared slice count grew to cover the atlas's authored tiles.

## Decisions and why

**The chunk source contract did not change.** `IAuthoritativeChunkSource.Generate`
still takes managed `uint[]` on a `ThreadPool` worker. Generation reaches Burst
through `BurstCompiler.CompileFunctionPointer` and writes through a pinned
pointer, so it gets full SIMD without the job system's safety machinery, which
does not belong on a pooled thread. This avoided reworking a load path that had
recently been stabilised for incarnation recovery.

**Coordinates are ordinary expressions.** Every noise op takes coordinate
operands rather than reading an implicit sample position. Domain warping then
needs no special case, composes to any depth, and the compiled program stays a
flat register machine with no re-entrant subtree evaluation.

**Seeds derive by name, not by position.** Inserting a node into a generator
leaves its siblings' worlds untouched. Ordinal derivation would silently
regenerate every existing world on any edit.

**Cross-platform reproducibility banned more than expected.** No `sin`, `cos`,
`exp`, `pow`, and no `Unity.Mathematics.noise.*`, because transcendental accuracy
is not contracted across Burst backends and Unity's noise has no seed hook. Hence
our own hash-based gradient noise, with a quintic fade that is polynomial and
therefore exact.

**One kernel, two callers.** The managed and Burst paths run the same source, so
a test can hold them to bit-exact agreement and any divergence is provably a
codegen difference rather than two implementations drifting apart.

**Painting order is authored order.** Layers are sorted by bottom bound only for
the bounds query; they are painted in the order the author wrote them. Nothing
guards against overlap, because overlap is a composition tool.

**Per-worker column plans, not a shared cache.** Measured rather than assumed;
see the evidence below.

## What was found while building it

**The first classic world had no oceans.** Fractal noise averages its octaves, so
its values crowd near zero and only brush the ends of `[-1, 1]`. Splines authored
across that whole range were therefore only ever asked about their middle, and
nothing reached the low end of the continentalness curve. Fixed with `Spread`,
which maps a field through `x / (|x| + k)` — smooth, monotone, never saturating.
`Ridge()` was added for the same reason: `FbmMode.Ridged` folds a field that
still crowds around zero, which puts nearly every column on a crest and lifts the
whole world rather than shaping it.

**Constructors allocated before they validated.** Four of them took native memory
and then threw on a bad block key or a surface that sampled Y, leaking what they
had taken. Found by Unity's leak detector reporting persistent allocations after
a test run, and fixed by resolving everything fallible before allocating
anything. `CompiledRecipe` had the same fault and was fixed later, found the same
way.

**The development camera was tuned to the fixture.** It started at Y 48, which
was above the old fixture's Y 0–25 terrain and inside the generated world's
stone. The render tests failed with meshed chunks and zero visible terrain, which
is exactly what standing inside solid ground looks like.

**Ground exactly at sea level is dry sand.** It occupies the topmost water cell,
so the shore rule applies but no water sits above it. That is the beach, one
block proud of the water beside it — correct behaviour that an over-strict test
assertion initially called a failure.

## Verification

Run against the live editor on September 10, 2026, after all changes.

| Suite | Result |
| --- | --- |
| EditMode, whole project | 382 passed, 0 failed, 4 skipped |
| PlayMode, whole project | 68 passed, 0 failed |
| Generation EditMode assembly | 144 passed |
| `tools/Test-LeanWorkflow.ps1` | passed |

The four skipped tests are the pre-existing `ChunkPipelineProfileTests` profiling
harness, marked explicit and run by name.

Behavioural contracts under test:

- `GenHash.SplitMix64` matches the published reference vector, and named seed
  derivation is pinned so a "tidy-up" cannot silently regenerate every world.
- Inserting a node leaves its siblings seeded identically.
- The Burst kernel and the managed kernel agree bit-for-bit across 512 samples of
  a program using warping, splines, ridged fractals and terracing.
- A column is identical regardless of which chunk generated it, horizontally and
  vertically, so chunk boundaries are seamless by construction.
- A chunk touching no layer evaluates nothing and returns all air.
- Sixteen threads generating the same chunk concurrently agree with a reference
  copy, which is what makes the shared immutable generator plus per-call context
  safe.
- Carving a known sphere hollows out that sphere; a coarse lattice differs from
  per-cell sampling only in a shell around the boundary.
- A density gradient closes a layer at floor and ceiling while leaving its middle
  free to be either, which is what separates it from a clamp.
- Aquifers leave some carved regions dry and flood others; a region holds one
  flat level throughout.
- `digblocks:classic` and `digblocks:flat` match recorded fingerprints.

Visual confirmation came from the existing `TerrainScene_RendersAndRestarts`
PlayMode test, which captures a frame and counts grass pixels; its screenshot is
written to `.utmp/terrain-world.png`.

## Measurements

Column plans do not depend on a chunk's Y, so a vertical stack could share one. A
`GenerationContext` keeps its plans between calls; `TerrainGenerator.PlansComputed`
and `PlansReused` expose what that achieves:

| Case | Plans computed | Chunks |
| --- | --- | --- |
| One worker, one column | 1 | 12 |
| One worker, two columns | 2 | 8 |
| Eight workers, same column each | fewer than half | 80 |

A cache shared across workers would recover the remaining slice at the cost of
locking. It was not built, because the numbers above say most of the win is
already taken; they are what a future attempt should be judged against.

Sampling cost, by construction rather than by measurement: a heightmap band costs
1024 noise columns per chunk. A density stage at the default 4×8×4 lattice costs
405 three-dimensional samples per chunk instead of 32768, which is what makes
opting a layer into three dimensions cost about what a heightmap costs.

## Not done

- **Fluid rendering.** Water is generated into the fluid channel and replicated,
  but nothing meshes fluids and `TerrainRenderer` accepts opaque materials only,
  so oceans read as empty basins. Shores are still correct, because the water
  line decides them whether or not the water can be seen.
- **Vertical streaming for many layers.** `VerticalRenderDistanceChunks` covers a
  fixed band around the viewer. Layers far apart in Y need it raised, which costs
  bandwidth and residency and should be a measured change.
- **World persistence.** A randomised seed resolves per session; world type and
  seed are serialized fields on `DigBlocksBootstrap` until world creation exists.
- **Biomes, features, structures, ores, caves.** Seams declared and documented;
  `RegionGrid` is implemented and tested but unused.
- **An editor preview window**, deferred by request during planning.
