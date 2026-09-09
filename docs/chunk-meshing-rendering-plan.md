# Chunk meshing and terrain rendering plan

Status: design approved by the user on September 8, 2026. Proceed with the recommended opaque-cube/free-fly milestone and desktop backend. DX12 and Vulkan functional feasibility checks passed; see [backend verification](terrain-backend-verification.md). Performance remains to be measured on the integrated pipeline.

Implementation status and final contracts: [chunk meshing and terrain rendering](chunk-meshing-rendering.md). The design below records the approved direction; the implementation document records actual limits and verification.

## Outcome and specification

Enter a single-player world through the existing menu and see lit, textured chunk geometry sourced from the admitted client replicas. Keep the same presentation path available to remote clients and omit it from dedicated servers. The user-supplied Voxel Engine Meshing & Texture Rendering Specification is the requirements baseline.

Greedy merging, deterministic per-face rotation, texture arrays, compact geometry, asynchronous meshing, reusable GPU storage, culling and indirect batching all belong to this milestone. Distant LOD is a separate future representation. Proposed initial content scope is opaque full cubes and an inspection camera; transparency, fluids, custom models, collision and terrain generation await scope confirmation.

## Existing boundaries and gaps

- Voxels owns 32-cube storage, registry and attributes. Preserve its independence from UnityEngine, Entities and NetCode.
- Voxels.Appearance already exposes native per-state and six-face appearance tables. Face records contain texture, fixed rotation and tint, but random rotation is currently a state-wide flag.
- Voxels.Runtime owns resident ECS identities and coherent detached replica captures. Presentation must consume client replicas, never authoritative server storage.
- Client/Rendering/Editor already imports block texture arrays. Reuse blocks.blockarray and its existing slice order; validate material bindings against compiled content.
- Bootstrap composes session services. Renderer lifetime must end before its client store and appearance inputs are disposed.
- Current streaming supplies a small dummy neighborhood. This is sufficient for first visualization; a broad generation or interest-streaming rewrite is outside this renderer plan.

## Proposed architecture

### Mesh input and invalidation

Keep one rebuild region per existing 32-cube chunk initially. Capture its solids plus six neighboring border slabs into pooled native job input. Greedy faces need no diagonal neighbors. Treat absent neighbors as air provisionally, then rebuild the affected boundary when they arrive or leave.

Track a mesh input stamp containing session generation, interest epoch, world-qualified address, incarnation, revision, appearance generation and each neighbor's presence/incarnation/revision. Validate the full stamp before publication. A center revision alone cannot establish border freshness. Publish notifications or an equivalent bounded change feed from the existing store; do not scan or copy all resident voxel payloads every frame. Overflow must trigger bounded reconciliation without losing dirty state.

Interior changes dirty the owning chunk. Boundary changes dirty the corresponding neighbor as well. If publication lacks cell-level changes, conservatively dirty the six immediate neighbors on revision changes until border-change metadata is available. Coalesce repeated dirtiness, retain the previous valid mesh during rebuilding, and never retain geometry after eviction or session replacement.

### Appearance and greedy algorithm

Extend appearance authoring and compilation with explicit per-face random-rotation policies, preserving existing block-wide defaults. Fixed rotation and randomized quarter turns need documented composition and inheritance rules. Configure grass top/bottom for random turns and side faces for upright fixed orientation. Appearance-only changes remain outside the simulation fingerprint.

Resolve face visibility from shape/opacity and appearance, then generate a render descriptor. Merge identity includes material, layer, resolved rotation and tint, with face direction implicit in each sweep. Do not merge by state id or include lighting. Keep material-specific resolution outside the rectangle algorithm.

Use Burst jobs for six direction-specific slice sweeps. Reuse a 32-by-32 mask per job; greedily consume equal rectangles. Specify and test all six position/UV bases and winding. UVs repeat once per voxel, including rotated non-square rectangles. Hash stable world voxel coordinates, direction and an explicit stable visual seed; never use mutable RNG or runtime string hashing.

### Packed geometry and draw backend

Recommended candidate: packed quad records with shader vertex pulling. Store local anchor, two extents, direction, rotation, texture layer and tint; material partitions the batch. Candidate storage is 12 bytes per quad including a stable chunk metadata index, subject to actual packing/alignment validation. Chunk origins live in a separate metadata table. Six procedural vertices reconstruct two triangles without storing six full vertices.

Compare this candidate against compact indexed vertices in an early graphics spike. Measure vertex/decode cost and actual draw submission, rather than assuming a Unity multi-command API becomes hardware multi-draw.

Proposed built-in batching path: GPU frustum/range culling of chunk bounds, followed by compact visible quad references and one indirect instanced-quad draw per compatible material/pass. This avoids a CPU draw per chunk without a native graphics plugin. Keep geometry resident; compact references, not full geometry, each frame. Cost of reference expansion and tiny quad instances must be measured. Native MDI is a separate architectural option only if measured results justify its platform and maintenance costs.

