# Entity definitions

How entity types are authored, resolved and compiled. The companion to
[block definitions](block-definitions.md), which this deliberately mirrors: same
content root, same additive archetype inheritance, same fingerprint discipline.
The design behind it is in [the entity foundation](entity-foundation.md); this is
the authoring reference.

## Three layers

Entity content is three kinds of document, each in its own folder under
`Assets/StreamingAssets/content/digblocks`:

| Folder | Document | Purpose |
| --- | --- | --- |
| `entity_models/` | model | Geometry and texture. Referenced by key, so several types can share one. |
| `entity_archetypes/` | archetype | Shared defaults a type inherits. Never instantiated itself. |
| `entities/` | type | An entity that can exist in the world. Gets a runtime id. |

Only documents in `entities/` become entity types. An archetype is a set of
defaults and nothing else; a model is appearance and nothing else. The split is
what lets a plain mob be three lines and keeps appearance out of the simulation
fingerprint.

Files may hold one document or an array of them, and the filename means nothing —
`key` is the identity.

## Additive authoring

Every field except `key` is optional. A field resolves through the archetype
chain and then falls back to the engine default, so a type states only what makes
it different:

```json
{
  "key": "digblocks:pig",
  "archetype": "digblocks:mob",
  "model": "digblocks:models/pig",
  "width": 0.9,
  "height": 0.9
}
```

Archetypes may name an archetype of their own. Resolution walks the chain from
the type outwards, first value wins, then `EntityTypeAttributes.Default` supplies
anything nobody set: an ordinary ground mob, 0.6 by 1.8 blocks, which persists,
falls, collides and can be hurt, replicated as an interpolated dynamic ghost at
importance 1.

`behaviors` is the one field that does not simply overwrite: the lists union
across the chain, deduplicated, so a type adds to what its archetype already runs
rather than replacing it. There is no way to author away an inherited behaviour;
if that is ever needed, it wants an archetype that does not have it.

## Authoring reference

### Types and archetypes

Both accept exactly the same fields; unknown fields are rejected rather than
ignored, so a typo fails at load with the file that caused it.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `key` | string | required | Namespaced identity, e.g. `digblocks:pig`. |
| `archetype` | string | none | Archetype to inherit from. |
| `model` | string | none | Model key. A type with no model simulates and replicates but draws nothing, which is what a trigger or marker wants. |
| `behaviors` | string array | none | Behaviour keys this type runs. Unions with the archetype chain, deduplicated. |
| `persists` | bool | `true` | Saved with its chunk and restored when that chunk loads again. A type without it is discarded when its chunk leaves the simulated set. |
| `gravity` | bool | `true` | Falls. A fixed-path flier or hovering marker does not. |
| `collides` | bool | `true` | Collides with terrain and other collidable entities. |
| `living` | bool | `true` | Can be damaged and killed; excludes markers and pure projectiles. |
| `category` | enum | `mob` | `mob`, `item`, `projectile`, `player`, `marker`. |
| `width` | float | `0.6` | Collision box width in blocks, greater than 0 and at most 64. |
| `height` | float | `1.8` | Collision box height in blocks, same bounds. |
| `ghostMode` | enum | `interpolated` | `interpolated`, `predicted`, `ownerPredicted`. Predict only what is locally controlled or latency-sensitive. |
| `ghostOptimization` | enum | `dynamic` | `dynamic`, or `static` for something that rarely moves. |
| `ghostImportance` | byte | `1` | Relative replication priority; NetCode spends its snapshot budget highest first. |

The ghost fields are flat, not a nested `ghost` object.

### Models

A model is a list of boxes in the manner of a Minecraft entity model, because
that is what this game's mobs are and because it keeps a dedicated server free of
mesh assets. Box coordinates are model units of 1/16 block, matching the block
model convention.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `key` | string | required | Namespaced identity, by convention `digblocks:models/<name>`. |
| `texture` | string | required | Texture key. |
| `textureSize` | int pair | `[64, 32]` | Texture dimensions the box UVs are laid out against. |
| `boxes` | array | required | At least one box. |

Each box:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | string | required | Part name. Becomes the addressable part that programmatic animation will pivot. |
| `size` | 3 numbers | required | Extent in model units. |
| `origin` | 3 numbers | `[0,0,0]` | Corner position relative to the pivot. |
| `pivot` | 3 numbers | `[0,0,0]` | Where the part rotates about. |
| `uv` | 2 integers | `[0,0]` | Top-left of the box's unwrap in the texture. |

A box sits on its origin rather than being centred on it, which is what puts a
mob's feet on the ground rather than half a body through it.

## Shipped content

One archetype, one type, one model — deliberately minimal, but shipped through
the real pipeline rather than hardcoded, so the pipeline is exercised rather than
stubbed:

- `entity_archetypes/mob.json` — an ordinary mob: persists, falls, collides,
  living, interpolated dynamic ghost.
- `entities/debug_orbiter.json` — the debug mob the F2 key spawns. Inherits
  `mob`, then turns off gravity, collision and life, because it is a marker
  flying a fixed path. It persists, so walking away and coming back finds it
  still flying.
- `entity_models/debug_cube.json` — one box, a shade under a block across.

`ShippedEntityContentTests` loads exactly what ships and asserts the resolved
result, so a content edit that breaks the pipeline fails in the editor rather
than in a build.

## What the fingerprint covers

`EntityTypeRegistry.Fingerprint`, version `DigBlocks.EntityRegistry.v1`, covers
the type key, behaviours, and every field of the resolved `EntityTypeAttributes`.
It deliberately excludes model and appearance: a client with a different mob
texture is not a simulation divergence, while a different hitbox is one.

Peers compare fingerprints at binding time. NetCode independently hashes the
ghost prefab collection and drops connections that disagree, so the fingerprint
is not the only guard — it is an earlier, legible failure rather than an opaque
disconnect after going in-game.

Because ghost policy is in the fingerprint and ghost prefabs are built in runtime
id order, **adding or removing an entity type changes every peer's view of the
collection**. Content has to match across a session; it is not per-client.

## Deferred by design

- Per-type loot, drops, spawn rules and AI parameters. They belong in this
  pipeline, but nothing consumes them yet and an unread field is a lie.
- Animation. Named boxes exist so that programmatic animation has something to
  pivot, but nothing animates them yet.
- Localised display names. Nothing shows an entity name yet.

## Verification

- `EntityContentTests` covers parsing, archetype resolution, rejection of unknown
  fields and the failure messages.
- `EntityTypeRegistryTests` covers id assignment, ordering and the fingerprint.
- `ShippedEntityContentTests` covers what actually ships.
- `EntityPresentationTests` covers what a box list compiles to.
