# Chunk data architecture

Status: approved design direction; storage/registry components implemented as of
September 6, 2026. See [implementation progress](chunk-implementation-progress.md)
for verification and remaining integration.
The user approved the preceding design discussion and selected independent fluid
storage and authorized implementation with the independent carrier in the
[chunk networking design](chunk-networking-design.md).

## Goal and scope

Provide compact, extensible, job-friendly voxel storage for a server-authoritative
Unity Entities game targeting 32 players initially, with scaling beyond that as a
measurement goal rather than a guarantee. Support exploration in all three axes,
multiple terrain layers, block states, provenance, and sparse block entities.

Terrain generation, lighting, meshing algorithms, gameplay systems and save
migration remain outside this implementation slice. Transport is described in the
companion networking design. Use dummy data while implementing networking and
storage together in bounded slices.

## Ownership and dependencies

- Voxel storage owns coordinates, state references, channel representations,
  mutation rules, revisions, and sparse records. Keep it independent of NetCode
  and presentation; Unity Collections/Mathematics are appropriate dependencies.
- ECS integration owns resident chunk entities and scheduling. It may depend on
  Entities and storage without depending on NetCode.
- The server owns authoritative contents and simulation. Each client owns its
  replica and presentation. Singleplayer retains separate client/server worlds
  and logical data ownership; no shared mutable array shortcut.
- Networking consumes coherent snapshots/change records through storage APIs.
  Save and wire serialization can share concepts without sharing byte layouts.
- Actual assembly names and buffer APIs remain implementation decisions. Avoid
  introducing duplicate lifecycle owners or an oversized framework in advance.

## Coordinates and size

- Baseline: cubic 32 x 32 x 32 chunks (32,768 cells), independent in X, Y, and Z.
- Use one compile-time edge-length definition. Derive volume, masks, shifts,
  strides, bounds, allocation lengths, and coordinate conversions from it.
- Restrict supported sizes to validated powers of two initially; exercise 16,
  32, and 64 in tests/build variants. Do not scatter literals such as 5, 31, or
  32768 through consumers. Changing the definition before a build must adapt all
  supported consumers, including encoders and local-position representations.
- A chunk address combines world/dimension identity with integer 3D coordinates.
  Vertically separated terrain layers can share one world; separate dimensions
  are only necessary for logically separate spaces.
- Negative positions use floor division, not truncation toward zero. At size 32,
  block coordinate -1 maps to chunk -1 and local coordinate 31.
- Specify and test one flattening order before implementation; all channels and
  sparse local-position records must agree. The exact order is not yet selected.
- Bound supported coordinate arithmetic and use checked/wider intermediates where
  necessary. Large-world rendering/floating origin is a later concern; absolute
  floating-point transforms must not become persistent voxel identities.
- A missing/unloaded chunk is unknown, not an all-air chunk.
- Save and protocol headers must identify chunk size and schema. Changing the
  build constant does not migrate old saves. Explicit migration or clear rejection
  is required; never silently reinterpret a different-size payload.

## Identity is separate from encoding

Three concepts must remain distinct:

| Concept | Example | Purpose |
| --- | --- | --- |
| Stable block key | `digblocks:oak_stairs` | Definitions, modding, persistence |
| Runtime block-state ID | A `uint` | Lookup of one finite configured state |
| Chunk-local palette index | `7` | Compact reference to a state used in a chunk |

A runtime state identifies the block type plus finite structural properties, not
an individual placed block. Four directions times two halves times two waterlog
values would make sixteen states for one type; this illustrates combinatorial
growth, not our selected fluid representation. Keep independent fluids out of the
solid state's Cartesian product.

Do not cap the global registry at 1,024 block types or states. A packed ten-bit
index could address 1,024 states in a local palette while the global registry is
much larger. Runtime registration order is not a stable save identifier. Save
palettes must resolve stable keys/properties rather than persist unchecked runtime
integers. Missing-content preservation/substitution policy needs a later explicit
decision. Multiplayer registry compatibility is defined in the networking design.

Definitions are data-driven authoring content compiled into immutable runtime
tables/blob assets. Hot-path reads must not query per-cell string dictionaries or
instantiate managed definition objects per voxel. State enumeration and property
validation must detect invalid or excessively large definitions early.

## Runtime channel representation

Selected baseline direction:

