# Debug menu implementation summary

Status: September 9, 2026. Records what the debug menu and debug view milestone
built, what it deliberately left open, and the evidence behind it. The overlay and
its navigation contracts are documented in [UI and menu foundation](ui-menu-foundation.md);
the terrain views are documented in [chunk meshing and terrain rendering](chunk-meshing-rendering.md).
This file is the implementation record, not the reference.

## What this milestone delivered

An in-game debug switchboard reached with `F3`, and the first three terrain debug
views behind it. The overlay reuses the existing menu coordinator rather than
introducing a parallel UI path, and the toggle state lives in `DigBlocks.Core` so
rendering can obey it without depending on client UI.

| Area | Outcome |
| --- | --- |
| Overlay | `Debug` menu on the overlay layer, catalog-registered, toggled by `F3` at any time |
| Isolation | Blocks no gameplay input, releases no cursor, takes no navigation focus, passes back through |
| Toggles | Declared in code with a display name, a key and a cycle of named states |
| Default state | State zero is what the switchboard starts and resets to; for a shading view that is `Off`, for a toggle choosing between equivalent modes it is the ordinary one |
| Keybinds | One standalone `InputAction` per toggle, live only while the overlay is open |
| State | `DebugOptions` in Core, keyed by `DebugToggleId`, with change notification |
| Wireframe | `F8`, screen-space triangle edges from barycentrics: off, over the surface, wires only |
| Fullbright | `F7`, authored surface colour with no lighting, ambient, shadowing or fog |
| Overdraw | `F6`, additive layer count as a red-to-white heat ramp: off, every layer, shaded fragments only |
| Secondary camera culling | `F5`, whether a viewport other than the session's culls for itself or is handed the main camera's visible set |

## Menu system changes

The overlay is informational and sits above the pause menu, so two gaps in the
coordinator had to close before it could coexist with real navigation.

| Addition | Why |
| --- | --- |
| `MenuBackBehavior.PassThrough` | back steps over the overlay and reaches the menu beneath, so `Escape` still opens and closes pause |
| `MenuMetadata.TakesNavigationFocus` | cleared, the overlay never becomes the focus target and cannot steal the pause menu's selected control |
| `CompositeMenuViewBinder` | the coordinator holds one binder; flow and the debug controller now share it |

Both metadata additions are catalog fields, so they are available to any future
HUD-like overlay without further coordinator work. The new `MenuMetadata` parameter
is optional and defaults to the previous behaviour, so no existing registration
changed meaning.

## Assemblies

No new assemblies. Four gained code, and the dependency direction is unchanged.

| Assembly | Added |
| --- | --- |
| `DigBlocks.Core` | `Diagnostics`: `DebugToggleId`, `DebugToggleIds`, `IDebugOptions`, `DebugOptions` |
| `DigBlocks.Client` | `Debugging`: `DebugToggle`, `DebugToggleCatalog`, `DebugMenuController` |
| `DigBlocks.Client.UI` | `DebugMenuEntry`, `DebugMenuView`, `CompositeMenuViewBinder`, and the two metadata additions |
| `DigBlocks.Client.Rendering` | `TerrainDebugBinder`, overdraw render state on `TerrainRenderer`, debug wiring in `TerrainRenderService` |

`DigBlocks.Client.UI` still depends only on Core, uGUI, TextMeshPro and the input
system. It never sees a toggle declaration: the controller in `DigBlocks.Client`
pushes finished `DebugMenuEntry` lines into the view. `DigBlocks.Client.Rendering`
never sees the menu; it observes `IDebugOptions` from Core. The two sides meet only
at the well-known identifiers in `DebugToggleIds`.

## Why the state lives in Core

`DigBlocks.Client.UI` cannot reference `DigBlocks.Client.Rendering`, and rendering
must not reference client UI. A switchboard both sides can reach therefore belongs
in Core, which neither can avoid. `DebugOptions` stores a state index per identifier
and declares nothing about what a toggle means, so adding a toggle costs Core
nothing beyond a well-known id when the consumer lives in another assembly.

## Terrain shader

All three views share the existing four passes. Nothing was added to the material
template, and no second material or resolve pass exists.

Wireframe uses true barycentrics. Packed quads are drawn as a non-indexed triangle
list, so `SV_VertexID % 3` names a triangle corner exactly and `TerrainVertex` can
emit them with no index buffer, geometry shader or extra vertex data. `fwidth` turns
them into a constant screen-space line width. Wires-only clips in the forward, depth
and depth-normals passes together, so a depth prepass cannot prime a solid surface
that the lit pass then hides behind.

