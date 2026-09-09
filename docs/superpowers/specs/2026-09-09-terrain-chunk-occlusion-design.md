# Terrain Chunk Occlusion Culling

Status: approved direction on 2026-09-09. Implement a conservative Sodium-style
chunk visibility graph. View-dependent GPU occlusion of individual packed quads is
explicitly deferred.

## Goal and scope

Reduce terrain submitted to the camera when opaque voxel structure prevents a
line of sight from the camera's chunk to other resident chunks. Each completed
chunk mesh carries a compact description of which of its six boundary faces are
connected through non-occluding cells. At render time, a graph traversal begins in
the camera's chunk and marks only reachable chunks as camera-visible.

The existing greedy mesher remains responsible for the other currently approved
form of block-face culling: a full-cube face is not emitted when its touching
neighbor is an opaque full cube, including across chunk boundaries. Greedy merging,
GPU range allocation, material batching, distance culling, frustum culling and
shadow submission remain otherwise unchanged.

This milestone does not perform screen-space or depth-buffer occlusion at packed-
quad granularity. Two walls in the same reachable chunk can both remain in its
indirect geometry list even if the nearer wall completely covers the farther wall.
Likewise, a hill does not screen-space-cull a disconnected hill merely because their
projections overlap. Hardware depth rejection still prevents hidden fragments from
shading where applicable. A future hierarchical-depth feature may address this,
but it is not part of this plan.

## Research conclusion

Sodium derives section occlusion data while building a section mesh, then traverses
neighboring render sections from the camera. Traversal through a section is allowed
only when its recorded face connectivity admits a path from an incoming boundary
to an outgoing boundary. Current Sodium adds directional and angular refinements,
multiple conservative traversal widths, asynchronous tree construction and fallback
paths. DigBlocks should adopt the durable core idea without copying that full policy
surface into its initial 256-chunk renderer.

This approach fits DigBlocks better than a custom URP hierarchical-depth pipeline
at the current scale. It is deterministic from voxel data, adds no GPU readback,
does not require a render feature or depth-pyramid lifetime, and works naturally
for caves and sealed structures. Its limitation is equally deliberate: it reasons
about traversable chunk portals, not exact projected coverage.

Primary references:

