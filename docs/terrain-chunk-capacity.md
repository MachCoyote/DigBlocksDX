# Terrain chunk capacity

September 11, 2026. Chunk slot capacity and draw distance are both derived from one authored view
distance. This note records what that means, and why the arrangement it replaced went wrong, because
the failure was quiet and the shape of it is easy to reintroduce.

## One authored number

`ChunkStreamingSettings.HorizontalRenderDistanceChunks` and `VerticalRenderDistanceChunks` are the
only authored view distance. Everything sizes itself from them:

| Derived from it | Where |
| --- | --- |
| Interest cylinder | `ChunkInterest.CountFor` |
| Store residency | `ChunkCompanionService.Residency()` = `chunks + chunks / 2 + 16` |
| Renderer slot capacity | `ChunkSlotGrid.Capacity` = `(2h+1)² × (2v+1)` |
| Draw distance and far plane | `(h + 1) × ChunkLayout.Edge` |

The store keeps 50% headroom because the owner store serves peers at different anchors. The renderer
needs none: `ResidentChunkStore.SetReplicaInterest` evicts every departing chunk before any
replacement arrives, so the client's slot occupancy never exceeds its interest count.

`ChunkInterest.MaximumChunks` (32,768) stays what it always was — a sanity ceiling on a decoded
interest from an untrusted peer, not a tuning value. It bounds what a peer may *ask* for. Do not
treat it as a capacity budget.

## Slots are positions, not allocations

`ChunkSlotGrid` wraps a chunk position onto a fixed box, the way a clipmap addresses a moving window:

```csharp
slot = floorMod(x, W) + W * (floorMod(z, W) + W * floorMod(y, H))
```

A slot is a pure function of position, so a chunk keeps its slot as the viewer moves, and the chunk
entering the volume reuses the slot of the one that left. There is no free list and nothing that can
be exhausted. Two properties are worth stating plainly because code depends on both:

* **No centre is tracked.** Two positions collide only if they differ by a whole box width, and the
  box is one chunk wider than the streamed diameter, so no two chunks that can be resident together
  ever share a slot — wherever the volume happens to sit.
* **Eviction must precede admission.** `SetReplicaInterest` already guaranteed this. It is now
  load-bearing rather than merely convenient.

The streamed volume is a cylinder inscribed in this box, so the corner slots stay empty: 5,625 slots
hold 3,969 chunks at the authored distance. That ~42% is the price of not allocating, and it falls
only on linear per-frame sweeps, not on anything that scales with chunk arrivals. A denser lattice
(minimum distance > 2h, near-hexagonal) would cut the overhead to about 15%, at the cost of a
generated neighbour table and arithmetic nobody can read at a glance. Not worth it unless slot count
becomes the binding constraint.

## What this replaced, and why it failed quietly

`TerrainRenderSettings.MaxChunks` was hand-authored at 4,096 — a second, independent copy of the view
distance. `500bf08` set it while residency was 2,205 chunks, leaving 1.86x headroom. `fa06994`, whose
subject is `docs(generation): add a worked reference world type and the summary`, raised
`VerticalRenderDistanceChunks` from 2 to 4:

```text
vertical=2   residency=2205   headroom=1891 slots (1.86x)
vertical=4   residency=3969   headroom=127 slots (1.03x)
```

An 80% increase in residency rode along inside a documentation commit, and nothing compared the two
numbers. `ChunkStreamingSettings.OnValidate` only warned at 32,768; `TerrainRenderer.Validate` only
rejected a `MaxChunks` outside `1..32768`.

The one enforcement was a runtime throw when the free list emptied. `TerrainRenderService.Tick`
caught it into `failure` and then returned early forever, so meshing stopped permanently while chunk
streaming kept running — terrain frozen part-loaded while the world audibly kept loading.

## Cost model

Three paths used to scale with capacity rather than occupancy. The worst is gone:

* `ChunkOcclusionGraph.RebuildLookup` was O(capacity) on **every** publish — O(N·capacity) to load a
  volume, about 16M operations at the authored distance and ~3.9 billion at h=32. Slots are now
  derived, so it does not exist.
* The occlusion flood fill did an `int3`-keyed dictionary probe per face per visited chunk, every
  frame. Neighbour slots are a fixed permutation, precomputed once into a `6 × capacity` table.
* `TerrainRenderer.DrawCamera` still dispatches one thread group per slot per batch per frame, and
  `UpdateCameraVisibility` still walks every slot per camera per frame. Both are linear and
  Burst/job-friendly. The dispatch is the one to fix first if the view distance grows a lot: use a
  compacted occupied-slot list with indirect dispatch, or handle several slots per group.

### The wrap seam

Traversal must not trust the neighbour table alone. An active neighbour slot may hold the chunk that
wrapped onto it from the opposite face of the box rather than a true neighbour — at h=12, chunk
x=24 occupies the slot x=−1 would. Stepping across that seam lights up the far side of the world.
The fill therefore keeps one `int3` comparison per candidate, which is still far cheaper than the
hash probe it replaced. `ChunkOcclusionGraphTests.TraversalDoesNotStepAcrossTheWrapSeamToADistantChunk`
pins this.

## When residency outgrows the grid

It should not be able to: view distance is negotiated, so a client never receives more than it
configured (see [network session foundation](network-session-foundation.md)). If it happens anyway,
`ChunkOcclusionGraph.SetNode` refuses the newcomer rather than overwriting a live chunk, warns once,
and fails open — every resident chunk renders and occlusion culling stops. Degraded, loud, and not
fatal. Nothing throws, and meshing does not stop.

## References

- [Chunk meshing and terrain rendering](chunk-meshing-rendering.md)
- [Chunk loading optimization](chunk-loading-optimization.md), which authored the current distances
- [Dynamic chunk loading](dynamic-chunk-loading.md)
- [Network session foundation](network-session-foundation.md) for view distance negotiation
