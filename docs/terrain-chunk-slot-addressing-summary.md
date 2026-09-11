# Terrain chunk slot addressing implementation summary

September 11, 2026.

The terrain renderer no longer allocates chunk slots. A slot is now a pure function of chunk
position, wrapped onto a fixed box derived from the authored view distance, so the chunk entering the
streamed volume reuses the slot of the one that left. Slot exhaustion, which previously stopped
meshing permanently, is no longer representable. View distance is negotiated between client and
server so that a client is never sent more chunks than it has slots for. This change does not add
LOD, does not alter the streamed volume's shape, and does not change what is drawn at a given
distance beyond correcting an overshooting far plane.

## Why it changed

`TerrainRenderSettings.MaxChunks` was a hand-authored second copy of the view distance that
`ChunkStreamingSettings` already owned, and nothing compared them. `fa06994`, a documentation commit,
raised the vertical distance from 2 to 4 and took residency from 2,205 to 3,969 chunks against 4,096
slots without revisiting the slot count. Exhausting the free list threw; `TerrainRenderService.Tick`
caught that into `failure` and returned early forever, so terrain froze part-loaded while the world
audibly kept streaming. [Terrain chunk capacity](terrain-chunk-capacity.md) records the arithmetic.

## Implemented ownership

- `DigBlocks.Voxels` owns `ChunkSlotGrid`, a readonly struct holding a horizontal and vertical chunk
  radius. It exposes `Width`, `Height`, `Capacity` and `SlotOf(int3)`, and rejects a view distance
  needing more than `MaximumCapacity` slots on widened arithmetic. It is the only new public type, and
  it lives in `DigBlocks.Voxels` because that is the assembly both the renderer and the chunk protocol
  already reference; no new assembly edge was added.
- `ChunkOcclusionGraph` owns node state, the traversal arrays and a precomputed `6 × capacity`
  neighbour permutation. `SetNode` and `RemoveNode` take a position rather than a slot, maintain the
  resident and renderable counters incrementally, and are O(1). The per-publish `RebuildLookup`, the
  `positionToSlot` dictionary and the `consistent` flag are gone.
- `ChunkMeshScheduler` derives each entry's slot from its address. The free list, `ResetSlots` and the
  capacity throw are gone.
- `TerrainRenderer` owns the grid used to size the GPU chunk arrays, every per-frame `Frame`, the cull
  dispatch bound and the distance cull range. `Remove` consults the graph before clearing a slot.
- `TerrainRenderService` builds the grid from the view distance passed by `DigBlocksBootstrap` and
  derives the camera far plane from it.
- `ChunkStreamingServer` owns the per-peer view-distance ceiling and the negotiated default.

## Addressing contract

`SlotOf` is `floorMod(x, W) + W * (floorMod(z, W) + W * floorMod(y, H))` with `W = 2h+1` and
`H = 2v+1`, matching `ChunkLayout.Index`'s x-then-z-then-y order. A floor modulus is required: the
remainder operator would fold negative coordinates onto the same slots as positive ones.

Two properties carry the design:

- **No volume centre is tracked.** Two positions collide only when they differ by a whole side length,
  and the box is one chunk wider than the streamed diameter in every axis, so no two chunks that can
  be resident together ever share a slot, wherever the viewer stands. Nothing needs re-centring.
- **Eviction must precede admission.** `ResidentChunkStore.SetReplicaInterest` raises every
  `ReplicaRemoved` synchronously before any replacement arrives, and `ChunkMeshScheduler.OnRemoved`
  clears the slot from that event. This was previously convenient; it is now correctness-critical.

The streamed volume remains the `ChunkInterest` cylinder, inscribed in this box. Corner slots never
receive a chunk, which costs 1,656 unused slots out of 5,625 at the authored distance and has no
visual effect: the chunk horizon is still round. A denser lattice satisfying minimum distance > 2h
would cut that overhead from about 42% to about 15%, at the cost of a generated neighbour table; it
is not worth doing unless slot count becomes the binding constraint.

### The wrap seam