1. Uniform: one state ID and no cell array.
2. Paletted8: byte cell indices for up to 256 palette entries.
3. Paletted16: ushort cell indices for larger palettes.
4. Direct runtime-ID fallback when a palette exceeds its supported range or is
   counterproductive. Exact promotion/fallback thresholds are benchmark decisions.

Palettes map local indices to global runtime state IDs. Air and an empty fluid
channel must be representable uniformly. Do not allocate dense air merely because
a chunk is resident. Palette entries can outlive their last cell reference until
scheduled compaction; never compact on every removal. Bound stale palette growth
and promote/compact/fallback safely rather than overflowing indices. A 64-cube
can contain more unique states than a ushort palette can represent.

| Cell encoding | Payload for 32-cube, excluding palette/overhead |
| --- | ---: |
| Uniform | One state ID |
| Tightly packed 4-bit | 16 KiB |
| Byte | 32 KiB |
| Tightly packed 10-bit | 40 KiB |
| Ushort | 64 KiB |
| Direct uint | 128 KiB |

Arbitrary bit packing is deferred for live storage, but remains suitable for
serialization and later measured optimization. It reduces memory bandwidth at
the cost of extraction, possible word crossings, repacking, and read-modify-write
ownership complexity. Different voxel indices can share a packed word, so they
are not automatically independent parallel writes. No representation is claimed
to be universally fastest before benchmarking.

## Independent fluids: selected

Each chunk supports an independent optional fluid channel. Its default is a
uniform empty value, not another mandatory dense array. Fluid identity and finite
properties such as level belong to fluid definitions/state lookup, independently
of the solid palette.

Solid/fluid coexistence must follow explicit compatibility and occupied-volume
rules. The channel does not imply arbitrary fluid mixing, multiple fluids in one
cell, realistic fluid simulation, or fluid support in every solid. Those gameplay
rules remain open. Do not also persist a redundant solid waterlogged property as
an independently mutable source of truth. Derived queries can expose waterlogging
when appropriate. Solid/fluid edits that must be coherent form one mutation batch.

## Tags, flags, and sparse metadata

| Category | Examples | Storage |
| --- | --- | --- |
| Definition tags | Wood, flammable, pickaxe-mineable | Shared definition data |
| Finite states | Facing, slab half, growth stage | Runtime state ID |
| Per-position flags | Player-placed | Optional chunk-local bitsets |
| Block-entity records | Sign text, inventories, machine configuration | Sparse typed records |

Tags are semantic content groupings, not necessarily ECS tags. Do not allocate a
tag collection for each voxel. Player-placed provenance is not a palette state:
one bit per cell costs 4 KiB at size 32, allocated only when needed. It records a
boolean, not who placed a block or full edit history. Replacement, removal, moving
blocks, automation, and attribution policies need explicit gameplay rules later.

## Block entities and custom rendering

Persist block-entity records sparsely by local cell position with a type/schema
identity and versioned payload. There is no payload slot/object for every cell.
Replacing a block must retire incompatible records; loading validates positions,
types, and duplicate entries. Define bounds for text/inventory/payload sizes.

Custom geometry does not imply a block entity, live ECS entity, or ticking:

- Stairs use ordinary state-dependent geometry.
- Static decorative models may be batched into chunk geometry or instances.
- Signs need sparse text data.
- Chests need inventory data and potentially client-only animated presentation.
- Furnaces/machines need simulation only when their behavior requires it.

Use ECS entities for useful active behavior/presentation, not merely persistence.
Unloaded inventories do not require live entities. The persisted record and active
ECS representation need one explicit authoritative owner/handoff, not two mutable
copies. Exact typed payload and activation APIs are deferred. Private inventory
contents must not automatically become public chunk replication data.

## Resident chunk entities and payload lifetime

Use one lightweight ECS entity per resident voxel chunk for coordinate/world
identity, lifecycle, revisions, dirty/work flags, and payload access. It is not a
ghost. No voxel-per-entity or per-chunk MonoBehaviour update loop.

Voxel payload lives outside inline component storage. Unity archetype chunks are
ECS memory pages, not voxel-world chunks. Choose external dynamic buffers versus
a world-owned native pool during implementation based on job access, allocation,
and lifetime needs. A custom allocator is not a prerequisite. If a pool is used,
handles must reject stale reuse and disposal must respect outstanding jobs.

