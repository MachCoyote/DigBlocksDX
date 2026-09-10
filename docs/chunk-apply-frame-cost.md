# Chunk apply frame cost implementation summary

Status: implemented September 9, 2026. Companion to
[chunk-streaming-throughput.md](chunk-streaming-throughput.md), which made loading fast; this
made it smooth.

## Problem

Loading was quick but visibly stuttery, with a hitch as each group of chunks appeared. Steady
framerate once loaded was fine, which ruled out the renderer.

Measured per chunk, all on the main thread:

| Stage | Cost |
| --- | --- |
| `ChunkWireCodec.DecodeSnapshot` | 0.56 ms |
| `ChunkData.FromChannels` | 6.16 ms |
| `PublishReplica` validation pass | 0.16 ms |
| `CopySolids` / `CopyFluids` | 0.35 ms |
| Total decode + publish | 7.37 ms |

A frame at 60 Hz is 16.6 ms, and pipelining means a whole in-flight window lands together, so
eight chunks cost about 59 ms in one frame.

`FromChannels` was 84% of it, and the cause was structural rather than algorithmic. The wire
format is already palette-packed; it was expanded into a flat `uint[32768]` and then
re-compressed one cell at a time through the generic mutation API. `PaletteChannel.Set` ran
65,536 times per chunk, and each call began by reading the cell back, which constructed two
native array views, then probed a hash map for the palette entry, then wrote the entry a byte
at a time. Incremental palette growth could also rewrite the whole channel mid-build when it
crossed a storage-width threshold.

The renderer was checked and cleared: meshing is jobified across workers at 1.34 ms per chunk,
uploads are budgeted and fenced, and the only `WaitForCompletion` is in `Dispose`.

## What changed

**Bulk channel construction.** `PaletteChannel.FromValues` collects the palette in one pass,
picks the final cell width up front, and fills the cells with a single copy. No per-cell read
back, no per-cell view construction, no mid-build promotions.

**Per-tick apply budget.** A completed transfer is queued rather than decoded where its last
slice lands; `ChunkStreamingClient.Tick` applies at most `AppliesPerTick` chunks. The queue is
bounded by the server's in-flight window, because a transfer only leaves that window once its
acknowledgement returns, which now happens after the chunk is applied. That makes the apply
budget natural backpressure on the transfer stage.

**No redundant copies.** `ChunkImage` exposes `Solids`/`Fluids` as read-only spans for consumers
that neither retain nor mutate, and `ChunkImage.FromOwnedChannels` wraps buffers the caller just
built instead of cloning them. Decoding a chunk previously allocated two 128 KiB arrays and then
immediately cloned both; publishing cloned them a third time.

## Measured outcome

| Metric | Before | After |
| --- | --- | --- |
| `FromChannels` | 6.16 ms | 1.20 ms |
| decode + publish per chunk | 7.37 ms | 1.99 ms |
| Mean client tick over a 245-chunk load | — | 2.09 ms |
| Worst client tick with no GC | ~59 ms | under 8 ms |

`AppliesPerTick` was swept over a full 245-chunk load. A budget of 2 converges in the same 125
ticks as 3 or 4 while doing the least work per tick, so it is the default. A budget of 1 halves
the peak load rate (249 ticks) for a smaller gain, and higher budgets only make frames lumpier
without loading faster, because the transfer stage is the limit at that point.

## Remaining cost: garbage collection

Every remaining slow tick coincides with a gen2 collection. Ticks without one stay under 8 ms;
ticks with one run 10–22 ms. The churn is inherent to `DecodeSnapshot` returning two
`uint[32768]` buffers per chunk, each 128 KiB and therefore allocated on the large object heap.

Worth noting for whatever is done next: moving decode to a worker thread would not fix this.
The collection is stop-the-world, so it stalls the main thread wherever the allocation happened.
Removing the intermediate buffers, or pooling them, is what removes the spike.

## Main files

| File | Responsibility |
| --- | --- |
| `Voxels/PaletteChannel.cs` | Bulk palette construction |
| `Voxels/ChunkData.cs` | Span-based channel load |
| `Networking/Chunks/ChunkImage.cs` | Span views and owned-buffer construction |
| `Networking/NetCode/Bulk/ChunkStreamingClient.cs` | Apply queue and per-tick budget |
| `Networking/NetCode/Bulk/ChunkStreamingSettings.cs` | Authored apply budget |

Paths are relative to `Assets/_Project/Scripts`.

## Behaviour changes

- A payload that disagrees with its declaration is now rejected when the client decodes it
  rather than as its last slice arrives, so the connection fails a tick later. The client tick is
  wrapped in the same protocol-failure handling that receiving already had.
- `ChunkData.FromChannels` takes `ReadOnlySpan<uint>` instead of `uint[]`. Array arguments still
  bind, and neither span is retained.

## Verification

- EditMode: 236 passed, 0 failed. PlayMode: 66 passed, 0 failed.
- `ChunkDataTests.BulkLoadedChannelsReadBackAndStayMutableAtEveryStorageWidth` covers all four
  storage classes and the write that pushes a bulk-loaded palette past the width the load chose,
  which is the case the new construction path could plausibly break.

## Deferred work

- Moving decode and channel construction off the main thread. Now worth less than it looked:
  it removes about 2 ms of main-thread work per chunk but not the GC pauses.
- Making the wire format carry the storage layout directly, so applying a chunk is a palette read
  plus a memcpy. This removes the intermediate buffers entirely and with them the GC spikes, which
  makes it the stronger of the two remaining options for smoothness.
