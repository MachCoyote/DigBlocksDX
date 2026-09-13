# Entity foundation implementation summary

What was built against [the approved entity foundation design](entity-foundation.md),
what it costs, and what it deliberately does not do yet. The authoring reference
is [entity definitions](entity-definitions.md).

The milestone target was a mob that flies a circle in the air, spawned from the
debug menu, as a working sign to build on. That works, and what stands behind it
is a slice through every layer the design named: content, registry, ghost
prefabs, replication, relevancy, residency and presentation.

## What exists

**A simulation assembly that does not know about netcode.** `DigBlocks.Simulation`
owns the ECS components and deterministic systems both worlds share. It depends
on `Unity.Entities` and must not depend on `Unity.NetCode`, so replication for its
components is declared as ghost variants in `DigBlocks.Networking.NetCode` rather
than as `[GhostField]` attributes on the components themselves. The precedent is
NetCode's own `PhysicsVelocityVariant`.

**Position that does not decay with distance.** `WorldPosition` is an exact
integer sector plus a small float offset inside it, because an absolute `float3`
cannot express this world: float spacing is about `v * 2^-23`, so a million blocks
out resolves to twelve centimetres and sixteen million resolves to a whole block.
`LocalTransform` is derived from it every frame and is never the source of truth.
On the wire it becomes three quantized `double` ghost fields in absolute block
coordinates — not sector and offset as separate fields, which would tear at every
sector boundary. Precision is identical at the origin and fifty million blocks
out, and a test asserts exactly that by comparing the same circle in both places.

**Entity types as content, not code.** JSON with the same additive archetype
inheritance block content uses, compiled to a `ushort`-indexed registry plus an
unmanaged attribute table for jobs. `EntityTypeRegistry.Fingerprint` covers
identity, behaviours and simulation attributes, and deliberately excludes the
model: a different mob texture is not a divergence, a different hitbox is.

**Ghost prefabs built in code from that registry.** `GhostPrefabCreation.Convert
ToGhostPrefab` walking the registry in runtime id order, so both worlds produce a
hash-identical collection with no subscene. Subscene-authored prefabs remain
available later; nothing here forecloses them.

**Replication scoped by what a peer can actually see.** Ghost relevancy derives
from the same per-peer chunk interest the chunk companion already owns, narrowed
by a separate entity simulation distance. Mob count then scales with the world
while per-client cost scales with view distance, which is the only shape that
holds up. Simulation distance is deliberately not render distance: streaming a
chunk costs bandwidth once, keeping its entities alive costs ticks and snapshots
every frame for every peer.

**Entities that load and unload with their chunks.** Server residency is the union
of peer interests, and entity residency derives from that same union rather than
keeping a second opinion. A chunk leaving hands its entities to
`IEntityChunkStore` and destroys them; a chunk arriving spawns them back.
`EntityFlags.Persists` decides which entities get that round trip at all, and
`IEntityStateCodec` decides what behaviour state survives it — as behaviour key
and bytes, which is what a chunk file will need once saving exists.

**Presentation attached after spawn, not baked into the prefab.** The prefab stays
simulation-only and identical in both worlds, so nothing about rendering can
perturb the ghost collection hash, and replacing the renderer is one new
implementation of `IEntityPresentationBackend`. Entities Graphics sits behind that
seam today.

## Two ordering rules that are not optional

Both were learned the hard way and are now stated in attributes rather than
inherited from Unity's system sort.

Everything that moves an entity of its own accord lives in
`EntityBehaviorSystemGroup`. Anything that *derives* something from a position
updates after it: `ChunkResidencySystem`, and `ServerPositionPublishSystem`.

The reason this matters more than it looks: a stale derivation is never a visible
error. It is an entity filed under a chunk it is not in — so the chunk that would
bring it back is not the chunk holding it — or a position on the wire that is a
tick old. Both were found only by playing the game.

## What it cost

- One structural change per spawned entity to attach presentation. Baking would
  have cost the same, and it happens on spawn rather than per frame.
- One managed pass per tick for residency and relevancy. Rebuilding the resident
  set is gated on a peer's interest actually changing; unloading is not, because
  an entity can leave the simulated set by moving.
- A quantized double per axis on the wire, delta-compressed against the previous
  snapshot, so what actually travels is a tick of movement in millimetres.

## Verification

547 PlayMode tests at the end of the slice, 543 passing and 4 skipped as
profiling harnesses. The entity-specific ones
cover the content pipeline, registry and fingerprint, coordinate conversions,
ghost prefab construction, sector-boundary continuity, precision at fifty million
blocks, relevancy across two peers, residency round trips under a walking and
wandering anchor, the debug spawn RPC path, variant world filters, Entities
Graphics inside a NetCode client world, behaviour state codecs, and a second
session in the same process.

Four bugs in this slice were found by playing the build rather than by the suite,
and every one of them was in a path the tests reached by a shorter route than the
game does. They are recorded individually in the design document under "Bugs the
tests did not have", because the shape of the gap is more useful than the fixes.
The pattern, in one line: test the real lifecycle — the real RPC path, real world
creation, real destroy-and-recreate, a real player walking — because the failure
mode of a shortcut is that it passes.

## Not done, by design

- **The player entity.** Spawning, owner prediction, input commands and camera
  ownership. The slice ends at server-authoritative interpolated mobs.
- **Entity collision.** Unity Physics is installed and a physics-world-per-frame
  direction is recorded in the design document, but no entity collides with
  anything yet. Box3D was evaluated for its double precision and deliberately
  deferred; the reasoning is recorded so it is not re-evaluated from scratch.
- **Block entities.** Designed in the foundation document, not implemented.
- **Items.** Designed, not implemented.
- **Disk persistence.** `IEntityChunkStore` has one in-memory implementation. It
  is not a stub — it makes the unload and reload round trip real and testable
  today — and a disk-backed store swaps in behind the same interface. Chunk saving
  does not exist anywhere in the project yet, so a disk implementation here would
  have nothing to write into.
- **Natural spawning and despawning.** Entities exist because something spawned
  them; today only the debug key does.

## One thing worth tuning

`ChunkStreamingSettings` ships with a render distance of 12 chunks horizontal and
4 vertical, but an entity simulation distance of 2 and 1, which are the code
defaults rather than an authored choice — the asset predates those fields. Mobs
therefore stop being simulated at 64 blocks while terrain is visible to 384. That
is a legitimate setting, and separating the two is the point, but the current
numbers are an accident rather than a decision.