- [Sodium section meshing and occluder collection](https://github.com/CaffeineMC/sodium/blob/dev/common/src/main/java/net/caffeinemc/mods/sodium/client/render/chunk/compile/tasks/ChunkBuilderMeshingTask.java)
- [Sodium directional visibility graph](https://github.com/CaffeineMC/sodium/blob/dev/common/src/main/java/net/caffeinemc/mods/sodium/client/render/chunk/occlusion/DirectionalVisGraph.java)
- [Sodium chunk occlusion traversal](https://github.com/CaffeineMC/sodium/blob/dev/common/src/main/java/net/caffeinemc/mods/sodium/client/render/chunk/occlusion/OcclusionCuller.java)
- [Unity's warning that baked occlusion is unsuitable for runtime-generated geometry](https://docs.unity3d.com/6000.0/Documentation/Manual/OcclusionCulling.html)

## Occluder contract

A solid state blocks visibility only when its simulation attributes contain both
`BlockFlags.Opaque` and `BlockFlags.FullCube`. This is the same predicate currently
used to remove a touching neighbor face in `GreedyMesherJob`.

All other cells are passable for visibility connectivity, even if they contain
rendered geometry. In particular:

- transparent or translucent full cubes do not occlude when `Opaque` is false;
- stairs, fences, slabs and other partial models do not occlude when `FullCube` is
  false;
- invisible air is passable;
- fluids do not participate in solid chunk occlusion;
- an appearance material or render layer never implies occlusion.

Keeping this classification in the simulation attribute table prevents client
materials from silently changing visibility and agrees with the existing rule that
render material selection is appearance data. Future custom shapes may introduce
a more precise occlusion shape contract, but must fail open until such a contract
is explicitly designed.

## Connectivity representation and build

`DigBlocks.Voxels.Meshing` owns a Burst-compatible chunk visibility result. Six
faces produce a 6-by-6 directed matrix packed into the low 36 bits of a `ulong`.
Bit `incoming * 6 + outgoing` states that at least one connected component of
passable cells touches both faces. A directed layout keeps traversal operations
simple even though the initial flood-fill result is symmetric.

After the padded 34-cube input is ready, a Burst job scans the central 32-cube
volume. It flood-fills every unvisited passable component with six-axis adjacency,
records the chunk boundary faces touched by that component, and adds every pair in
that face set to the matrix. It uses fixed-capacity native scratch storage owned by
the existing mesh worker and performs no managed allocation. Empty chunks connect
every face; fully opaque chunks connect none.

The visibility build can run alongside greedy geometry generation because both are
read-only consumers of the padded input and attribute table. The worker combines
their handles before publication. Connectivity is subject to the scheduler's same
store identity, interest epoch, dirty version, neighbor-stamp and stale-result
checks as geometry; geometry and connectivity from different revisions must never
be published together.

## Runtime graph traversal

The client rendering assembly owns the view-dependent graph policy. Each published
slot retains its chunk position, mesh presence and connectivity even when the chunk
has no geometry. Empty chunks are graph nodes and must not disappear merely because
their quad count is zero.

For each camera frame where graph culling is valid:

1. Convert the camera position to a chunk coordinate using mathematical floor, so
   negative world coordinates behave correctly.
2. Find the active node containing the camera. Seed it as visible with all six
   incoming directions. Treating all components of the origin chunk as reachable is
   intentionally conservative because the renderer does not retain the exact voxel
   component containing the camera.
3. Pop a node, union the outgoing directions connected to its newly received
   incoming directions, and visit resident orthogonal neighbors. A neighbor receives
   the face opposite the direction used to enter it.
4. Requeue a node only when it receives an incoming direction not processed before.
   This permits multiple valid routes without unbounded cycles.
5. Mark reached nodes camera-visible. Unreached nodes are excluded only from camera
   batches.

Traversal operates over the complete current replica interest cuboid. It does not
treat absent data as an opaque wall. Graph culling therefore fails open and leaves
all built chunks camera-visible when any of these conditions holds:

- the replica store is absent, reset or not `DataReady`;
- any entry has a pending or stale mesh/connectivity result;
- the camera is outside the resident chunk set;
- the feature is disabled in terrain settings;
- connectivity or slot metadata is inconsistent.

`ResidentChunkStore.DataReady` means the unique resident replicas fill the current
interest volume, so traversal cannot mistake a streaming hole for terrain. Any
replica change temporarily disables graph culling until all affected results pass
the existing stale-publication checks. This favors extra rendering over visible
holes.

## Rendering integration

`TerrainRenderer` continues to upload one chunk metadata buffer per reusable frame.
Chunk metadata distinguishes three concepts:

- resident graph node;
- renderable geometry range;
- camera-visible result.

The compute culling kernel checks camera visibility before appending geometry to a
normal material batch, then applies its existing distance and frustum tests. It does
not apply camera occlusion to shadow-only batches: a camera-hidden chunk can still
cast a visible shadow. Shadow batches retain their existing conservative distance
behavior.

No GPU geometry is moved or rebuilt when camera visibility changes. The CPU changes
only the per-slot visibility field already included in the chunk metadata upload;
the existing compute pass performs final batch compaction.

Expose bounded counters useful for tests and later profiling: resident graph nodes,
camera-visible chunks and graph-culled chunks. These are diagnostics, not a promise
that fewer visible chunks always improves frame time.

## Resource and performance bounds

The graph has at most `TerrainRenderSettings.MaxChunks` nodes, currently 256. Store
position lookup, incoming masks, visibility flags and the traversal queue in reused
fixed-capacity collections. A traversal is linear in resident nodes and their six
neighbor edges. It performs no per-frame managed allocation and no GPU-to-CPU
synchronization.

Connectivity rebuild cost is paid only with mesh work after relevant chunk changes.
The flood fill is bounded by `ChunkLayout.Volume` cells. The implementation should
measure its warmed Burst cost beside the existing mesher benchmark rather than claim
an improvement from reduced submitted geometry alone.

If profiling later shows traversal every frame is material, cache results until the
camera crosses a chunk boundary or graph metadata changes. This optimization is not
required initially; correctness and allocation-free behavior are.

## Lifecycle and failure behavior

Replica reset clears all graph nodes and camera visibility immediately. Retired
mesh ranges retain their existing frame-reference lifetime. Worker disposal completes
both mesh and visibility jobs before releasing native scratch storage. Session
shutdown adds no readback or render-pipeline dependency.

Invalid content state IDs and native capacity errors remain hard failures consistent
with the current mesher. Runtime uncertainty in graph readiness is not a failure;
it disables occlusion until trustworthy data is available.

## Verification requirements

- Connectivity tests cover empty, fully opaque and uniformly solid chunks; a sealed
  plane; a plane with an opening; multiple disconnected air components; and each of
  the six boundary directions.
- Attribute tests prove `Opaque && FullCube` is required and that transparent full
  cubes, opaque partial blocks and air remain passable.
- Traversal tests cover a straight open chain, a fully opaque stopping chunk,
  alternate routes around a barrier, cycles, multiple incoming faces, negative
  camera coordinates and an origin outside the graph.
- Fail-open tests cover incomplete replica data, pending rebuilds, reset and stale
  worker results after an edit opens an occluder.
- Renderer tests prove camera batches reject graph-hidden chunks while shadow-only
  batches remain conservative.
- Existing greedy mesher tests continue proving touching opaque full-cube neighbors
  remove shared faces, including at chunk boundaries.
- Record warmed connectivity cost and an integrated scene case containing reachable
  and graph-occluded chunks. Compare visible and submitted chunk/quad counters with
  culling enabled and disabled; do not infer a frame-time win without GPU profiling.
- Run the affected EditMode suite, targeted PlayMode terrain tests, a fresh Unity
  compile, lean workflow checks and final diff inspection.

## Deferred work

- Hierarchical-Z, occlusion queries, software raster occlusion or a custom URP
  renderer feature.
- View-dependent culling of a farther quad hidden by a nearer quad inside a chunk
  that remains graph-visible.
- Sodium's angular masks, multiple-width visibility trees and asynchronous cull
  executor.
- Custom-model occlusion shapes, transparent rendering, fluids and translucent
  sorting.
- Reusing terrain visibility for entities, particles, simulation or server interest.
- Hierarchical region/octree acceleration beyond the current bounded chunk set.
