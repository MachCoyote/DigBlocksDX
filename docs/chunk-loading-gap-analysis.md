# Chunk loading: where the pipeline falls short

Status: analysis and proposal, September 9, 2026. **Acted on September 10** — see
[chunk-loading-optimization.md](chunk-loading-optimization.md) for what was built and what it
measured. The measurements below are the "before" state. Companion to
[chunk-streaming-throughput.md](chunk-streaming-throughput.md) and
[chunk-apply-frame-cost.md](chunk-apply-frame-cost.md), both of which this supersedes on the
question of what currently caps load speed.

Prompted by a comparison against Minecraft running Sodium, Lithium, ModernFix, Moonrise and
Nvidium, which fills a 24-chunk horizon in a few seconds while this loader struggles to keep up
with a flying camera.

## Summary

Three separate things are true and only the first was known.

1. The authored streaming settings sit in the worst corner of their own parameter space. Retuning
   two numbers alone takes a 245-chunk load from 2.12 s to 0.35 s. No code change.
2. The pipeline cannot exceed 256 chunks at all. `ChunkInterest.MaximumChunks` is 256 and the
   current interest is 245, so the maximum reachable horizon is a 96-block radius, roughly
   Minecraft render distance 6. Distance is a harder wall than speed.
3. `chunk-streaming-throughput.md`'s recorded hypothesis — that in-flight window depth against
   snapshot-encode latency is the cap — is wrong. The encoder is never the constraint at the
   authored settings. Measurements below.

## What was measured

`Assets/_Project/Tests/EditMode/NetCodeStreaming/ChunkPipelineProfileTests.cs` (present in the
worktree, marked `[Explicit]`, not committed). It drives the real `ChunkStreamingServer` /
`ChunkStreamingClient` pair through a 60 Hz loop paced to real time, over the same layered terrain
`ChunkStreamingThroughputTests` uses. Editor, native collection safety checks on.

### Per-chunk stage costs

One representative chunk: 12,365-byte payload, 5-entry solid palette, uniform fluid channel.

| Stage | Cost | Thread |
| --- | --- | --- |
| worldgen + `ChunkData.Apply` | **5.09 ms** | server main |
| `ChunkWireCodec.EncodeSnapshot` | 0.650 ms | server worker |
| `ChunkWireCodec.DecodeSnapshotInto` | 0.190 ms | client main |
| `ResidentChunkStore.PublishReplica` | 0.727 ms | client main |
| &nbsp;&nbsp;of which the 32,768-cell validation loop | 0.153 ms | |
| &nbsp;&nbsp;of which `ChunkData.FromChannels` | 0.442 ms | |
| **client total per chunk** | **0.917 ms** | client main |

### Pipeline, as authored

`PeerWindow` 8, `AppliesPerTick` 2, `SnapshotWorkers` 16, h=3 v=2:

```
245/245 in 127 ticks (2.12 s) | 1.93 chunks/tick | idle 4 | encoder pegged 0 | window full 4
  t 10  applied 2  total  14  inflight 6  encodes 6  ready 2
  t 60  applied 2  total 114  inflight 6  encodes 6  ready 2
  t110  applied 2  total 214  inflight 6  encodes 6  ready 2
```

`applied 2` on every tick, and `ready 2` — two fully encoded payloads sitting on the server every
tick with nowhere to go. The encoder is pegged on **zero** ticks of 127. The system is entirely
apply-budget bound, and 127 x 2 = 254 is the whole explanation for the load time.

### Parameter sweep

Same 245 chunks. Ticks to converge:

| `PeerWindow` \ `AppliesPerTick` | 2 | 4 | 8 | 16 |
| --- | --- | --- | --- | --- |
| **8** (authored) | 128 | 123 | 118 | 122 |
| **16** | 128 | 66 | 62 | 58 |
| **32** | 125 | 65 | **34** | **28** |
| **64** | 124 | 64 | 35 | **21** |

