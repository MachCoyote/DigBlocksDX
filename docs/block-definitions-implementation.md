# Block definition implementation summary

Status: September 8, 2026. Records what the block definition milestone built,
what it deliberately left open, and the evidence behind it. The subsystem itself
is documented in [block definitions](block-definitions.md); this file is the
implementation record, not the reference.

## What this milestone delivered

An authoring layer above the existing `BlockRegistry`, compiled into two
independent runtime outputs, plus the first six blocks.

| Area | Outcome |
| --- | --- |
| Authoring | JSON documents under `Assets/StreamingAssets/content/digblocks`, flat fields, optional everything but `key`, unknown fields rejected by origin |
| Inheritance | Material archetypes chain through `archetype`; nearest layer wins per field, tags union |
| States | Property Cartesian expansion into canonical state keys, capped at 256 per block, with `when`-matched per-state overrides |
| Simulation | 24-byte unmanaged `BlockAttributes` per state, plus a caller-owned `NativeArray` table for jobs |
| Appearance | Client-only packed per-state render material and six face records, in its own assembly |
| Compatibility | Registry fingerprint bumped to `v2`, covering attributes and identity keys but no appearance |
| Content | air, bedrock, dirt, grass_block, stone, testblock, plus the reserved empty fluid |

## Assemblies

Two new assemblies were added and one existing one extended.

| Assembly | Added | Purpose |
| --- | --- | --- |
| `DigBlocks.Voxels` | extended | Definitions, resolution, expansion, compiler, attributes, attribute table |
| `DigBlocks.Voxels.Content` | new | JSON schema, content sources, loading and validation; owns the Newtonsoft dependency |
| `DigBlocks.Voxels.Appearance` | new | Packed client render table; nothing else may depend on it from a server path |

`DigBlocks.Voxels` still references only Collections, Mathematics and Burst. It
gained no UnityEngine, Entities or JSON dependency, so the portable storage and
codec path is unchanged. `DigBlocks.Bootstrap` gained a `DigBlocks.Voxels.Content`
reference because it is the composition root that supplies the content path.

## Files

Runtime, under `Assets/_Project/Scripts/Voxels`:

- `BlockAttributes.cs`, `BlockAttributeTable.cs`, `ResourceKeys.cs`
- `Definitions/BlockDefinition.cs`, `BlockAttributeOverrides.cs`, `BlockAppearance.cs`
- `Definitions/BlockContentCompiler.cs`, `CompiledBlockContent.cs`, `BlockContentException.cs`
- `Content/BlockContentSource.cs`, `BlockContentJson.cs`, `BlockContentLoader.cs`
- `Appearance/BlockAppearanceTable.cs`

Modified: `BlockRegistry.cs` (attributes, optional identity keys, fingerprint v2,
attribute table factory), `Bootstrap/GameServiceComposer.cs` and the new
`Bootstrap/BlockContentProvider.cs`.

Tests: `Tests/EditMode/Voxels/BlockContentCompilerTests.cs`,
`Tests/EditMode/VoxelContent/`, `Tests/EditMode/VoxelAppearance/`.

## Decisions worth remembering

- **Material means renderer material.** `material` in content names a render
  material owning one texture array, not a gameplay family. The gameplay defaults
  bundle is `archetype`. Render layer moved out of the fingerprinted attributes
  entirely; simulation keeps only `BlockFlags.Opaque` for light and face culling.
- **Textures are raw slice indices**, validated against the `slices` count the
  material declares. There is no texture key table and no atlas: the array asset
  and the definitions are authored together against the same slice order.
- **Appearance is outside the fingerprint.** A client texture, material or tint
  difference must never fail companion binding; an attribute difference must.
- **Compatibility ctor retained.** `StateDefinition`'s original five-argument
  constructor survives as a defaulting overload, so existing registry fixtures and
  tests were not touched by the attribute change.
- **The attribute table is caller-owned.** `BlockRegistry` did not become
  `IDisposable`; a world builds one table and shares its read-only view. Blob
  assets stay available as a later upgrade if profiling wants them.
- **Air is a real definition**, not an absence, and holds reserved solid id 0.
  `digblocks:empty` is its fluid-channel counterpart and is structural.

## Verification

- Full EditMode run: 97 passed, 0 failed, 0 skipped.
- `DigBlocks.Voxels`, `DigBlocks.Voxels.Content`, `DigBlocks.Voxels.Appearance`
  and their three test assemblies all compile clean.
- `tools/Test-LeanWorkflow.ps1` passes; `docs/generated/assembly-map.md` regenerated.

Not verified: `DigBlocks.Bootstrap` did not build during this milestone, so the
`GameServiceComposer` change from `BlockRegistry.CreateDummy()` to the compiled
registry is unexercised. `DigBlocks.Client.UI` fails to compile because it
references a `DOTween.Modules` assembly definition that is not present in the
worktree, which makes `Image.DOColor` resolve to the `Camera` overload; Bootstrap
depends on Client.UI and is skipped in turn. That is unrelated in-progress UI work
and was deliberately left untouched. PlayMode tests were not run for the same
reason, so the fingerprint bump has not been exercised against live companion
binding.

## Next

Binding render material indices to real `Texture2DArray` assets and Unity
materials is the remaining gap between this milestone and meshing. The decisions
still open for meshing are listed in
[from chunk data to meshing](chunk-meshing-readiness.md).