Overdraw is the one view that needs render state rather than just a global. `Blend`,
`ZWrite` and `ZTest` expressions resolve material properties and cannot read shader
globals, and a `MaterialPropertyBlock` does not affect render state either. The mode
is therefore split: `TerrainDebugBinder` sets the process-wide globals, and
`TerrainRenderService` applies blend and depth state to the session's own material
clones and forces a black camera clear, because additive counting turns any other
clear colour into phantom layers.

The ramp is additive and saturating rather than a per-layer palette. A true rainbow
needs a nonlinear count-to-colour remap, which fixed-function blending cannot do;
the alternatives were a URP renderer feature or a full-resolution opaque texture
copy in every build, and neither was worth it for a debug view. The default step is
linear `(1, 0.09, 0.02)`, so red saturates on the first layer and green then blue
follow: red, orange, yellow, white. `TerrainDebugBinder.OverdrawStep` retunes the
spread without a rebuild.

## Files

Runtime:

* `Scripts/Core/Diagnostics/` — `DebugToggleId.cs`, `DebugToggleIds.cs`, `IDebugOptions.cs`, `DebugOptions.cs`
* `Scripts/Client/Debugging/` — `DebugToggle.cs`, `DebugToggleCatalog.cs`, `DebugMenuController.cs`
* `Scripts/Client/UI/` — `DebugMenuEntry.cs`, `CompositeMenuViewBinder.cs`, `Views/DebugMenuView.cs`, and edits to `MenuBackBehavior.cs`, `MenuMetadata.cs`, `MenuCatalogEntry.cs`, `MenuCoordinator.cs`, `MenuIds.cs`
* `Scripts/Client/Rendering/` — `TerrainDebugBinder.cs`, and edits to `TerrainRenderer.cs` and `TerrainRenderService.cs`
* `Scripts/Bootstrap/` — edits to `ClientPresentation.cs`, `ClientPresentationComposer.cs`, `DigBlocksBootstrap.cs`

Shaders, under `Assets/_Project/Shaders/Terrain`: `Terrain.shader` and
`TerrainGeometry.hlsl`.

Assets: `Prefabs/UI/DebugMenu.prefab` carries `DebugMenuView` bound to its title and
control-line labels; `UI/MenuCatalog.asset` registers the `Debug` entry.

Tests, under `Assets/_Project/Tests/EditMode`: `Core/Diagnostics/DebugOptionsTests.cs`,
`Client/Debugging/DebugToggleCatalogTests.cs`, and additions to
`Client/UI/MenuCoordinatorTests.cs`. `DigBlocks.Client.Tests` gained a
`Unity.InputSystem` reference; the generated assembly map is unchanged because it
records internal references only.

## Verification

Fresh evidence at the end of the milestone:

* EditMode: 195 of 195 pass, up from 182 before this work. The 13 added tests cover
  toggle state cycling and notification, back routing over a pass-through overlay,
  back reported unhandled when only pass-through menus are open, interaction and
  input requirements under a non-blocking overlay, and the catalog invariants.
* PlayMode: 59 of 60 pass. `DigBlocksBootstrapTests.SampleScene_BootstrapReachesTitle`
  fails because it loads a scene named `SampleScene` that does not exist in the
  project and is not in the build profile. It is unrelated to this work and failed
  before it.
* Shader: `DigBlocks/Terrain` reports supported, no errors, no messages, four passes,
  and render-state defaults of `One`/`Zero`, depth write on, depth test `LEqual` —
  ordinary opaque rendering when nothing is toggled.
* `tools/Test-LeanWorkflow.ps1` passes.

Not verified by automation: how the views actually look. The heat ramp constants,
wire thickness and wire colour were chosen analytically for linear colour space,
and are exposed as settable properties for that reason.

## Not in this milestone

Live readouts of any kind — frame timings, chunk residency, quad counts, camera
position — even though `TerrainRenderService` already exposes `BuiltChunks` and
`Quads`. Toggle state does not persist across runs, keys are not rebindable, and
toggles are declared in code rather than authored as an asset. The menu shows name
and key only; the named states each toggle cycles through exist in the data and are
logged, but are not displayed. Overdraw covers terrain, which is currently all
rendered geometry; a view spanning arbitrary future renderers would need a URP
renderer feature. The shadow pass still runs while overdraw is active, which costs
time without affecting the counts.
