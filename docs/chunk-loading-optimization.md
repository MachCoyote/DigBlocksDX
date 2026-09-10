# Chunk loading optimization implementation summary

Status: implemented September 10, 2026. Acts on
[chunk-loading-gap-analysis.md](chunk-loading-gap-analysis.md), which holds the measurements that
motivated it, and follows the plan in
[superpowers/plans/2026-09-09-chunk-loading-optimization.md](superpowers/plans/2026-09-09-chunk-loading-optimization.md).

Supersedes the "what now caps load speed" section of
[chunk-streaming-throughput.md](chunk-streaming-throughput.md) and the two deferred options recorded
in [chunk-apply-frame-cost.md](chunk-apply-frame-cost.md).

## Measured outcome

Both rows are the authored settings of their day, measured by `ChunkPipelineProfileTests` driving the
real server and client pair through a 60 Hz loop paced to real time, over layered terrain. Editor,
native collection safety checks on.

| | Before | After |
| --- | --- | --- |
| Interest | 7x7x5 box, 245 chunks | 25-wide cylinder, **2,205 chunks** |
| Horizon | 96-block radius (~render distance 6) | **384-block radius (~render distance 24)** |
| Load time | 2.12 s | **1.18 s** |
| Rate | 1.93 chunks/tick, 116 chunks/s | **31.06 chunks/tick, 1,863 chunks/s** |
| Server tick | 3.13 ms mean | 3.40 ms mean |
| Client tick | 1.94 ms mean | 3.13 ms mean, 6.2 ms worst |
| Residency ceiling | 256 chunks | 32,768 chunks |

Sixteen times the chunks per second, nine times the chunks, and both ticks still a small fraction of
a 16.6 ms frame.

### Per chunk

12.5 KB payload, five-entry solid palette, uniform fluid channel.

| Stage | Before | After |
| --- | --- | --- |
| worldgen, generate and adopt | 3.23 ms, server main thread | 0.77 ms, and on a worker |
| `EncodeSnapshot` | 0.650 ms, worker | **0.009 ms**, inline |
| `DecodeSnapshotInto` | 0.190 ms | **0.026 ms** |
| `PublishReplica` | 0.727 ms | **0.099 ms** |
| client total | 0.917 ms | **0.125 ms** |

### Where the budgets bind now

Ticks to converge the authored 2,205-chunk neighbourhood:

| `PeerWindow` \ `AppliesPerTick` | 2 | 8 | 16 | 32 |
| --- | --- | --- | --- | --- |
| **16** | 1104 | 277 | 207 | 207 |
| **64** (authored) | 1104 | 277 | 139 | **71** |

Before this work the same table was flat at roughly two chunks a tick everywhere, because window
depth and apply budget were the same constraint. They are separate now: the apply budget sets the
rate and the window only has to be deep enough not to get in its way.

## What changed

### Interest is a cylinder, and the ceiling is gone

Horizontal extent is a true radius, so the corners of the enclosing square — which are further away
than the render distance claims — are never streamed. That is 29% fewer chunks at any given visible
distance. Vertical extent is a straight offset from every column, so the cylinder does not taper.

`ChunkInterest.MaximumChunks` went from 256 to 32,768 as a sanity ceiling on a decoded interest
rather than a tuning value; the real bound is the store's `maxResidents`, which is local
configuration a peer cannot choose, and which now derives from the authored interest.

Radial order depends only on the shape, so each shape is enumerated and sorted once and later
interest changes are a translation. Interest changes whenever the player crosses a chunk boundary,
and re-sorting thousands of addresses on each of those would be a hitch.

### The wire format carries the storage layout

This is the Minecraft lesson from the analysis: keep one delivery path and make the codec free,
rather than forking the path. Storage held one, two or four whole bytes per cell regardless of how
small the palette was, while the wire had always packed to the palette's real width, so every chunk
was re-packed on arrival and expanded on departure.

`PaletteChannel` now packs at `BitsPerEntry`, non-spanning, so entries never straddle a word and
reading one is a shift and a mask. Codec 1 carries bits-per-entry, the palette and the raw words, so
encode and decode are a palette copy and a memcpy. A typical terrain chunk went from 32 KiB per
channel to about 12 KiB, which is what makes thousands of resident chunks affordable at all.

Validation got cheaper without getting weaker: state ids are checked against a palette of a few
entries rather than all 32,768 cells, and the cell walk that remains only confirms every entry
indexes inside the palette and that a word's padding bits are zero — which is what stops a Burst job
reading past the palette. The checksum is slice-by-eight, because walking 12 KB one byte at a time
would have become the dominant cost once everything around it was a copy.

The price, as recorded when the option was first written down: storage layout and protocol now move
together. The codec byte in the header is what a future layout change versions against.

### Encoding happens inline

At nine microseconds, the asynchronous encoder was hiding a cost that no longer exists while adding
about three ticks of latency to every chunk — and throughput is window depth divided by round trip.
The ThreadPool worker, pending list, managed buffer pool and request/pump/take handshake are gone.
The profiler now shows zero payloads in flight on a typical tick, where two used to sit encoded and
waiting.

This gives up the deliberate invariant that the encode worker touched no native chunk memory.

### Window and apply budget are separate