Traversal must not trust the neighbour permutation alone. An active neighbour slot may hold the chunk
that wrapped onto it from the opposite face of the box rather than a true neighbour — at h=12, chunk
x=24 occupies the slot x=−1 would — and stepping across that seam lights up the far side of the
world. The flood fill therefore keeps one `int3` comparison per candidate, which remains far cheaper
than the dictionary probe it replaced.

## Negotiated view distance

The client declares its configured radii in its bulk binding request, which grew from 70 to 74 bytes
(see [chunk transfer protocol](chunk-transfer-protocol.md)). The server keeps two separate bounds per
peer, and conflating them is a mistake worth naming:

- **The client's declared radii** are a hard ceiling on every interest that peer is ever given,
  including one set explicitly by server-side code, because the client's renderer has exactly that
  many slots.
- **The negotiated default**, `min(client, server)`, bounds what a peer is admitted with and what it
  may ask for.

Clamping everything to the negotiated default would make the server's own default view distance a cap
on explicit server-side interest, which is a different concern from what a client can hold. Both
clamps route through one private `SetInterest`, so no caller can bypass them.

This also fixed a pre-existing defect: a client configured below the server previously received more
chunks than its replica store allowed and threw `Interest exceeds replica capacity` on connect.

## Cost model

Removed: the O(capacity) `RebuildLookup` on every publish — O(N·capacity) to load a volume, roughly
16 million operations at the authored distance and about 3.9 billion at h=32 — and the per-face
`int3`-keyed dictionary probe in the flood fill, which ran every frame for every visited chunk.

Still proportional to capacity, both linear and job-friendly: `TerrainRenderer.DrawCamera` dispatches
one thread group per slot per batch per frame, and `UpdateCameraVisibility` walks every slot per
camera per frame. The dispatch is the first thing to address if the view distance grows substantially;
a compacted occupied-slot list with indirect dispatch, or several slots per group, would do it.

## Failure behaviour

A slot collision means residency outgrew the grid, which negotiation is meant to make unreachable.
`ChunkOcclusionGraph.SetNode` then refuses the newcomer rather than overwriting a live chunk,
`TerrainRenderer` warns once naming the position and grid, and the graph fails open: every resident
chunk renders and occlusion culling stops. Nothing throws and meshing does not stop, which is the
behaviour the removed capacity throw failed to provide.

## Configuration

`TerrainRenderSettings.MaxChunks` and `RenderDistance` were removed from the type and the authored
asset. `ChunkStreamingSettings.HorizontalRenderDistanceChunks` and `VerticalRenderDistanceChunks` are
the only authored view distance; raising either now moves the streamed radius, the renderer's slot
capacity and the draw distance together. Draw distance is `(h + 1) × ChunkLayout.Edge`, which is 416 m
at the authored distance, replacing a 512 m constant that sat beyond any chunk that could exist.

`ChunkSlotGrid`'s default value is a valid grid of radius zero and capacity one. An unassigned field
therefore compiles and runs, presenting only as a blank frame; this occurred once during development.

## Verification evidence

Full PlayMode suite after the change: 461 tests, 457 passed, 0 failed, 4 skipped. The four skips are
`ChunkPipelineProfileTests`, which is `[Explicit]` by design. The baseline before the change was 450
tests, so the eleven new tests are `ChunkSlotGridTests` and the net additions to
`ChunkOcclusionGraphTests`.

New behavioural coverage: collision-freedom across several placements of the volume, slot reuse when
the anchor steps by one chunk, floor-modulus wrapping of negative coordinates, traversal refusing the
wrap seam, aliasing rejection and fail-open, removal of a chunk that is not the slot's holder, and
`min(client, server)` negotiation surviving both an anchor move and an explicit server-side interest.

`TerrainSchedulerTests`, `TerrainSecondaryCameraTests` and `DigBlocksBootstrapTests` are the
integration contracts and remain green; the last of these caught the development defect noted above.

## References

- [Terrain chunk capacity](terrain-chunk-capacity.md)
- [Chunk meshing and terrain rendering](chunk-meshing-rendering.md)
- [Terrain chunk occlusion implementation summary](terrain-chunk-occlusion-summary.md)
- [Independent chunk transfer protocol](chunk-transfer-protocol.md)