Keep a spatial address lookup. Avoid duplicating lifecycle truth in both an ECS
component and an independent manager. Lightweight entity count alone is not a
reason to reject ECS; measure payload memory, structural churn, scheduling and
meshing costs. Avoid scanning all resident chunks every tick without useful work.

Chunk writes are exclusive and batched; readers depend on the appropriate writer.
Jobs may run across different chunks in parallel. Palette mutations, resize, and
unload must not invalidate readers. Capture coherent snapshot revisions without
blocking on network delivery. Stale asynchronous mesh/encode results must be
detectable. Boundary edits invalidate affected neighbors. Detailed dependency,
revision, and buffer implementation contracts will be finalized with networking.

## 3D residency and budgets

Configure horizontal XZ and vertical Y radii separately. For a rectangular region
with radii H and V, count is `(2H + 1)^2 * (2V + 1)`. At H=8, V=4, there are 2,601
chunks. Ushort cell arrays alone occupy about 163 MiB; 32 non-overlapping player
regions would occupy about 5.1 GiB before palettes, fluids, meshes, and overhead.
These are illustrative worst-case dense counts, not selected default distances.

- Server residency is the union of interests, not copies per player.
- Distinguish loaded, rendered, and actively simulated sets.
- Prioritize nearby work and bound allocations, job work, and memory.
- Use unload hysteresis to avoid boundary thrashing.
- Client replicas remain distinct from server authoritative contents.
- Units/default radii, cuboid versus other distance metrics, and exact memory
  budgets remain tuning decisions. Changing chunk size changes physical coverage
  if radii are expressed in chunks; document the chosen units explicitly.

## Rendering granularity

32-cube is the selected storage/lifecycle baseline, not an immutable requirement
that every edit rebuild one giant mesh. Compared with a 16-cube, it has eight times
the cells: fewer scheduling units for the same volume, but potentially larger
rebuilds/uploads and coarser culling. Introduce smaller dirty/meshing regions only
if measurements justify them; derive their layout from the supported chunk size.
No meshing algorithm, GPU implementation, or terrain generation is selected here.

## Eventual implementation and verification

Implement in bounded slices with the networking design:

1. Size/coordinate contracts and immutable registries.
2. Uniform/paletted channels with dummy data and safe batch mutation.
3. Sparse records/flags, chunk identities, ownership and revision contracts.
4. Coherent snapshot and bounded change extraction for replication.
5. Streaming integration before meshing/generation work.

Tests should cover negative/boundary coordinates and flattening round trips;
16/32/64 configurations; uniform expansion; palette promotion, stale entries and
fallback; independent fluids; flag/record cleanup; illegal content; job readers
versus mutation/unload; and stale asynchronous results. Benchmark uniform, mixed,
high-diversity, and edit-heavy dummy chunks. Measure memory and throughput for
byte/ushort palettes versus direct uint and, later, packed alternatives. Broader
Unity compiler/tests are required when code is implemented, not for this spec.

## Research and rationale

- [NeoForge block states](https://docs.neoforged.net/docs/blocks/states/): finite
  property combinations and the distinction from arbitrary block-entity data.
- [Minestom palette source](https://github.com/Minestom/Minestom/blob/master/src/main/java/net/minestom/server/instance/palette/Palette.java):
  a Minecraft server implementation with uniform, indirect and direct modes.
  This is a design precedent, not a mandate to copy its thresholds or wire layout.
- [Vintage Story chunks](https://wiki.vintagestory.at/Modding:WorldGen_Concept/en)
  and [IChunkBlocks](https://apidocs.vintagestory.at/api/Vintagestory.API.Common.IChunkBlocks.html):
  32-cube chunks, palettes, and separate solid/fluid access.
- [Luanti map terminology](https://api.luanti.org/map-terminology-and-coordinates/):
  independently addressed 16-cube mapblocks.
- [Voxel Tools buffers](https://voxel-tools.readthedocs.io/en/latest/api/VoxelBuffer/)
  and [performance](https://voxel-tools.readthedocs.io/en/latest/performance/):
  uniform channels and mesh-size/culling tradeoffs.
- [Unity archetypes](https://docs.unity.cn/Packages/com.unity.entities@1.0/manual/concepts-archetypes.html):
  distinguishes ECS memory chunks from game-world chunks.

These sources support design precedents, not comparative DigBlocks benchmarks.
