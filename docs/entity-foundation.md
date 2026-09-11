# Entity foundation

## Status

Proposed, September 11, 2026. This is the approved direction for dynamic entity
simulation, ghost replication and entity presentation. Nothing in it is
implemented yet; the [implementation order](#implementation-order) is the
sequence of bounded steps that make it real.

## Goal and scope

The observable outcome this design is built to reach is small on purpose: a
server-authoritative mob, spawned on demand from the client debug menu, flying a
circle in the air, replicated to the client as an interpolated ghost, drawn by
Entities Graphics, relevant only to peers whose chunk interest covers it, and
unloaded with its chunk. It is a proof that every seam in the chain exists and
holds, not a feature.

In scope:

- the `NetworkStreamInGame` gate that currently keeps ghost replication switched
  off entirely, and the protocol bump that comes with it
- `DigBlocks.Simulation`: ECS components, deterministic systems, the entity type
  registry and chunk residency
- `DigBlocks.Simulation.Content`: JSON authoring for entity types and box models
- code-built ghost prefabs from the registry, with a seam for authored prefabs
- ghost relevancy derived from per-peer chunk interest
- entity chunk residency with an in-memory entity chunk store
- a presentation seam with an Entities Graphics backend behind it
- sector-based world coordinates, so distant play does not lose precision

Out of scope, designed here but built later: disk persistence, the player entity
and prediction, voxel collision, inventories, block entity activation, and any
mob behaviour beyond the circle.

## Where this starts from

Three facts about the current repository shape this design more than anything
else.

**Ghost replication is not merely unused, it is switched off.** Admission ends at
`AwaitingWorldData` and `NetworkStreamInGame` is never set, which
[the network foundation](network-session-foundation.md) deferred deliberately. No
snapshot traffic flows today, so the first step is a gate, not an optimisation.

**There is no player.** The interest anchor driving chunk streaming is a debug
free-fly `Camera` GameObject in `TerrainRenderService`. Nothing in the codebase
represents a person in the world, so entity work cannot lean on a player entity
and must not accidentally invent a half of one.

**Chunks already avoid ghosts.** Chunk state streams over a separate Unity
Transport companion connection, budgeted and batched. Ghost replication and bulk
voxel transfer are therefore already separate concerns with separate bandwidth,
and this design keeps them that way.

## Ownership and dependencies

New assemblies:

| Assembly | Path | Owns |
| --- | --- | --- |
| `DigBlocks.Simulation` | `Scripts/Simulation` | ECS components, deterministic systems, `EntityTypeRegistry`, the unmanaged `EntityTypeAttributes` table, chunk residency, the entity chunk store contract |
| `DigBlocks.Simulation.Content` | `Scripts/Simulation/Content` | JSON documents, archetype inheritance, the compiler |
| `DigBlocks.Client.Rendering/Entities` | folder, not an assembly | the presentation seam, the Entities Graphics backend and box-model meshing |

Extended:

- `DigBlocks.Networking.NetCode/Ghosts/` — ghost component variants, code-built
  prefabs, the in-game gate, the relevancy bridge and spawn RPCs
- `DigBlocks.Client.Rendering` — the entity presentation backend
- `DigBlocks.Client.Debugging` and `DigBlocks.Client.UI` — debug action entries

`DigBlocks.Simulation` references `Unity.Entities`, `Unity.Burst`,
`Unity.Collections`, `Unity.Mathematics`, `Unity.Physics`, `DigBlocks.Voxels` and
`DigBlocks.Core`. It must not reference `Unity.NetCode`, `DigBlocks.Networking`,
or anything presentational. That restriction is the same one that keeps
`DigBlocks.Voxels.Generation` usable from EditMode tests and offline tools, and
it is what makes simulation testable without standing up a connection.

The authoring and simulation layers mirror [block definitions](block-definitions.md)
exactly, for the same reasons: authoring owns JSON and Newtonsoft, and simulation
owns the compiled unmanaged tables that jobs read.

Appearance does not get its own assembly, which is a deliberate departure from the
block layering. `DigBlocks.Voxels.Appearance` exists because block appearance data
has consumers other than the renderer; entity appearance has exactly one, and
`DigBlocks.Client.Rendering` is already the client-only assembly a dedicated
server never loads, so the dedicated-server rule is satisfied without it. Model
*data* still lives in `DigBlocks.Simulation/Definitions` beside the registry, as
block appearance data lives inside `DigBlocks.Voxels`. Split it out if a second
consumer appears.

### The cost of keeping simulation NetCode-free

Components in `DigBlocks.Simulation` cannot carry `[GhostField]`, because that
attribute lives in `Unity.NetCode`. Replication is declared instead in
`DigBlocks.Networking.NetCode/Ghosts/` with `[GhostComponentVariation(typeof(T))]`,
which is the mechanism NetCode itself uses for third-party components — including
`Unity.Transforms.LocalTransform` and, as it happens, `PhysicsVelocity`, whose
variant NetCode already ships in `Unity.NetCode.Physics`.

The cost is one variant declaration per replicated component, in a different file
from the component. The benefit is that simulation stays loadable, runnable and
testable with no connection, no transport and no netcode codegen, which is worth
considerably more than the ceremony costs.

## Entity type content

Authored as JSON in `Assets/StreamingAssets/content/digblocks`, alongside block
content, with the same additive archetype inheritance: every field except `key`
is optional and resolves through the archetype chain, then engine defaults.

```json
{
  "key": "digblocks:debug_orbiter",
  "archetype": "digblocks:mob",
  "model": "digblocks:models/debug_cube",
  "width": 0.6,
  "height": 0.6,
  "ghost": { "mode": "interpolated", "optimization": "dynamic", "importance": 1 },
  "behaviors": [ "digblocks:circle_flight" ]
}
```

Models are authored separately and referenced by key, so several entity types can
share one. The schema is a box list in the manner of a Minecraft entity model,
because that is what this game's mobs will actually be and because it keeps the
appearance layer free of imported mesh assets:

```json
{
  "key": "digblocks:models/debug_cube",
  "texture": "digblocks:entity/debug",
  "boxes": [
    { "name": "body", "pivot": [0, 0, 0], "origin": [-4, -4, -4], "size": [8, 8, 8], "uv": [0, 0] }
  ]
}
```

Box coordinates are model units of 1/16 block, matching the block model
convention. The appearance layer compiles a box list into one `Mesh` plus a
material binding; named boxes become the addressable parts that programmatic
animation will later pivot.

The first milestone ships exactly one entity type and one model, but ships them
through the real pipeline rather than hardcoding them, so the pipeline is
exercised rather than stubbed.

### Registry and fingerprint

`EntityTypeRegistry` compiles to a state table indexed by a runtime `ushort` type
id, plus an unmanaged `EntityTypeAttributes` table that jobs read. Managed
definitions are load-time records and appear on no hot path, exactly as
`BlockRegistry` and `BlockAttributeTable` do.

`EntityTypeRegistry.Fingerprint`, version `DigBlocks.EntityRegistry.v1`, covers
the type key, the resolved archetype chain, category, ghost policy and every
field of `EntityTypeAttributes`. It deliberately excludes model and appearance
data: a client with a different mob texture is not a simulation divergence, while
a different hitbox is one.

NetCode independently hashes the ghost prefab collection and drops connections
that disagree. The fingerprint is therefore not the only guard — it is an
earlier, legible failure at binding time rather than an opaque disconnect after
going in-game.

## Component model

All unmanaged, in `DigBlocks.Simulation`:

| Component | Replicated | Notes |
| --- | --- | --- |
| `EntityTypeId { ushort Value }` | yes, static | index into the registry table |
| `WorldPosition { int3 Sector; float3 Local; }` | no, rebuilt from `ReplicatedPosition` | the authoritative position; see [coordinates](#coordinates-and-precision) |
| `ReplicatedPosition { double X, Y, Z; }` | yes | how position travels: absolute blocks, quantized to 1/1024; owned by the netcode assembly |
| `LocalTransform` | no | derived frame-local transform for physics and rendering, never the source of truth |
| `AabbExtents { float2 Value }` | no | width and height, derived from the registry on spawn |
| `ChunkResidency { ChunkAddress Address }` | no | server only |
| `CircleFlight { float3 Center; float Radius, AngularSpeed, Phase; }` | no | server-only behaviour state |

Presentation components are client-only and are never on the ghost prefab; see
below.

## Coordinates and precision

The world is meant to be travelled for millions of blocks. Every coordinate path
in DOTS is single precision, so absolute world positions in a `float3` are not an
option. Float spacing at magnitude `v` is about `v * 2^-23`:

| Distance from origin | Position granularity |
| --- | --- |
| 8,400 blocks | 1 mm |
| 100,000 | 1.2 cm |
| 1,000,000 | 12 cm, visibly broken |
| 16,777,216 | one full block |

There are four separate ceilings, and only one of them is physics:

- `LocalTransform.Position` is `float3`, so the transform system degrades.
- `LocalToWorld` is `float4x4`, so rendering degrades with it.
- NetCode's default transform variant is `[GhostField(Quantization=1000)]`, and
  quantized floats are stored in an `int`. That caps replicated position at
  `int.MaxValue / 1000`, about 2,147,483 blocks, and it overflows rather than
  degrading. Large quantized values also compress badly, so bandwidth per entity
  worsens well before the overflow.
- `TerrainRenderer` computes chunk origins as `(float3)position * 32`, which has
  the same ceiling. That is outside this milestone but recorded below.

### Sector and offset

The authoritative position is an exact integer sector plus a small float offset:

```csharp
public struct WorldPosition : IComponentData
{
    public int3 Sector;   //coarse integer grid, SectorEdge blocks across
    public float3 Local;  //offset within the sector
}
```

`SectorEdge` is 4096 blocks. Worst-case float granularity inside a sector is then
about 0.5 mm, constant everywhere in the world, and the range is bounded only by
the `int3`. Precision does not decay with distance the way any float or double
absolute coordinate does, because the large part of the number is exact.

`LocalTransform` becomes a derived value: the entity's offset from its simulation
frame origin, recomputed from `WorldPosition`. Physics and rendering read it and
are correct because it is always small. Nothing treats it as authoritative, and
nothing outside the frame machinery writes it.

### Replication

`WorldPosition` is not itself replicated. Position travels as `ReplicatedPosition`:
absolute block coordinates as three `double` ghost fields, quantized to 1/1024 of
a block. The client rebuilds `WorldPosition` from them, and `LocalTransform` is
derived from that.

A quantized `double` field is stored in a `long` and delta-compressed with
`WritePackedLongDelta`, so this is sub-millimetre precision across a range of
about nine quadrillion blocks, and the precision is identical everywhere. It does
not decay with distance from the origin, from a spawn point, or from anything
else. Bandwidth is not the obvious loss it looks like, because the value
delta-compresses against the previous snapshot: what goes on the wire is the
movement since the last tick, which for anything moving at a plausible speed is a
handful of bits.

The sector split must not reach the wire. NetCode interpolates each ghost field
independently, so replicating the sector and the offset as separate fields would
lerp the offset across its wrap while the sector snapped, and every sector
crossing would throw the entity a full sector's width and back inside one tick.
An absolute coordinate has no wrap to tear at. Simulation still keeps the sector
form, because physics and rendering need a frame-local float and the sector grid
is what supplies one.

### Why the quantization is a power of two

1024, not 1000, and it matters more than it looks. The generated deserializer
dequantizes with the scale written as a float literal widened to a double. At a
quantization of 1000 the reciprocal is 0.001000000047..., a relative error of
about 4.8e-8: under a micron near the origin, but **two whole blocks fifty million
blocks out**, which quietly undoes the entire point of replicating doubles. 1/1024
is exact in both float and double, so the scale contributes no error at any
distance. Any future quantization on a large-magnitude field should be a power of
two for the same reason.

### Verification

`SectorBoundaryReplicationTests` covers both halves:

- a mob flown through a sector boundary is asserted to be seen on each side, and
  no two consecutive client frames may move it more than an eighth of a sector,
  where a tear would be a whole sector;
- the same circle is flown at the origin and fifty million blocks out, and the
  error out there may not exceed the error at the origin. Measuring the radius in
  absolute terms would mostly measure interpolation chord error, so comparing two
  identical circles is what isolates precision. Both currently come out at 0.038
  blocks, which is the chord error alone.

### Simulation frames

A simulation frame is a physics world with an integer origin. Every body in it
carries a small offset from that origin, so the numbers the solver sees stay well
conditioned no matter where in the world the frame sits.

`PhysicsWorldIndex` is the partitioning primitive for this. Unity Physics does not
manage frame origins, so this design does: a frame owns an `int3` origin on the
sector grid, entities are assigned to a frame by sector, and an entity that drifts
beyond the frame's extent migrates. Rebasing uses hysteresis so an entity pacing a
frame boundary does not thrash, in the same way chunk unloading does.

Dimensions fall out of the same mechanism, since `ChunkAddress.World` already
distinguishes them and bodies in different physics worlds cannot interact.

### What lands now

Only the contract. `WorldPosition` is authoritative and `LocalTransform` is
derived from the outset, and the sector-aware ghost variant ships with the first
replicated entity. There is exactly one frame, pinned at the origin.

Frame creation, entity migration between frames, and per-frame physics worlds are
built when something can actually travel far enough to need them. Getting the
contract right now makes that additive; getting it wrong makes it a rewrite of
every system that touches a position.

## Presentation is attached, not baked

`ConvertToGhostPrefab` requires both worlds to build the prefab identically. The
package supports per-component client/server stripping through overrides, but
this design keeps every render component off the prefab entirely:

- the prefab is simulation-only and identical in both worlds, so the riskiest
  property of code-built ghosts is trivially satisfied rather than carefully
  maintained
- no managed shared component (`RenderMeshArray`) sits in the ghost path
- the renderer can be replaced without touching simulation, loading, relevancy or
  netcode

A client system watches for ghost entities carrying `EntityTypeId` without an
`EntityViewAttached` tag and calls into:

```csharp
public interface IEntityPresentationBackend : IDisposable
{
    void Attach(EntityManager manager, Entity entity, ushort typeId);
    void Detach(EntityManager manager, Entity entity);
}
```

`EntitiesGraphicsPresentationBackend` is the only implementation. Replacing it
with an instanced renderer in the style of the existing terrain path, or with a
pooled GameObject bridge for animation-heavy entities, is one new file.

The seam costs one structural change per spawned entity on the client. Baking
would have cost the same, and it happens on spawn rather than per frame, so at
mob counts it is free. If entity spawn rates ever reach thousands per frame —
dense item drops, particles — revisit with a batched attach.

This seam is also insurance. Entities Graphics running inside a NetCode client
world, which is created by `ClientServerBootstrap.CreateClientWorld` rather than
as the default world, is the one assumption in this design that has not been
verified against a running editor. It should hold, because NetCode client worlds
carry `WorldSystemFilterFlags.Presentation`, but it is checked early, and the
fallback backend is already designed if it does not.

## The in-game gate

Ghost replication needs `NetworkStreamInGame` on both ends of the connection.
The transition:

1. The client's `ResidentChunkStore.DataReady` becomes true — its replica count
   matches its interest — and `ClientEnterGameSystem` adds `NetworkStreamInGame`
   to its own connection and sends `EnterGameRpc`.
2. The server validates that the connection is admitted and companion-bound, then
   adds `NetworkStreamInGame` to that connection.
3. `NetworkSessionState` gains `InGame`, after `AwaitingWorldData`.

This changes application semantics, so the protocol version bumps. The server
must reject `EnterGameRpc` from a connection that has not completed admission,
following the existing precedent in `ServerHelloReceiveSystem`, which discards
client-issued control RPCs rather than trusting them.

Going in-game starts snapshot and command traffic on the NetCode connection while
chunk traffic continues on the companion socket. At this milestone interest is
anchored on the debug camera and the mob is spawned beside it, so the added
traffic is negligible — but the interaction between the two bandwidth budgets
must be measured before player movement lands, not assumed.

## Simulation distance

How far the world is alive is a separate setting from how far it is drawn, with
its own horizontal and vertical radii, for the same reason Minecraft separates
them: streaming a chunk costs bandwidth once, whereas keeping the entities in it
alive costs ticks, behaviour and snapshots every frame, for every peer. A player
can reasonably want to see much further than the server should be simulating.

`EntitySimulationDistance` carries the two radii and is authored beside render
distance in `ChunkStreamingSettings`, because those are the two numbers a player
would expect to find together. It stays entity-scoped and never reaches
`ChunkStreamingOptions` or the chunk protocol.

It is always clamped down to the peer's own streaming distance, never up.
Simulating an entity in a chunk a client does not have would replicate something
it cannot place. `ChunkInterest.Narrowed` performs that clamp, and the standalone
`ChunkInterest.Contains` overload lets relevancy ask about one address at the
narrowed radius without building a second interest every tick.

Both entity residency and ghost relevancy key off the simulated volume rather
than the streamed one.

## Ghost relevancy from chunk interest

Without relevancy, every mob in the world replicates to every client. With it,
mob count scales with world size while per-client snapshot cost scales with view
distance. This is the single most important performance property in the design,
which is why it is built now rather than deferred.

NetCode's `GhostRelevancy` singleton is set to `GhostRelevancyMode.SetIsRelevant`
and its `GhostRelevancySet` keyed by `(NetworkId, ghostId)`. The bridge from
chunk interest to connection already exists in pieces:

```text
connection entity -> SessionContext.ServerConnections -> peer id
                  -> ChunkStreamingServer peer        -> ChunkInterest
```

A server system rebuilds the set each tick, testing each entity's residency
against every peer's simulated volume. `ChunkStreamingServer` is `internal` and
its `Peer` is an implementation detail, so it grows a narrow accessor that copies
out peer id and interest rather than handing over the peer itself.

Entity residency splits its work by cost. Rebuilding the set of simulated chunks
enumerates every chunk of every peer, so it runs only when some peer's interest
epoch actually advances, which is when a player crosses a chunk boundary; taking
entities back out of the store happens at the same moment, since that is when the
resident set can grow. Unloading, by contrast, runs every tick, because an entity
can leave the simulated set by moving or by being spawned outside it, neither of
which touches any peer's interest.

The naive rebuild is O(entities x peers), but the test is two multiplies and a
comparison, it is Burst-parallel over entities, and it runs only on change. At a
thousand entities and thirty-two peers that is thirty-two thousand trivial tests.
Bucketing entities by `ChunkResidency` into a multi-hash-map and walking each
peer's cached interest offsets instead is the fallback if measurement ever
demands it; it is not worth the complexity first.

## Entities and chunks

Server chunk residency is already the union of peer interests rather than a copy
per player, as [the chunk data architecture](chunk-data-architecture.md) requires.
Entities follow the chunks:

- `ChunkResidencySystem`, Burst and server-side, writes `ChunkResidency` from
  `WorldPosition`, skipping the write when the chunk has not changed so a
  stationary mob costs nothing downstream.
- Spawning sets `ChunkResidency` itself rather than waiting for that system. An
  entity with a default residency reads as being in world zero, which no interest
  covers, and would be swept into the chunk store before it ever ticked.
- When a chunk leaves the resident set, the entities in it are handed to
  `IEntityChunkStore.Store(address, entities)` and destroyed.
- When a chunk enters the resident set, `IEntityChunkStore.Load(address)`
  respawns them.

`InMemoryEntityChunkStore` implements this now. Chunk saving to disk is not
implemented anywhere in the project yet, so a disk-backed implementation would be
premature — but the in-memory store is not a stub. It makes the unload and reload
round trip real and testable today, and the disk version later swaps in behind
the same interface without disturbing anything that calls it.

This honours the approved rule that a persisted record and a live ECS
representation have one authoritative owner with an explicit handoff, never two
mutable copies.

## Block entities

Designed here, implemented later, and consistent with the direction already
approved in the chunk data architecture rather than replacing it.

A block entity is a sparse per-cell record carrying a type key and a versioned
payload, persisted with its chunk; there is no payload slot per cell. A live ECS
entity exists only where there is active behaviour or presentation — a furnace
that is smelting, a chest that is open. Loading a chest's inventory requires no
entity at all. `BlockFlags.BlockEntity` and `BlockRegistry.BlockEntityKey`
already exist and already sit inside the registry fingerprint, so the content
contract is cut; what is missing is the store and the activation handoff, which
mirrors the entity chunk residency handoff above.

Private inventory contents are not part of chunk replication.

## Items

Two deliberately distinct things:

- `ItemStack` is unmanaged data — item type id, count, durability, with a side
  table for richer per-stack state. It lives in inventories. It is never an
  entity, and an inventory sitting in an unloaded chunk is a record, not a world
  object.
- A dropped item is an ECS entity and a ghost carrying an `ItemStack`,
  static-optimised and low importance, relevant by chunk interest like any other
  entity.

## Replication policy by category

| Category | Ghost mode | Optimisation | Importance | Relevancy |
| --- | --- | --- | --- | --- |
| Local player | Owner-predicted | Dynamic | highest | always |
| Remote players | Interpolated | Dynamic | high | chunk interest |
| Mobs | Interpolated | Dynamic | medium | chunk interest |
| Dropped items | Interpolated | Static | low | chunk interest |
| Projectiles | Interpolated | Dynamic | high | chunk interest |
| Block entities | not ghosts | — | — | on demand |

Prediction is a CPU budget, not a default ghost mode. Only the locally controlled
player is predicted, and only once player movement exists. Projectiles may earn
prediction later if their latency justifies the resimulation cost; they do not
get it by default.

## Physics

Unity Physics 6.6 and `Unity.NetCode.Physics` are available. NetCode supplies
`PhysicsWorldHistory` for lag-compensated hit validation,
`PredictedPhysicsSystemGroup`, and a ready-made `PhysicsVelocity` ghost variant.

Physics is not used at this milestone — a mob flying a fixed circle is kinematic
transform animation and nothing more — but the design must not preclude it.
`DigBlocks.Simulation` therefore references `Unity.Physics`, and the entity type
schema reserves space for a physics configuration so adding one is not a
retrofit.

### Why there are several physics worlds

Multiple physics worlds are motivated by precision, not by broadphase cost. Unity
Physics rebuilds a BVH each step, so spatial separation within one world is
already handled and splitting purely to reduce broadphase work would be a
pessimisation — several rebuilds instead of one, hand-off logic at every boundary,
and two players who meet unable to collide.

Precision changes that conclusion. A solver operating on absolute coordinates
millions of blocks from the origin is working with a grid coarser than the bodies
it is simulating. Partitioning into frames with integer origins keeps every
solver input small, which is the [coordinate design](#simulation-frames) above.
`PhysicsWorldIndex` is the primitive that does the partitioning; the frame origin
is ours to maintain. Dimensions are the same mechanism, and NetCode's separation
of predicted from interpolated physics is a third use of it.

### Evaluated and deferred: Box3D

Box3D, Erin Catto's 3D engine forked from the Box2D architecture, was considered
as an alternative because its large-worlds build uses double precision for
positions. It was rejected for now, for recorded reasons rather than for quality.

- Its large-worlds mode is a compile-time ABI switch, not a runtime option, and
  only world positions become double; velocities, forces and the solver stay
  float. The cost is a few percent.
- The broadphase remains float-bound and degrades with distance: roughly a metre
  of uncertainty at 1e7 and about sixteen metres at 1e8, with a recommended
  operating range of plus or minus 1e7 to 1e8. Double precision therefore buys a
  Minecraft-sized world, not an unbounded one, and its precision still decays.
  The sector design is unbounded and its precision is constant.
- Double precision physics addresses one of the four ceilings listed under
  coordinates. Transforms, rendering and replication would still need sector
  coordinates, and once those exist the solver only ever sees small frame-local
  values, so the double precision is redundant.
- Adopting it means giving up `Unity.NetCode.Physics` entirely: lag compensation
  through `PhysicsWorldHistory`, `PredictedPhysicsSystemGroup`, and the
  `PhysicsVelocity` ghost variant. Client prediction needs physics state
  snapshotted, restored and resimulated per tick, which Unity Physics already
  integrates with and a native engine would not.
- Maturity: the engine is v0.1 alpha by its author's own description, and the
  Unity binding is 0.8.x with an experimental DOTS integration, no shipped
  macOS or iOS binaries, and explicit warnings about breaking changes.

It stays a candidate. Its triangle mesh, height-field and baked compound collision
are directly relevant to voxel terrain, so it should be measured against Unity
Physics at the milestone that settles collision — by which time it will not be
v0.1. The sector design keeps that switch cheap: with `WorldPosition`
authoritative and all physics state derived and frame-local, the physics backend
is replaceable for the same reason the renderer is.

### Open decision: voxel collision representation

How voxels present themselves to Unity Physics is unresolved and does not need to
be resolved to reach the flying mob, but it will shape the meshing and chunk
subsystems when it is.

- A per-chunk `MeshCollider` built from the greedy quads the mesher already
  produces is compact and reuses existing work, but rebuilds on every edit and
  gives no cheap point query.
- A per-chunk compound of box colliders is simple and queryable but large.
- A custom voxel AABB sweep outside Unity Physics, Minecraft-style, is cheap,
  deterministic and Burst-friendly, and leaves Unity Physics to entity-versus-
  entity work. Many voxel games split it exactly this way.

Deciding this needs measurement against real chunk data, so it is deferred to the
milestone that needs it.

### Known follow-up: terrain render origins

`TerrainRenderer` submits chunk origins as `(float3)position * 32`, absolute world
coordinates in single precision, so terrain has the same ceiling entity positions
do and would visibly misalign long before the world border. It needs the same
frame treatment — chunk origins expressed relative to the render frame — but it is
a separate piece of work in `DigBlocks.Client.Rendering` and is not part of this
milestone. It is recorded here so the two do not drift apart.

## Debug spawn

The debug menu currently offers only toggles that cycle through states on a
keypress. It gains a second entry kind, `DebugAction`: a label, a key and a
callback, rendered alongside toggles by `DebugMenuView`.

The spawn action sends `SpawnDebugEntityRpc { FixedString64Bytes TypeKey; float3 Position }`.
The server honours it only when its own launch options permit debug commands. A
client-issued spawn is never trusted by default — a client that can spawn
arbitrary entities on a real server is a straightforward exploit, and the gate
belongs on the server, not on whether the client happens to show the menu.

## Implementation order

Each step is independently verifiable, and each leaves the project working.

1. **In-game gate.** `EnterGameRpc`, `NetworkSessionState.InGame`, protocol bump.
   Verify: a single-player session reaches `InGame` on both connections and stays
   connected.
2. **`DigBlocks.Simulation` and the registry.** Components including
   `WorldPosition`, the single origin-pinned simulation frame and the system that
   derives `LocalTransform` from it, `EntityTypeRegistry`, `EntityTypeAttributes`,
   fingerprint. Verify: registry and fingerprint tests mirroring the block
   registry tests, plus sector normalisation and round trips at extreme
   coordinates.
3. **`DigBlocks.Simulation.Content`.** Schema, archetype inheritance, compiler,
   one entity type and one box model in StreamingAssets. Verify: compiler tests
   covering inheritance and rejection of malformed documents.
4. **Code-built ghost prefabs and position replication.** A system in both worlds
   builds prefabs from the registry through `ConvertToGhostPrefab`, after
   `DefaultVariantSystemGroup`, plus the authored-prefab seam and
   `ReplicatedPosition`. Verify: client and server prefab hashes agree, the
   connection survives going in-game, and a ghost crossing a sector boundary moves
   continuously on the client. This is the highest-risk step in the sequence.
5. **Spawn and circle behaviour.** `SpawnDebugEntityRpc`, server spawn, a Burst
   `CircleFlightSystem` operating on `WorldPosition`. Verify: the ghost appears in
   the client world and its replicated position traces a circle, including when
   the circle straddles a sector boundary.
6. **Chunk residency.** `ChunkResidencySystem`, `IEntityChunkStore`,
   `InMemoryEntityChunkStore`. Verify: the unload and reload round trip.
7. **Ghost relevancy.** The interest accessor and the relevancy system. Verify: a
   mob outside a peer's interest does not reach that client, and appears when
   interest moves over it.
8. **Presentation.** `IEntityPresentationBackend`, the Entities Graphics backend,
   box model compilation. Verified with a recording backend standing in for
   Entities Graphics, which is the same substitution the seam exists to allow, plus
   geometry assertions on the compiled box model. Composed only for graphical
   clients, so headless runs are unaffected.
9. **Debug menu action.** `DebugAction` entries alongside toggles, sharing the
   toggles' keybind lifetime so a debug command cannot fire during ordinary play.
   The work itself is a callback supplied by the composition root, because client
   presentation must hold no session or transport knowledge and spawning is a
   networking concern.

## Verification

- EditMode: registry, fingerprint, content compiler, box model compilation, chunk
  residency transitions, entity chunk store round trip.
- PlayMode: the in-game gate, ghost prefab hash agreement, end-to-end spawn and
  replication, relevancy on both sides of an interest boundary.
- Manual: single player, spawn from the debug menu, watch the mob circle, and
  confirm terrain rendering and chunk streaming are undisturbed.
- Regression: the full PlayMode suite, with a domain reload before the run and
  the reported test count checked against the suite size.

## Research and rationale

- Minecraft stores entities per chunk and, since 1.17, in a separate entity
  region file keyed by chunk. Entities load and unload with their chunk. The
  `IEntityChunkStore` handoff follows that separation without adopting its file
  format.
- Minecraft's block entities are chunk-owned records rather than world objects,
  with behaviour attached only where something actually ticks. The approved chunk
  data architecture already reached the same conclusion independently.
- Unity's documented guidance for `ConvertToGhostPrefab` is that prefabs must be
  created identically on client and server and contain all components, with
  client-only or server-only components handled through overrides. Keeping
  presentation off the prefab avoids relying on that mechanism for the common
  case.
- NetCode's own `PhysicsVelocityVariant` in `Unity.NetCode.Physics` is the
  precedent for declaring replication for a component whose assembly knows
  nothing about netcode.

### Dead ends worth not repeating

Two findings from building the position encoding, recorded because each cost real
time and neither is discoverable from the documentation.

**A quantized ghost field is only as exact as its scale.** The generated
deserializer multiplies by the dequantization scale written as a float literal
widened to a double. Any quantization whose reciprocal is not exactly
representable in float therefore injects a relative error of up to about 6e-8,
which is invisible near the origin and metres away at the edge of a large world.
Use powers of two for quantization on any field whose magnitude can get large.

**A custom ghost field template cannot be registered from a project assembly while
the package is immutable.** The generator finds registrations one of two ways: it
parses the `UserDefinedTemplates.RegisterTemplates` body when the compilation
declares that type in source, which is only true for `Unity.NetCode` itself, and
otherwise it reflects over the compiled `Unity.NetCode.dll`. The documented
approach is an assembly definition reference that compiles the registration into
that assembly, but Unity does not rebuild package assemblies while the package
lives in the immutable package cache: the reference is accepted into the generated
IDE project and the built assembly never changes. Declaring the type in a project
assembly instead does not help either. Registering a template therefore requires
vendoring the package. A second, unrelated trap if anyone tries again: the
registration body is parsed syntactically for object creations among the argument's
*descendant* nodes, so `templates.Add(new TypeRegistryEntry{...})` is invisible to
it and `templates.AddRange(new[]{ ... })` is required.
- Minecraft keeps entity positions and bounding boxes in `double` and renders
  chunk geometry camera-relative, so absolute coordinates never reach a float in
  the vertex path, with a world border near 30,000,000 blocks stopping play
  before its integer packing misbehaves. The old Far Lands near 12,550,824 were a
  terrain generation precision artifact rather than a physics one. It sidesteps
  the problem with doubles and a border rather than rebasing the world; DOTS
  being single precision throughout, this design cannot copy that and uses exact
  integer sectors instead.
- Box3D [announcement](https://box2d.org/posts/2026/06/announcing-box3d/),
  [repository](https://github.com/erincatto/box3d) and
  [large worlds documentation](https://box2d.org/documentation3d/large-worlds.html)
  supply the double precision behaviour and range limits recorded above. C#
  bindings surveyed were [Suvitruf/box3d-unity](https://github.com/Suvitruf/box3d-unity),
  which carries the experimental DOTS integration, and
  [Miguel249/Box3D.NET](https://github.com/Miguel249/Box3D.NET), which is
  single precision only.