The rule is `chunks/tick ~= min(AppliesPerTick, PeerWindow / 4)`. The 4 is the round trip in
ticks: request, capture job, pump, worker encode, pump, send, receive, queue, apply, acknowledge,
retire. **The authored pair is the one corner where neither knob does anything**, because a window
of 8 pins the rate at 2 whatever the apply budget is, and an apply budget of 2 pins it at 2
whatever the window is.

With every budget opened up: 245 chunks in **15 ticks (0.25 s)**, 16.3 chunks/tick. Client tick
mean 15.2 ms, worst 48.3 ms — too lumpy to ship as-is, but it establishes that the ceiling the
current stage costs allow is roughly 8x what is being used.

## Why the window and the apply budget are coupled

They should not be. A transfer leaves the server's in-flight window only when its acknowledgement
arrives, and the client only sends that acknowledgement after the chunk has been decoded and
published. So the window is doing two jobs at once: wire flow control, and backpressure on the
client's frame budget. That coupling is what produces the diagonal in the table — raising either
number alone is wasted.

`chunk-apply-frame-cost.md` describes this as "natural backpressure", which it is; the problem is
that the recycle latency is a full round trip, so window depth divided by round trip is a hard
ceiling on throughput that has nothing to do with what the client could actually afford.

## Structural ceilings found by reading

Beyond tuning, these are the walls that stop the loader reaching a Minecraft-scale horizon.

| Ceiling | Where | Effect |
| --- | --- | --- |
| 256 chunks, hard | `ChunkInterest.MaximumChunks` | h=3 v=2 (245) is the largest legal interest |
| 256 residents | `ChunkWorldSystem.Configure` default | matches the above |
| 256 render slots | `TerrainRenderSettings.MaxChunks` | matches the above (range allows 4096) |
| ~2 mesh publishes per frame | `TerrainRenderSettings.UploadSlots` = 2 | throttles the 6 mesh workers to ~120 chunks/s |
| O(entries x workers) per tick | `ChunkMeshScheduler.Tick` candidate scan | linear scan for the best chunk, once per free worker |
| O(entries) per tick | `ChunkMeshScheduler.IsCurrent` | called every tick through `SetGraphReady` |
| 6x mesh input copy | `ChunkMeshScheduler.Schedule` | copies 7 full chunks (229,376 cells) to read 1 chunk + 6 faces (38,912) |
| worldgen on the server main thread | `ResidentChunkStore.EnsureLoaded` | 5.09 ms/chunk on *fixture* terrain, synchronous inside `StartTransfers` |
| O(window^2) rescans | `ChunkStreamingServer.PayloadCount`, `Prepare` | nested peer x transfer loops per scheduling iteration; `Prepare`'s is only for a stat |
| 64 KB resident per chunk | `PaletteChannel` byte-per-cell storage | 2,500 chunks would cost 160 MB of voxel storage alone |

The two `UploadSlots` each hold a `GraphicsBuffer` sized for `GreedyMesherJob.MaximumQuads`
(196,608 quads x 12 B = 2.36 MB), which is why there are only two. Publish is gated on that slot's
GPU fence having passed, so a chunk that meshes cannot land until a slot frees — the mesh worker
stays blocked on it in the meantime.

## Does Minecraft bypass encode/decode in single player?

Partly, and the part it does not bypass is the interesting one.

**The packet envelope is bypassed.** Minecraft's `Connection` has two distinct pipeline setups:
`configureSerialization(ChannelPipeline, PacketFlow, BandwidthDebugMonitor)` for real sockets, and
`configureInMemoryPipeline(ChannelPipeline, PacketFlow)` for the Netty `LocalChannel` that
single-player uses. The in-memory pipeline carries `Packet` *objects*; no encoder or decoder is
installed. There is a private `configureInMemoryPacketValidation` that adds a serialize/deserialize
round trip, but only under the packet-validation debug flag — it exists precisely to catch the
serialization bugs single-player would otherwise never exercise.

**The chunk payload is not bypassed.** `ClientboundLevelChunkPacketData`'s constructor eagerly
writes every section into a `byte[]`, and the client reads that buffer back. Single-player pays
chunk serialize and deserialize in full.