A transfer releases its payload the moment its last slice goes out and then holds nothing but a
small record while it waits to be applied, so the window can be deep for free while
`MaxBufferedPayloads` still bounds the memory. `PeerWindow` reaches 1024 and is authored at 64. The
payload counters are maintained where a payload is attached or released rather than being nested
walks of every peer's transfers, which a deep window would have made quadratic.

### Worldgen runs on workers

`IAuthoritativeChunkSource` fills one value per cell into buffers it is handed, on a worker thread,
and packs the result there too. The store adopts it by replacing both channels outright rather than
replaying edits through the per-cell mutation path. Chunks still generating are skipped rather than
waited on; the interest cursor comes back round to them once the pump has adopted the result, which
preserves closest-first selection without blocking on the slowest chunk.

Concurrent generations default to 32. Each holds about 640 KiB of scratch and packed result, so this
is the memory-for-latency knob: at eight, the load rate capped at eight chunks a tick regardless of
what else was allowed.

### Meshing reads packed storage in place

Meshing a chunk used to copy seven whole chunks into uint-per-cell buffers and then read one chunk
and six boundary planes out of them — 229,376 cells expanded to reach 38,912, with 917 KiB of
scratch per worker. The padding job now reads each source chunk's packed cells directly, so the
expansion and the buffers are gone and the only materialised working set is the padded region the
mesher actually consumes.

Two things had to be fixed to make views shareable, both worth remembering. `NativeList.AsArray`
hands out a view onto the list's current buffer and taking one again invalidates the last, which is
fatal when neighbouring chunks share sources and several mesh workers read the same chunk at once;
the view is taken once per structural change and handed out from there. And a channel with no cells
to store was backed by an empty list, which yields an array backed by nothing that a job cannot be
given even if it never reads it.

### Mesh scheduling and upload

Picking the next chunk to mesh was a linear scan of every resident chunk, once per free worker per
tick. Chunks waiting now sit in buckets keyed on distance from the camera's chunk, so picking is a
walk of a few buckets and leaving is a swap remove, and the queue only rebuilds when the camera
crosses a chunk boundary. `IsCurrent` was another full scan every tick and is two counters.

Publishing needed a staging buffer whose fence had passed, each sized for the largest mesh
imaginable at 2.36 MiB, which is why there were only two — capping publishing near two chunks a
frame however many mesh workers were running. Staging is now a ring of per-frame buffers that chunks
are appended into, so how many can be published in a frame is a question of bytes, the budget that
already existed.

### Single-player direct delivery

Implemented as designed in [single-player-direct-delivery.md](single-player-direct-delivery.md):
optional, read per chunk, and **off by default**. Only the payload hop changes; interest, leases,
baselines, retry and residency are untouched, and the interest declaration still goes over the wire.
Toggled from the debug menu with F4.

It is worth much less than when it was proposed, because encoding is now nine microseconds rather
than 0.65 ms. It saves the copy and the slicing, not the codec.

## Authored settings

| Setting | Was | Now |
| --- | --- | --- |
| `HorizontalRenderDistanceChunks` | 3 | 12 |
| `VerticalRenderDistanceChunks` | 2 | 2 |
| `PeerWindow` | 8 | 64 |
| `AppliesPerTick` | 2 | 32 |
| `MaxBufferedPayloads` | 64 | 128 |
| `GlobalBytesPerTick` / `PeerBytesPerTick` | 256 KiB / 128 KiB | 2 MiB / 1 MiB |
| `TerrainRenderSettings.MaxChunks` | 256 | 4096 |
| `TerrainRenderSettings.MeshWorkers` | 6 | 8 |
| `TerrainRenderSettings.QuadCapacity` | 1,048,576 | 2,097,152 |
| `TerrainRenderSettings.UploadSlots` | 2 slots | 3 ring frames |
| `BulkDriver` reliable window | 64 | 256 |
| `BulkDriver` messages per connection per update | 64 | 512 |

## Verification

- EditMode 240 passed, PlayMode 67 passed.
- `ChunkStreamingThroughputTests` remains the standing guard against returning to stop-and-wait.
- `ChunkCompanionTests.DirectDeliveryBypassesTheCodecAndTheSwitchTakesEffectPerChunk` covers both
  positions of the toggle and that both routes land the same data.
- `PaletteChannelViewTests` guards the two view invariants the meshing change depends on.
- `ChunkPipelineProfileTests` is explicit; run it by name to re-measure.

## What is still ahead

Stated as open, not done.

- **Meshing is the next wall, and it is not measured.** A mesh worker picks up one chunk per tick, so
  the rate at which chunks become *visible* is bounded near `MeshWorkers` per tick — about 480
  chunks/s at eight workers, against the 1,863 chunks/s the loader now delivers. Data lands far
  faster than geometry follows. Measuring that, and completing more than one chunk per worker per
  tick, is the next piece of work.
- **`QuadCapacity` is sized for the fixture**, whose content only exists within a 6-chunk radius.
  Real terrain filling the full 12-chunk radius will need considerably more, and the per-camera
  visible buffers are sized off the same number — ten of them at four bytes a quad — so that sizing
  wants splitting into a separate visible capacity before the world fills out.
- **Level of detail is not addressed.** GPU geometry and fill time still grow with the square of the
  radius, which is what caps the horizon well before the 32,768-chunk ceiling does.
- The fixture chunk source is still sample content, not the world-generation subsystem.