### GPU ownership and streaming

Use large long-lived geometry arenas with a coalescing free-list allocator. Upload replacements to new ranges, publish new metadata only after upload ordering is established, and retire old ranges behind completion fences covering every consumer pass. Never overwrite an in-flight range or assume a fixed number of elapsed frames guarantees safety.

Use reusable upload staging regions supported by the selected API. Unity LockBufferForWrite does not guarantee direct GPU mapping; choose between mapped upload buffers and staging-copy storage after target validation. Bound arena capacity, pending upload bytes, job count and retired bytes. Under pressure, defer work and preserve current geometry rather than block the render loop. Pool job inputs/outputs; define a checked worst-case output capacity for fragmented chunks and avoid silent truncation.

Reclaim all allocations on unload, retire safely across session changes, and handle zero-quad chunks explicitly. Growth must be infrequent, budgeted and fence-safe. GPU-visible chunk slot reuse needs the same lifetime discipline as geometry reuse.

### Scheduling, presentation and lighting

Maintain a coalescing dirty queue prioritized by distance/camera relevance with aging to prevent starvation. Separate capture, worker execution, completion validation and upload budgets. Complete only finished jobs during streaming; shutdown may join outstanding jobs before freeing their input.

Place pure meshing under a client-only meshing assembly and Unity graphics ownership under a client rendering assembly. ECS coordinates chunk work; GameObjects are suitable for camera and presentation authoring. Bootstrap supplies content, world access and renderer configuration through existing service composition.

Integrate with the installed URP 17.6 render path. Confirm forward/depth/shadow participation in the spike: ordinary URP lighting must not become a substitute unlit shader. Decide terrain shadow casting/receiving explicitly because procedural custom-pass draws do not automatically gain every MeshRenderer pass. Cull separately for camera and shadow views when applicable. Resolve authored tint indices through a small client palette.

Add a free-fly inspection camera and useful initial framing of the existing dummy chunks if approved. Respect menu focus and pause input. Distinguish replica readiness from visual readiness; do not change NetCode in-game admission to make the camera work.

## Implementation sequence and verification gates

1. Settle platform/content scope and packed-quad versus indexed/native backend direction. Validate actual target graphics capabilities and URP pass behavior in a small spike.
2. Extend per-face rotation contracts and content. Test inheritance, explicit overrides, grass policies and unchanged simulation fingerprints.
3. Implement isolated Burst greedy meshing and packing. Test empty/full chunks, all six directions, uniform walls, render-equivalent states, rotation splits, deterministic negative-world coordinates, tint/material splits, neighbor occlusion, UV orientation and worst-case capacity. Compare emitted coverage to a simple exposed-face oracle on randomized small inputs.
4. Add revision-aware input scheduling and pooled buffers. Test neighbor arrival/removal, edit coalescing, stale center/neighbor results, epoch replacement, eviction/reentry, capacity limits and shutdown with jobs running.
5. Implement GPU arena and indirect renderer. Test allocator fragmentation/coalescing and pending retirements; visually validate packing, culling, tiling, tint and lighting. Verify fences and submissions with graphics tooling on each accepted API.
6. Wire session composition, assets and inspection camera. Exercise Play, return to title, reenter, client disconnect, empty meshes, remote client and dedicated server exclusion. Include chunk-border changes while jobs/uploads are pending.
7. Run relevant fresh Unity compilation, EditMode and PlayMode checks. Record actual graphics validation separately from headless tests. Capture CPU meshing time, upload bytes, quad count, arena live/retired capacity, draw count and GPU time for uniform, randomized and fragmented terrain. Avoid an unmeasured fastest-renderer claim.

Update architecture, block definitions and meshing-readiness documentation with final implemented contracts. Regenerate the assembly map when asmdefs change. Preserve unrelated worktree changes, currently Assets/_Project/splashes.txt.

## Original decision checklist (resolved by the approved milestone)

- Target platforms and minimum graphics APIs.
- Initial opaque-cube/inspection scope versus other geometry and interaction.
- Packed quads with built-in indirect instancing versus a native MDI investment.
- Required initial lighting features, especially terrain shadow casting.
- Stable visual seed source, exact resource budgets and device limits after code/API inspection.

## API evidence to validate against the installed editor

- Unity procedural indirect API: https://docs.unity.cn/2023.3/Documentation/ScriptReference/Graphics.RenderPrimitivesIndirect.html
- Unity mapped-write caveats: https://docs.unity3d.com/ja/2022.3/ScriptReference/GraphicsBuffer.LockBufferForWrite.html
- Unity multi-draw issue record: https://issuetracker-mig.prd.it.unity3d.com/issues/graphics-dot-rendermeshindirect-does-not-issue-multi-draw-rendering-commands-when-using-a-graphics-api-capable-of-multi-draw-commands

These establish risks to check, not proof of behavior on this project's Unity 6.6 installation.
