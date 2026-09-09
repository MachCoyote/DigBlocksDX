# Meshing milestone summary

September 9, 2026.

The approved opaque-cube milestone connects the existing authoritative chunk pipeline to visible, textured terrain. Entering Play loads a nine-chunk development fixture through normal replication; a free-fly camera inspects it. Visual readiness waits for populated fixture revisions and current mesh output. The fixture is bounded development content, not persistence or world generation.

## Implemented

- Burst greedy meshing with face-coverage tests, render-equivalent merging, deterministic per-face rotation, upright grass sides, and no baked lighting/AO merge constraints.
- A 12-byte packed quad format, procedural vertex reconstruction, texture arrays, authored tint, URP lighting, shadow, depth and normal passes.
- Reusable detached input buffers, revision/neighbor/epoch validation, coalesced rebuilds, neighbor invalidation and safe stale-result rejection.
- A fixed GPU geometry arena, mapped upload staging, per-frame upload budgets, compute visibility lists, indirect material batches and completion-protected allocation reuse.
- Per-key Material assets, cloned per session with texture-array overrides. The default Opaque asset exposes base color, smoothness and metallic; the shader reads all three. Source edits apply to the next session and teardown destroys only clones.
- Session integration, an authoritative fixture, render settings, asset setup and backend verification tools. Architecture, appearance authoring and assembly documentation are updated.

## Verification recorded during implementation

The affected EditMode suite passed 81 tests, followed by the additional fragmented-capacity test. Three targeted PlayMode tests passed together, covering world/restart and scheduler lifecycle. The material patch then passed its new shader-property test and two PlayMode tests covering rendered template color, clone isolation/source survival, and normal scene restart. These are focused checks, not a claim that the entire repository test suite ran.

DX12 and Vulkan functional backend preflight passed on the RTX 3080 Ti. Integrated world tests used DX12 and rendered 9,243 quads (110,916 bytes of live geometry). The fragmented checkerboard test emitted 98,304 quads without truncation. Warmed editor schedule-plus-completion averages were 1.416 ms uniform, 2.189 ms checkerboard, and 3.767 ms random; these are CPU microbenchmarks rather than GPU/frame timings.

Large-world GPU profiling, integrated Vulkan world testing, remote-client visual testing and allocation-rate profiling remain future validation. Transparent/semi-transparent rendering, fluids, custom models, collision and distant LOD are outside this milestone.

## Additional workspace changes included at the user's request

The commit also includes the current authored texture-slice updates, splash text additions, DebugMenu prefab, UIWorkbench scene removal of its MainMenu instance, and EditorSettings play-mode option change. These workspace edits are preserved as requested; the terrain checks do not establish full DebugMenu behavior.

## References

- [Implementation, controls, budgets and material authoring](chunk-meshing-rendering.md)
- [Approved design record](chunk-meshing-rendering-plan.md)
- [DX12/Vulkan backend evidence and limitations](terrain-backend-verification.md)

Pre-commit checks on September 9: refreshed Unity compilation reported no console errors; lean workflow validation and whitespace checks passed. No runtime code was changed during this documentation/commit pass, so the successful targeted tests above were not repeated.