**It is nearly free, because the wire format is the storage format.** A section on the wire is
block count, fluid count, then a paletted container: bits-per-entry, palette, and a data array the
protocol documentation describes as raw longs matching the server's internal format, tightly
packed, least significant bits first. Encoding is `writeLongArray(storage.getRaw())`. Decoding is
`readLongArray` straight into the client's `BitStorage`. Both ends are a memcpy plus a palette of a
few dozen entries.

That is exactly the "wire format carrying the storage layout" option
[chunk-apply-frame-cost.md](chunk-apply-frame-cost.md) recorded as available but unscheduled.

The conclusion runs against the direct-delivery idea rather than for it: **Minecraft keeps one
delivery path and makes the codec free, rather than forking the path.** See
[single-player-direct-delivery.md](single-player-direct-delivery.md) — the case for deferring it is
now stronger, not weaker. Encode is 0.650 ms on a worker thread and pegged on zero ticks at the
authored settings; there is nothing there to reclaim, and `docs/architecture.md`'s reason for
routing single player through the multiplayer path is unchanged.

## What Minecraft's mod stack actually contributes

Worth separating, because only some of it is about loading.

- **Moonrise** (Spottedleaf; the same chunk system that ships in Paper) is the one that matters for
  load speed. It replaces the chunk system with a multi-threaded one: separate worker and I/O
  pools for generation, lighting and disk, with priority driven by player position. The main thread
  does the final handoff and little else.
- **Sodium** builds chunk meshes on a worker pool and batches geometry into regions rather than
  per-chunk draws.
- **Nvidium** organises terrain into 8x4x8-section regions and drives culling and geometry on the
  GPU with mesh shaders. It raises the render distance ceiling and cuts CPU draw cost; it does not
  make chunks arrive faster. This project's `TerrainRenderer` already takes the same shape —
  compute-shader upload into a shared geometry buffer, plus an occlusion graph.
- **Lithium** and **ModernFix** are game-logic and startup work, not the chunk path.

One caveat on the comparison: a 24-chunk horizon filling in seconds is almost certainly
*pre-generated* terrain being read from region files and inflated on worker pools. Fresh generation
at that distance is much slower even with Moonrise. The relevant lesson is still the same one —
none of that work is on the main thread.

## Scale target

Minecraft sections are 16^3 = 4,096 cells; these chunks are 32^3 = 32,768, so one chunk is eight
sections. Render distance 24 is a 384-block radius: 49x49 = 2,401 columns, of which surface terrain
populates maybe five to eight sections each. Covering the same radius here means h=12, so 25x25 =
625 columns and, at three to four populated vertical layers, roughly **2,000 to 2,500 chunks**.

Filling that in five seconds is about **500 chunks/s**. Current measured rate is 116 chunks/s, with
a hard stop at 256 chunks.

## Proposal

Ordered so each step unblocks the next. Steps 1 and 2 are what actually move the number; 3 through
5 are what stop something else becoming the wall immediately afterwards.

### 0. Retune now (no code)

`PeerWindow` 8 -> 32, `AppliesPerTick` 2 -> 8. Measured 34 ticks instead of 127, at 5.74 ms mean
client tick. That is a real 3.7x for one asset edit, though 5.74 ms is a large share of a frame and
it is a stopgap rather than the answer. Do not raise one without the other.

### 1. Decouple the window from the apply budget

Split acknowledgement in two: a receipt that frees the transfer slot as soon as the last slice
lands, and an applied-acknowledgement that advances `peer.Baselines[index]` for delta purposes.
Window depth then governs the wire and `AppliesPerTick` governs the frame, independently, and the
diagonal in the sweep table disappears. The invariant that matters — at most one transfer per lease
index — is preserved by the receipt, not by the apply.

This is the change that makes step 2's savings spendable.

### 2. Make the codec free — the Minecraft lesson

