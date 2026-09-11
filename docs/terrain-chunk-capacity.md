# Terrain chunk capacity

September 10, 2026. Recorded while fixing the standalone terrain freeze; nothing here is broken today,
but the margin one of these numbers was chosen for has since been spent.

## Three different limits, only one of which binds

| Limit | Where | Value | Kind |
| --- | --- | --- | --- |
| Interest sanity ceiling | `ChunkInterest.MaximumChunks` | 32,768 | protocol guard on a decoded interest |
| Store residency | `ChunkCompanionService.Residency()` | `chunks + chunks / 2 + 16` = 5,969 | derived from the authored interest |
| Renderer slots | `TerrainRenderSettings.asset` `MaxChunks` | 4,096 | hand-authored tuning value |
| Actual client residency | cylinder r=12, +/-4 | 3,969 | 441 columns x 9 layers |

The 32,768 ceiling is deliberately not a tuning value, and its own comment says so: it bounds what a peer
may ask for, while "the real residency bound is the store's maxResidents, which is local configuration
rather than something a peer can choose". `TerrainRenderSettings.MaxChunks` carries the same 32,768 in its
`[Range(16, 32768)]` attribute, so the type permits an effectively unbounded slot count; only the authored
asset value is 4,096.

`TerrainRenderSettings.MaxChunks` is therefore a different kind of number from the other two. It sizes the
renderer's GPU chunk-slot arrays, and it is the smallest of the three, so it is the one that actually binds.

## How the margin was spent

`500bf08` raised `MaxChunks` from 256 to 4,096 while the authored interest was a 2,205-chunk cylinder, which
left 1,891 spare slots (1.86x). `fa06994`, whose subject is `docs(generation): add a worked reference world
type and the summary`, raised `VerticalRenderDistanceChunks` from 2 to 4:

```text
vertical=2   residency=2205   headroom=1891 slots (1.86x)
vertical=4   residency=3969   headroom=127 slots (1.03x)
```

An 80% increase in residency rode along inside a documentation commit, and `MaxChunks` was not revisited.

## What is and is not at risk

A moving anchor cannot transiently overflow the slots. `ResidentChunkStore.SetReplicaInterest` evicts every
chunk outside the new interest synchronously, raising `ReplicaRemoved` for each before any replacement
arrives, so the client replica store never holds more than the interest count. The store's own 50% headroom
exists for the owner store serving peers at different anchors, not for the client.

The exposure is authoring. Nothing validates the streaming radius against the renderer's slot count:
`ChunkStreamingSettings.OnValidate` only warns at 32,768, and `TerrainRenderer.Validate` only rejects a
`MaxChunks` outside `1..32768`. Neither compares the two. Raising `HorizontalRenderDistanceChunks` to 13
(4,653 chunks) or `VerticalRenderDistanceChunks` to 5 (4,851) silently exceeds 4,096 with no warning.

The only enforcement is a runtime throw in `ChunkMeshScheduler.OnChanged`:

```csharp
if (freeSlots.Count == 0) throw new InvalidOperationException("Terrain chunk capacity is smaller than admitted residency.");
```

`TerrainRenderService.Tick` catches that into `failure` and then returns early forever, so meshing stops
permanently while chunk streaming keeps running. The symptom is terrain that freezes partway through while
the world audibly keeps loading, which is close enough to the render-context completion bug fixed alongside
this note to be mistaken for it.

## Why MaxChunks cannot simply be set to 32,768

Memory is not the obstacle. At 32,768 slots the per-frame chunk buffers cost about 3 MB of GPU memory across
three frame snapshots, and the CPU-side arrays about 2 MB. Three code paths are proportional to capacity
rather than to occupancy, in increasing order of severity:

- `TerrainRenderer.DrawCamera` dispatches the cull kernel with one thread group per slot, per batch, per
  frame, whether or not the slot holds anything.
- `TerrainRenderer.UpdateCameraVisibility` walks every slot once per camera per frame.
- `ChunkOcclusionGraph.RebuildLookup` is O(capacity) and runs on **every** `SetNode` and `RemoveNode`, which
  is once per published chunk. Loading 3,969 chunks into 4,096 slots already costs roughly 16 million
  operations plus 3,969 dictionary rebuilds; at 32,768 slots the same load costs roughly 130 million.

The last of these is the reason capacity is not currently free, and it should be made incremental before
the slot count is treated as a sanity limit rather than a budget.

## Options

1. Derive `MaxChunks` from the streaming options the way `ChunkCompanionService.Residency` already does, so
   one authored render distance drives both and the two cannot drift apart again.
2. Make `ChunkOcclusionGraph.RebuildLookup` incremental, removing the per-publish O(capacity) cost and with
   it the main reason to keep the slot count small.
3. At minimum, cross-validate the authored streaming radius against `TerrainRenderSettings.MaxChunks` so the
   mismatch is an authoring-time warning rather than a fatal runtime throw.

Options 1 and 3 are alternatives; option 2 is independent and is the prerequisite for an effectively
unbounded slot count.

## References

- [Chunk meshing and terrain rendering](chunk-meshing-rendering.md)
- [Chunk loading optimization](chunk-loading-optimization.md), which authored the current distances
- [Dynamic chunk loading](dynamic-chunk-loading.md)
