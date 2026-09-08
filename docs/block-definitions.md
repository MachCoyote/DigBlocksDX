# Block definitions

Status: September 8, 2026. Definitions, the compiler, JSON content, the compiled
attribute table and the client appearance table are implemented. Texture arrays,
Unity materials and meshing are not; see [from chunk data to meshing](chunk-meshing-readiness.md).

## Three layers

Block content is authored once and compiled into two independent outputs.

| Layer | Owner | Content |
| --- | --- | --- |
| Authoring | `DigBlocks.Voxels.Content` | JSON documents for materials, archetypes and blocks |
| Simulation | `DigBlocks.Voxels` | `BlockRegistry` state table plus an unmanaged `BlockAttributes` table |
| Appearance | `DigBlocks.Voxels.Appearance` | Per-state render material and six packed face records |

`DigBlocks.Voxels` stays free of UnityEngine, Entities and JSON. The content
assembly owns the Newtonsoft dependency, the appearance assembly owns everything
a dedicated server build should not carry, and the composition root supplies the
content path so neither reaches for `Application` directly.

Definitions are managed load-time records. Nothing on a hot path reads them:
`BlockRegistry.CreateAttributeTable` and `BlockAppearanceTable.Create` produce
caller-owned native arrays indexed by runtime state id, which is what meshing and
simulation jobs consume.

## What the fingerprint covers

`BlockRegistry.Fingerprint` is the compatibility hash both peers compare during
companion binding. It is version `DigBlocks.Registry.v2` and covers the state key,
behavior key, model key, block-entity key, drops key, sound-set key, tags, and
every field of `BlockAttributes`.

It deliberately covers no appearance data. A client with different textures, a
different render material assignment or a different tint still binds, because a
texture difference is not a simulation divergence. A hardness or collision
difference is one, so it fails binding rather than mispredicting.

## Additive authoring

Every field except `key` is optional. Unset fields inherit from the archetype
chain and then from engine defaults, so a plain block is three lines:

```json
{ "key": "digblocks:dirt", "archetype": "digblocks:soil", "texture": 2 }
```

Archetypes are named bundles of defaults for both simulation and appearance, and
chain through their own `archetype` field. The nearest layer that sets a field
wins; tags union across the chain rather than replacing. A block restating the
block-wide `texture` or `tint` discards inherited per-face values for that field,
so "I set the texture" means every face, including one an archetype specialised.

`properties` expand into finite states through the Cartesian product of their
values, capped at 256 states per block. Expansion sorts by property name and
varies the last property fastest, so state ordering stays stable as properties are
added. Each combination becomes a canonical state key such as
`digblocks:oak_log[axis=y,lit=true]`, and `states` entries narrow attribute or
appearance overrides to the states whose values all match their `when` object.

## Authoring reference

Content lives under `Assets/StreamingAssets/content/digblocks/` in `materials/`,
`archetypes/` and `blocks/`. Files load in ordinal filename order. Fields are flat
rather than nested, and any unknown field is a load error naming the file — a
silently ignored typo would ship as a wrong block.

### Materials

A render material is one Unity material and the texture array it owns. There are
expected to be few: opaque, transparent, semi-transparent, and later PBR.

| Field | Meaning |
| --- | --- |
| `key` | Namespaced material key |
| `renderLayer` | `opaque`, `cutout` or `transparent`; opaque materials sort first so submeshes build in draw order |
| `slices` | Number of slices in this material's texture array; block texture indices are validated against it |

### Blocks and archetypes

| Field | Meaning |
| --- | --- |
| `key` | Namespaced block or archetype key, required |
| `archetype` | Layer to inherit from; on an archetype this is its parent |
| `channel` | `solid` or `fluid`; blocks only |
| `invisible` | Block is never meshed and needs no appearance; air and the empty fluid use this |
| `behavior`, `model` | Identity keys resolved by later systems |
| `tags` | Semantic groupings, unioned across the chain |
| `properties` | `{ "axis": ["y","x","z"] }` or `{ "axis": { "values": [...], "default": "y" } }` |
| `states` | Overrides narrowed by a `when` object of property values |
| `blockEntity`, `drops`, `sounds` | Optional identity keys |