Change `PaletteChannel` to bit-packed `ulong[]` cells using the same bits-per-entry rule
`ChunkWireCodec` already uses, and change the wire format to carry bits-per-entry, palette, and the
raw packed words. Then:

- decode becomes a palette read plus `Buffer.BlockCopy`: removes 0.190 ms, and most of the 0.442 ms
  `FromChannels` re-derives a palette and re-packs today;
- encode becomes the same in reverse: removes most of 0.650 ms;
- validation checks a palette of a few hundred entries instead of 32,768 cells: removes 0.153 ms;
- `ComputeFaceHashes` hashes packed plane words rather than expanded cells;
- `PaletteChannel.ReadView.Get`, which the mesher calls per cell, becomes a shift and mask instead
  of a per-byte loop;
- resident storage drops from 64 KB to roughly 13 KB per chunk, which is what makes step 3
  affordable at all.

Client per-chunk main-thread cost goes from 0.917 ms to something near 0.1 ms. That is what lets
`AppliesPerTick` reach 32 without eating the frame.

The price is the one already recorded: storage layout and protocol become coupled. `WriteHeader`
already emits a codec byte, currently always 0, so the extension point exists — version the layout
there and keep codec 0 decodable if a translation layer is ever wanted.

### 3. Lift the 256-chunk ceilings

`ChunkInterest.MaximumChunks`, the `maxResidents` default, and `TerrainRenderSettings.MaxChunks`.
Only worth doing after step 2's memory reduction. Along with it:

- `ChunkMeshScheduler`'s candidate scan needs to stop being linear. Bucket dirty entries by chunk
  distance and pull from the nearest non-empty bucket, re-bucketing only when the camera crosses a
  chunk boundary.
- `ChunkStreamingServer.PayloadCount` and `Prepare`'s byte tally should be maintained counters.
  `Prepare`'s nested scan exists only to keep `PeakEncodedPayloadBytes` current.
- `QuadCapacity` (1,048,576 quads, ~12.6 MB) is sized for 256 chunks at ~4,096 quads each. It needs
  re-sizing against the new residency, or per-chunk quad counts need to come down.

### 4. Get worldgen off the server main thread

`IAuthoritativeChunkSource.LoadOrGenerate` returns `CellEdit[]` synchronously, and
`ResidentChunkStore.EnsureLoaded` calls it from inside `StartTransfers` and then walks the array
cell by cell through `ChunkData.Apply`. Measured 5.09 ms per chunk on the trivial fixture; a real
generator with caves, ores and features will be an order of magnitude worse, and it lands squarely
on the tick that is supposed to be scheduling transfers.

Change the source contract to produce whole channels on a worker — ideally the packed `ulong[]`
from step 2 directly — and give the store a bulk load path mirroring `PublishReplica`'s. Keep
`CellEdit[]` for actual edits, where it is the right shape. This is the same separation Moonrise
exists to enforce in Minecraft, and it is the difference between a fixture that hides the cost and
a world that does not.

### 5. Unblock mesh publishing

Replace the two worst-case-sized `UploadSlots` with a single persistent ring staging buffer sized
in bytes, fenced per ring region, so publishes per frame are limited by bytes rather than by slot
count. And shrink the mesh input copy: `Schedule` should copy the centre chunk plus the six
boundary planes, not seven whole chunks — 38,912 cells instead of 229,376.

## Expected end state

With steps 1 and 2, per-chunk client cost near 0.1 ms supports an apply budget in the low tens
inside a few milliseconds of frame, and the window is free to be deep because it no longer waits on
applies. That is roughly 1,000 to 2,000 chunks/s in the transfer stage — comfortably past the
500 chunks/s the scale target needs — at which point steps 3, 4 and 5 decide the real number,
because residency, worldgen and mesh publishing all become the wall at once.

## Verification status

Stage costs and the sweep are measured, in the editor, with safety checks on; a player build has
more headroom. Meshing and upload ceilings in the table above are read from code and settings, not
measured — `UploadSlots`, the candidate scan and the 7-chunk copy each deserve a PlayMode
measurement before being acted on. Nothing here has been implemented.
