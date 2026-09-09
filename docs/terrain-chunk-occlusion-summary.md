# Terrain chunk occlusion implementation summary

September 9, 2026.

DigBlocks now performs conservative, Sodium-style terrain chunk graph culling. Mesh workers derive face-to-face visibility from the same revision-matched voxel snapshot used for greedy geometry. The client renderer traverses those portals from the camera's chunk and excludes unreachable chunk geometry from normal camera batches. This feature does not add a depth pyramid, GPU readback, URP renderer feature or per-quad screen-space occlusion.

## Implemented ownership

- `DigBlocks.Voxels.Meshing` owns `ChunkFaceConnectivity` and the Burst `ChunkVisibilityJob`. Six incoming and six outgoing faces occupy the low 36 bits of a `ulong`.
- `ChunkMeshScheduler` owns fixed visited, queue and result storage per worker. Visibility and geometry run after the same padding job, their handles are combined, and both pass the existing store, interest epoch, dirty version and seven-neighbor stamp checks before publication.
- `ChunkOcclusionGraph` owns fixed-capacity node and traversal arrays plus a pre-sized position lookup. Node changes rebuild the lookup; camera traversal performs no managed allocation.
- `TerrainRenderer` owns graph readiness, diagnostics and the camera-visible GPU metadata bit. Empty meshes remain graph nodes even though they have no geometry range.
- `TerrainStreaming.compute` rejects a camera-hidden chunk only for normal batches. Shadow-only batches ignore camera visibility and retain conservative distance handling.

## Connectivity and traversal contract

A solid state occludes only when it has both `BlockFlags.Opaque` and `BlockFlags.FullCube`. Air, fluids, transparent full cubes and opaque partial blocks are passable. The visibility job flood-fills the center 32-cube volume with six-axis adjacency. Every passable component contributes all directed pairs among the chunk boundaries it touches. Empty chunks connect all faces; fully opaque chunks connect none.

Traversal converts camera position to chunk coordinates with mathematical floor, including below zero. The origin is conservatively seeded with every incoming face because the exact air component containing the camera is not retained. Each reached node propagates through its recorded incoming-to-outgoing connections to resident orthogonal neighbors. A node is reprocessed only when it receives a previously unprocessed incoming face, permitting alternate routes while bounding cycles.

Graph culling fails open when the setting is disabled, the replica store is absent or incomplete, a mesh/connectivity result is pending or stale, the camera is outside the resident graph, or node metadata is inconsistent. Replica changes mark the graph unready before rebuild scheduling. Reset clears graph nodes and visibility counters immediately. Worker disposal completes the combined handle before releasing scratch memory.

## Configuration and diagnostics

`TerrainRenderSettings.ChunkOcclusionCulling` controls the feature and is enabled in the default `TerrainRenderSettings.asset`. Existing chunk, geometry, upload and frame resource ceilings are unchanged. Each mesh worker adds fixed scratch for 32,768 visited bytes, 32,768 queue integers and one connectivity result.

The renderer exposes resident graph nodes, camera-visible renderable chunks, graph-culled renderable chunks and camera-visible packed quads. The quad count is the amount made eligible by the CPU graph before GPU distance, frustum and material compaction. It is not a readback of the final indirect argument count, and the implementation deliberately adds no GPU synchronization to obtain one.

## Verification evidence

The focused meshing and rendering EditMode assemblies pass 45 tests. New coverage includes empty and opaque chunks, planes and openings on all axes, disconnected components, every boundary face, the exact occluder predicate, open chains, barriers, alternate routes, cycles, repeated incoming faces, negative camera coordinates, all fail-open cases, empty nodes, reset and zero managed allocations across repeated traversals.

Two targeted terrain PlayMode tests pass. They cover revision and neighbor invalidation, stale combined-job rejection, mesh replacement, reset, a complete 3-by-3 replica graph, a sealed barrier, fail-open during an edit and recovery after opening a tunnel. A source contract verifies that normal compute batches test camera visibility while shadow-only batches do not.

The complete fixture measured:

| Mode | Resident nodes | Camera-visible renderable chunks | Graph-culled chunks | Graph-eligible quads |
| --- | ---: | ---: | ---: | ---: |
| Enabled | 9 | 3 | 1 | 14 |
| Disabled | 9 | 4 | 0 | 20 |

Warmed Burst schedule-plus-completion timings over ten editor samples were:

| Input | Visibility | Combined mesh and visibility |
| --- | ---: | ---: |
| Uniform | 0.654 ms | 1.407 ms |
| Sealed plane | 1.260 ms | 1.292 ms |
| Checkerboard | 2.509 ms | 2.592 ms |
| Deterministic random | 2.156 ms | 3.767 ms |

These are workload and CPU/editor measurements, not evidence of a frame-time improvement. Fresh Unity compilation completed without errors. The only console warnings were two existing `FindFirstObjectByType` deprecations and an MCP transport warning. Lean workflow validation, the generated assembly-map update and `git diff --check` passed.

## Deliberate limitations and remaining validation

This milestone culls unreachable chunks, not hidden quads within a reachable chunk. A farther wall can still be submitted behind a nearer wall, and hardware depth rejection handles its covered fragments. Screen-space hierarchical depth, occlusion queries, directional/angular refinements, custom occlusion shapes and reuse for entities or simulation remain deferred.

Large-residency CPU/GPU profiling, final indirect-count capture with an external graphics profiler, integrated Vulkan world testing and remote-client visual testing remain useful follow-up validation. None requires changing the current correctness contract.

## References

- [Detailed runtime, resource and authoring guide](chunk-meshing-rendering.md)
- [Approved design](superpowers/specs/2026-09-09-terrain-chunk-occlusion-design.md)
- [Implementation plan](superpowers/plans/2026-09-09-terrain-chunk-occlusion.md)
- [Original meshing milestone](meshing-milestone-summary.md)
