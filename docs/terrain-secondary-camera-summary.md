# Terrain secondary camera implementation summary

September 9, 2026.

Terrain now draws into cameras the session does not drive: a Scene view during Play Mode, and any additional runtime camera. Previously an indirect submission named the session camera and nothing else, so every other viewport rendered an empty world even though the terrain was fully resident. A debug toggle switches secondary viewports between culling for themselves and reusing the main camera's visible set, which makes the culled geometry directly observable from outside the main camera's frustum. This feature does not add edit-mode terrain, a URP renderer feature, or per-camera meshing.

## Why nothing appeared before

`Graphics.RenderPrimitivesIndirect` submissions are addressed through `RenderParams.camera`. The renderer set that field to the session's own camera, which restricts the draw to that camera alone; terrain also never becomes a GameObject, so no scene traversal could find it. Two properties of Scene view cameras compound this: they are absent from `Camera.allCameras` because they are not enabled cameras, and they report `isActiveAndEnabled` as false, so the renderer's own entry guard would have rejected one had it been passed in.

## Implemented ownership

- `TerrainCameraSet` owns eligibility and enumeration. It accepts enabled base Game cameras whose culling mask contains the terrain layer, plus every open Scene view under `UNITY_EDITOR`. Overlay cameras are rejected because they draw into the base camera stacking them; preview and reflection cameras are rejected because they render outside the frame loop and would strand a submission. Scene view cameras are admitted on type alone.
- `TerrainRenderer` owns per-camera cull state. A `CameraSlot` holds one camera's frame ring and its last built frame. The session camera's slot lives as long as the renderer; a secondary camera builds one the first frame it culls for itself.
- `TerrainRenderer` owns submission lifetime. A `Frame` counts outstanding submissions, and its completion token is inserted only when the last camera holding it finishes rendering.
- `TerrainDebugBinder` owns the culling mode read, alongside the existing overdraw read. The mode changes no shader or camera state, so unlike overdraw the renderer applies it without a binder instance.
- `TerrainRenderService` owns the runtime override for whether secondary viewports draw at all.

## Per-camera contract

`Draw` takes the session camera, submits it first, then fans out over the eligible set. Each camera culls for its own view: the occlusion graph holds one visible set at a time, so a camera traverses into it and uploads the result to its own frame before the next camera overwrites it. Only the session camera's pass is recorded, so `CameraVisibleChunks`, `GraphCulledChunks` and `CameraVisibleQuads` keep meaning what they meant.

Cull output cannot be shared between cameras, so an independently culling camera needs its own ring of append buffers. Rings are allocated on first use and handed back once the camera has been idle for sixty frames and every frame it holds reports GPU completion, releasing the geometry references those frames retained. A failed allocation disables secondary viewports and logs, rather than taking the session down.

Mirroring allocates nothing. The secondary camera is handed the session camera's frame exactly as it stands, so the terrain it shows is the terrain that camera kept, and the holes are what frustum, distance and graph culling removed.

A frame is recycled only after every camera holding it has rendered, because a mirrored frame outlives the camera that built it. A camera that never reaches the pipeline — a Scene view hidden behind the Game tab, a camera destroyed mid-frame — would otherwise hold its frame pending forever and drain the ring, so a submission left over from an earlier frame is resolved at the start of the next draw. That hazard existed for the single-camera path as well and is now closed for both.

## Configuration and diagnostics

`TerrainRenderSettings.SecondaryCameraRendering` is the authored default for the whole behaviour and is enabled in the default asset. `TerrainRenderService.SecondaryCameraRendering` overrides it at runtime and falls back to the authored value until something sets it, so a user setting can switch extra viewports off wholesale without knowing the renderer exists.

`TerrainRenderSettings.SecondaryFrameSlots` sizes a secondary ring and defaults to 2 against the session camera's 3. A shallower ring costs less memory and only risks reusing the previous frame's visible set when the GPU still holds both slots, which the existing fallback path already handles. At the shipped budgets a secondary camera costs roughly 16 MB of append buffers, scaling with material count; the session camera's own cost is unchanged.

The `F5` debug toggle cycles `Own View` and `Mirror Main`. `Own View` is state zero, which is what the switchboard starts and resets to. This is the first toggle whose zero state is an ordinary mode rather than `Off`, because the neutral behaviour of a viewport is to show what it can see; the shading toggles retain their `Off` contract.

## Verification evidence

227 EditMode tests pass. New coverage asserts that ordinary Game cameras other than the session's are collected, that disabled, overlay and layer-masked cameras are rejected, that the culling mode clamps to the states the menu offers, and that open Scene views are collected despite reporting themselves disabled. That last test ran rather than skipping, so a real Scene view camera was enumerated. The catalog contract was split: every toggle must name at least two distinct states, and separately, a toggle that alters how the world is shaded must be off at state zero.

61 of 62 PlayMode tests pass. A new terrain test verifies the feature at pixel level against real render targets: with unlit albedo forced for determinism, a second camera renders the chunk in front of it while the session camera faces away and draws nothing, renders nothing while mirroring that camera's empty visible set, and renders nothing again once secondary rendering is switched off. An earlier draft of this test failed by submitting the draw at the tail of a frame, which confirmed that a submission must be made during a frame's update phase to reach that frame's rendering — the phase `TerrainView` already uses.

The single PlayMode failure, `DigBlocksBootstrapTests.SampleScene_BootstrapReachesTitle`, is unrelated and pre-existing: it loads `SampleScene`, which does not exist in the project and is not in the build profile. Fresh Unity compilation completed without errors; the only warnings were two existing `FindFirstObjectByType` deprecations. Scene view rendering during a live session was additionally confirmed visually.

## Deliberate limitations and remaining validation

Terrain still exists only in Play Mode. Edit-mode terrain would need a parallel lifecycle standing up a client world, chunk store and mesh scheduler outside play, with teardown across domain reloads, and is deliberately not attempted.

A Scene view that repaints out of step with the frame loop can, at worst, show one frame of stale culling after its submission is resolved as stranded. Secondary cameras also submit their own shadow-only batches, so an extra viewport costs an extra shadow pass. Per-camera occlusion traversal is CPU work repeated per camera per frame; at the shipped chunk ceiling this is bounded and unmeasured against a large residency. Split-screen and portal cameras are supported by the eligibility rules but are not exercised by any test.

## References

- [Detailed runtime, resource and authoring guide](chunk-meshing-rendering.md)
- [Terrain chunk occlusion implementation summary](terrain-chunk-occlusion-summary.md)
- [Debug menu implementation](debug-menu-implementation.md)