Simulation fields, all optional: `opaque`, `fullCube`, `collides`, `replaceable`,
`permitsFluid`, `requiresTool`, `flammable`, `unbreakable`, `randomTicks`,
`hardness`, `blastResistance`, `friction`, `tool`, `toolTier`, `lightEmission`,
`lightAttenuation`, `flammabilityCatch`, `flammabilitySpread`. A non-opaque block
does not attenuate light unless it states an attenuation explicitly.

Appearance fields, all optional: `material`, `texture`, `textures`, `rotation`,
`rotations`, `tint`, `tints`, `randomizeRotation`. The face maps are keyed by
`down`, `up`, `north`, `south`, `west`, `east`, plus the shorthands `end` (up and
down) and `side` (the four horizontals). Groups apply before individual faces, so
a specific face always wins over the group that also covers it.

Texture values are raw slice indices into the array owned by the block's render
material. They are authored to match the array asset, so the array's slice order
and the content are maintained together.

## Shipped content

Six blocks are defined, plus the fluid channel's reserved empty value.

| Block | Id | Archetype | Textures |
| --- | ---: | --- | --- |
| `digblocks:air` | 0 | — | none; invisible |
| `digblocks:bedrock` | 1 | `digblocks:rock` | 5 on every face |
| `digblocks:dirt` | 2 | `digblocks:soil` | 2 on every face |
| `digblocks:grass_block` | 3 | `digblocks:soil` | 3 up, 4 on the four sides, 2 down |
| `digblocks:stone` | 4 | `digblocks:rock` | 1 on every face |
| `digblocks:testblock` | 5 | `digblocks:solid` | 0 on every face |
| `digblocks:empty` | 0 (fluid) | — | none; invisible |

The grass block is one block using three of the six slices, not several blocks.
Its bottom reuses the dirt slice, and only its top face carries the
`digblocks:grass` tint source — a tint name, not a block key. Nothing resolves
tint sources yet, so the key compiles to an index and is applied when meshing lands.

`digblocks:air` is a full definition rather than an absence: invisible,
non-opaque, non-colliding, replaceable, fluid-permitting, zero hardness. It holds
reserved solid id 0, which is what lets a chunk stay uniformly empty without
allocating cells. `digblocks:empty` is the same idea for the fluid channel and is
structural, not a placeable block.

The texture array owned by `digblocks:opaque` therefore holds six slices, in
order: testblock, stone, dirt, grass top, grass side, bedrock.

Runtime state ids are reserved-first then ordinal by key. They are runtime
registration order, not a save identifier — persistence must resolve stable keys,
as [chunk data architecture](chunk-data-architecture.md) requires.

## Deferred by design

- Texture arrays and Unity materials. The appearance table carries material
  indices and slice indices; nothing binds them to assets yet.
- Block-entity records. A definition can declare a `blockEntity` type key, which
  sets `BlockFlags.BlockEntity` and enters the fingerprint. Sparse records,
  schemas and activation remain deferred.
- Drops and sound sets exist as identity keys only, awaiting the item and audio
  systems. They are fingerprinted now so those systems do not force a second
  migration of the state table.
- Overrides can set a field but not clear an inherited one back to absent.
- One render material per state. A per-face material override is a natural
  extension of the face record if a block ever needs mixed materials.

## Verification

EditMode tests cover archetype precedence and tag union, per-face override rules,
deterministic property expansion and state-override matching, the fingerprint
including attributes while excluding appearance, attribute and appearance table
alignment to state ids, and rejection of unknown archetypes, cycles, out-of-range
slices, malformed JSON and unknown fields. `ShippedBlockContentTests` asserts the
compiled ids, attributes and slice assignments of the content above, so a content
edit has to update those assertions deliberately.
