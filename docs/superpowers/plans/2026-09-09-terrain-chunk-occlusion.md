# Terrain Chunk Occlusion Culling Implementation Plan

**Goal:** Add conservative Sodium-style chunk graph culling without changing the
URP render pipeline or claiming view-dependent packed-quad occlusion.
**Architecture:** Meshing computes revision-matched face connectivity from voxel
attributes; client rendering traverses resident chunks from the camera and supplies
a camera-visibility bit to the existing GPU batch compaction.
**Tech stack:** Unity 6.6, Burst, Jobs, Native Collections, indirect compute culling,
URP 17.6, NUnit.
**Spec:** ../specs/2026-09-09-terrain-chunk-occlusion-design.md

**Status:** Implemented and verified on September 9, 2026. See
`../../terrain-chunk-occlusion-summary.md` for the delivered files, measurements,
test evidence and remaining profiling work.

## Constraints

Preserve the 32-cube storage model, packed 12-byte quad format, existing geometry
arena and one camera submission per material. Use `Opaque && FullCube` as the sole
solid occluder predicate. Do not add a URP renderer feature, depth pyramid, GPU
readback, custom block models, transparent rendering or fluid occlusion. Graph
uncertainty must render too much rather than produce a hole. Keep shadow submission
independent of camera occlusion.

## 1. Specify connectivity with tests

- [x] Add `ChunkFaceConnectivity` under
  `Assets/_Project/Scripts/Voxels/Meshing/` with six-face bit indexing, opposite-face
  lookup and Burst-compatible connection queries.
- [x] Add failing EditMode tests in
  `Assets/_Project/Tests/EditMode/VoxelMeshing/` for empty/all-connected, fully
  opaque/no-connections, sealed planes, openings, disconnected components and every
  face orientation.
- [x] Add cases proving only a state with both `Opaque` and `FullCube` blocks a
  visibility flood. Include non-opaque full cubes and opaque partial blocks.
- [x] Implement `ChunkVisibilityJob` over the center of the existing padded voxel
  input. Use worker-owned fixed native scratch arrays and a `NativeReference<ulong>`
  result; allocate nothing during `Execute`.
- [x] Extend the warmed mesher benchmark report with isolated visibility-build and
  combined job timings for representative uniform, plane and fragmented inputs.

## 2. Publish geometry and connectivity atomically

- [x] Extend `ChunkMeshScheduler.Worker` with visibility scratch/result ownership.
  Schedule visibility and greedy meshing from the same completed padding job, then
  combine their handles.
- [x] Pass connectivity through `TerrainRenderer.TryPublish` together with slot,
  position and quads. Empty mesh results must still publish a resident graph node.
- [x] Preserve store, epoch, entry version and seven-neighbor stamp validation for
  the combined result. A stale visibility result must be rejected whenever its mesh
  would be rejected.
- [x] On a replica change, tell the renderer that graph results are temporarily
  untrustworthy before scheduling work. Re-enable graph culling only when the store
  is `DataReady` and every scheduler entry is current.
- [x] Ensure reset and disposal clear or complete graph state with the same lifetime
  guarantees as mesh state.

## 3. Implement conservative camera traversal

- [x] Add an allocation-free `ChunkOcclusionGraph` in
  `Assets/_Project/Scripts/Client/Rendering/`. It owns fixed arrays for positions,
  connectivity, incoming masks, queue membership and camera visibility, plus a
  position-to-slot lookup rebuilt only when nodes change.
- [x] Add EditMode tests in
  `Assets/_Project/Tests/EditMode/ClientRenderingRuntime/` for open chains, barriers,
  alternate paths, cycles, multiple incoming directions and camera positions on
  both sides of zero.
- [x] Seed all origin faces conservatively. Propagate only connectivity-approved
  outgoing faces and only to orthogonally adjacent resident chunks. Process a node
  again only when it gains a new incoming bit.
- [x] Fail open when disabled, not ready, missing an origin node, or internally
  inconsistent. Test each fallback and assert all renderable chunks remain camera-
  visible.
- [x] Add `ChunkOcclusionCulling` to `TerrainRenderSettings`, enabled in
  `Assets/_Project/Resources/TerrainRenderSettings.asset`, with validation that does
  not alter existing resource ceilings.

## 4. Feed the existing GPU culler

- [x] Reuse one reserved word in `TerrainRenderer.ChunkGpuData` for camera visibility;
  keep the struct's 32-byte layout. Retain separate CPU graph metadata so zero-quad
  chunks remain traversal nodes.
- [x] Before each fresh frame snapshot, run or consume the graph traversal and fill
  the camera-visibility field. Expose resident, visible and culled chunk counters.
- [x] Update `Assets/_Project/Shaders/Terrain/TerrainStreaming.compute` so normal
  batches reject camera-hidden chunks before scanning their geometry. Shadow-only
  batches must ignore this bit and preserve their existing conservative behavior.
- [x] Extend targeted PlayMode coverage under
  `Assets/_Project/Tests/PlayMode/Bootstrap/TerrainSchedulerTests.cs` or a focused
  companion file. Build a complete replica interest containing a blocking chunk and
  a chunk behind it; assert visibility counters with culling enabled, fail-open while
  dirty, and recovery after rebuild/reset.
- [x] Add a shader/source contract test if direct GPU counter inspection is not
  stable in EditMode. Prefer an integrated rendered result when the existing terrain
  PlayMode harness can verify it without timing races.

## 5. Verify and document

- [x] Run the VoxelMeshing and ClientRenderingRuntime EditMode suites while iterating.
- [x] Run targeted terrain PlayMode tests and inspect structured results plus bounded
  error/warning excerpts.
- [x] Run a fresh Unity compile after the final source and shader changes.
- [x] Compare the fixture with chunk graph culling enabled and disabled. Record graph
  node, visible chunk, culled chunk, submitted quad and warmed connectivity timings;
  report these as workload measurements, not guaranteed frame-time gains.
- [x] Run `powershell -File tools/Test-LeanWorkflow.ps1` and `git diff --check`.
- [x] Inspect the final diff for hidden shadow regressions, stale graph publication,
  per-frame allocation, incorrect negative-coordinate math and accidental scope
  expansion.
- [x] Update `docs/chunk-meshing-rendering.md` with the implemented ownership, rules,
  measured evidence and explicit per-quad occlusion limitation. Update architecture
  navigation only if a new durable document is added.

## Representative acceptance contracts

```csharp
Assert.That(ChunkFaceConnectivity.Connects(empty, BlockFace.West, BlockFace.East), Is.True);
Assert.That(ChunkFaceConnectivity.Connects(sealedPlane, BlockFace.West, BlockFace.East), Is.False);
```

```csharp
graph.SetReady(true);
graph.Cull(cameraInsideOrigin);
Assert.That(graph.IsCameraVisible(frontSlot), Is.True);
Assert.That(graph.IsCameraVisible(behindSealedChunkSlot), Is.False);
```

```csharp
graph.SetReady(false);
graph.Cull(cameraInsideOrigin);
Assert.That(graph.CameraVisibleCount, Is.EqualTo(graph.RenderableCount));
```

The exact test API may change during red-green-refactor, but the observable contracts
must remain. Tests should exercise production traversal and real scheduler stale-
result behavior rather than duplicating graph logic in a test fixture.
