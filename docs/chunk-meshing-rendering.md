# Chunk meshing and terrain rendering

September 8, 2026. Implemented opaque full-cube milestone for Unity 6000.6 / URP 17.6.

Open the Bootstrap scene, enter Play Mode, and choose Play. The authoritative development fixture supplies nine chunks through the existing replication channel. The inspection camera uses WASD, mouse look, Space / Left Control for elevation, and Left Shift for speed. Menu focus gates camera input. The bootstrap `useTerrainFixture` flag disables this bounded fixture; it is not world generation or persistence.

## Ownership and flow

`Voxels.Meshing/GreedyMesherJob.cs` runs Burst greedy sweeps on detached native input. `ChunkData.ScheduleSolidCopy` copies palette-backed solids into caller-owned reusable storage; mutation/disposal waits for that short copy, not the subsequent mesher. Each worker retains seven full source chunks and constructs a 34-cube padded input. This deliberately trades some copy bandwidth for a simple reusable capture contract; border-only copy optimization remains possible.

`Client/Rendering/ChunkMeshScheduler.cs` observes replica publication/reset events and schedules bounded workers from a distance-and-age priority queue. A revision conservatively invalidates the chunk and its six neighbors. Publication checks store identity, interest epoch, entry version, and all seven presence/incarnation/revision stamps. Empty output removes geometry. Reset clears visibility immediately; stale jobs finish safely and cannot publish. Readiness requires every admitted chunk's latest dirty version to be meshed.

`TerrainRenderer.cs` owns long-lived GPU resources. `TerrainRenderService.cs` binds it to the client ECS world and session lifetime. Dedicated servers create no renderer. Bootstrap waits for populated fixture revisions before visual readiness, preventing an initial air snapshot from briefly appearing as a hole.

## Packed geometry and batching

Each greedy quad occupies exactly 12 bytes:

| Word | Bit fields |
| --- | --- |
| Geometry | X/Y/Z anchor: 6 each; width/height minus one: 5 each; face: 3; reserved: 1 |
| Surface | Array layer: 16; tint: 8; rotation: 2; reserved: 6 |
| Owner | Chunk slot: 24; material: 8 |

The vertex shader reconstructs six triangle vertices, face normals, and repeating UVs. No generic vertex/index stream or baked lighting/AO is emitted. Merge identity is texture, tint, resolved rotation and material; direction is implicit in each sweep. State IDs do not prevent merging. Face appearance remains four bytes, with fixed rotation and randomization policy sharing one byte.

Compute culling builds references into resident geometry, with camera frustum/distance rejection and a conservative separate shadow list. One indirect camera submission and one shadow-only submission per material replace per-chunk draw calls. URP owns the actual lighting/depth/shadow passes; this is not a claim of native multi-command MDI. The shader implements forward lighting, shadows, depth and normals.

## Resource budgets and completion

Default settings live in `Assets/_Project/Resources/TerrainRenderSettings.asset`: two workers, 256 chunk slots, 1,048,576 quad arena entries, two upload slots, three frame snapshots, 4 MiB upload budget per frame, 512-unit range. Each worker reserves the checked worst case of 196,608 quads. One worst-case chunk's worth of arena space is kept out of the live geometry budget for replacement uploads. Fragmentation or outstanding frame references may still defer publication.

The default geometry arena is 12 MiB; visible-reference buffers consume 24 MiB for one material, and upload buffers another 4.5 MiB. Worker storage and chunk metadata are additional. More materials multiply visibility storage; configuration validates an eight-material ceiling. Capacity is fixed for this milestone; sustained exhaustion defers uploads and initial readiness eventually reports a timeout rather than silently dropping faces.

Mapped staging is copied into device geometry by compute. Replacement ranges are reference-counted by both uploads and frame snapshots. Retired ranges become reusable only after all owners release them. DX12 uses pollable fences; the verified Vulkan device uses asynchronous readback completion tokens because its async-queue fence cannot be polled. Ordinary streaming does not synchronously wait for the GPU. Session teardown drains the last submission before disposal.

## Verification and limits

See `terrain-backend-verification.md` for DX12/Vulkan functional preflight results. The integrated DX12 scene renders 9,243 packed quads (110,916 bytes of live geometry), with textured grass, stone wall, and the authored test-block checker. Both scene sessions pass pixel checks and return-to-title cleanup. A direct store/renderer integration test covers stale revisions, neighbor culling, neighbor removal, reset, and in-flight cleanup. EditMode coverage includes greedy face oracles, packing, merging, detached copies, allocation retirement, appearance and replica contracts.

The preflight covers both APIs; integrated world tests currently run on DX12. No integrated Vulkan performance result is claimed. CPU/GPU frame-time profiling at large residency, allocation-rate profiling, remote-client visual testing, and aggressive occlusion culling remain follow-up validation. Current scope excludes fluids, transparency, custom models, collision and distant LOD. These do not change the chunk protocol or simulation representation.

Fresh verification: 81 affected EditMode tests passed, followed by the added fragmented-capacity benchmark test (passed); three targeted PlayMode tests passed together. Lean workflow validation and whitespace checks passed. Warmed Burst schedule-plus-completion averages over 10 samples were 1.416 ms uniform (6 quads / 72 bytes), 2.189 ms checkerboard (98,304 / 1,179,648), and 3.767 ms random (32,943 / 395,316). These editor microbenchmarks include job dispatch/completion overhead and are not GPU or whole-frame measurements.

## Authored material templates

Each `TerrainRenderSettings.Materials` entry binds a material key to a source Material asset and its texture array. The default source is `Assets/_Project/Materials/Terrain/Opaque.mat`. Edit its Base color (RGB), Smoothness, and Metallic properties in the Inspector. Defaults preserve the previous rendering (white, 0.15, 0). The shader consumes these properties through its per-material constant buffer.

Each session clones the source once per key, enables instancing, and overrides `_BlockTextures` from that key's binding. Shutdown destroys only the clones. Asset edits apply when the next session creates its clones; editing the source during an existing session does not update those clones. The texture array remains owned by the settings binding, so changing the material's texture field does not override that binding.

The setup menu creates the opaque asset and fills missing opaque material references in existing settings without overwriting authored values. Bindings currently require the `DigBlocks/Terrain` shader and its forward/shadow/depth/normal passes. Per-key source assets support future render types, but transparent and semi-transparent rendering still require their own rendering implementation before those content layers can be enabled.

Material patch verification: the shader-property test first failed for missing editable properties and passed after implementation. Both PlayMode checks passed: the normal scene/restart test and a rendered-color test demonstrating session-owned clones, next-session template updates, and source-material survival on shutdown. See [milestone summary](meshing-milestone-summary.md) for the consolidated change record.
