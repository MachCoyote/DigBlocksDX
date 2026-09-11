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
| `DigBlocks.Simulation.Appearance` | `Scripts/Simulation/Appearance` | compiled box models: mesh and material bindings per model key |

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

The three-layer split mirrors [block definitions](block-definitions.md) exactly,
for the same reasons: authoring owns JSON and Newtonsoft, simulation owns the
compiled unmanaged tables that jobs read, and appearance owns everything a
dedicated server build must be able to drop.

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
| `LocalTransform` | yes | NetCode's default transform variant |
| `AabbExtents { float2 Value }` | no | width and height, derived from the registry on spawn |
| `ChunkResidency { ChunkAddress Address }` | no | server only |
| `CircleFlight { float3 Center; float Radius, AngularSpeed, Phase; }` | no | server-only behaviour state |

Presentation components are client-only and are never on the ghost prefab; see
below.

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

A server system rebuilds the set when an entity's `ChunkResidency` changes or a
peer's interest epoch advances, testing `interest.Contains(residency.Address)`.
`ChunkStreamingServer` is `internal` and its `Peer` is an implementation detail,
so it grows a narrow read-only accessor exposing peer id, anchor and radii rather
than handing out the peer.

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
  `LocalTransform.Position`, change-filtered so a stationary mob costs nothing.
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

On multiple physics worlds: `PhysicsWorldIndex` is a shared component that
partitions entities into separate worlds, each stepping its own broadphase, and
bodies in different worlds cannot collide. That maps correctly onto genuinely
disjoint spaces — dimensions, which `ChunkAddress.World` already distinguishes —
and onto NetCode's separation of predicted from interpolated physics. It maps
poorly onto players spread across one dimension: Unity Physics rebuilds a BVH
broadphase each step, so spatial separation is already handled, and splitting per
player would mean several broadphase rebuilds instead of one, hand-off logic
whenever an entity crosses a region boundary, and two players who meet being
unable to collide. The plan is therefore one physics world per dimension, plus
NetCode's predicted world, and not a context per player.

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
2. **`DigBlocks.Simulation` and the registry.** Components, `EntityTypeRegistry`,
   `EntityTypeAttributes`, fingerprint. Verify: registry and fingerprint tests
   mirroring the block registry tests.
3. **`DigBlocks.Simulation.Content`.** Schema, archetype inheritance, compiler,
   one entity type and one box model in StreamingAssets. Verify: compiler tests
   covering inheritance and rejection of malformed documents.
4. **Code-built ghost prefabs.** A system in both worlds builds prefabs from the
   registry through `ConvertToGhostPrefab`, after `DefaultVariantSystemGroup`,
   plus the authored-prefab seam. Verify: client and server prefab hashes agree
   and the connection survives going in-game. This is the highest-risk step in
   the sequence.
5. **Spawn and circle behaviour.** `SpawnDebugEntityRpc`, server spawn, a Burst
   `CircleFlightSystem`. Verify: the ghost appears in the client world and its
   replicated position traces a circle.
6. **Chunk residency.** `ChunkResidencySystem`, `IEntityChunkStore`,
   `InMemoryEntityChunkStore`. Verify: the unload and reload round trip.
7. **Ghost relevancy.** The interest accessor and the relevancy system. Verify: a
   mob outside a peer's interest does not reach that client, and appears when
   interest moves over it.
8. **Presentation.** `IEntityPresentationBackend`, the Entities Graphics backend,
   box model compilation. Verify: the mob is visible, and graphics-free batch
   runs are unaffected.
9. **Debug menu action.** `DebugAction` entries and the spawn keybind.

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
