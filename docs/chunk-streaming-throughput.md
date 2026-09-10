# Chunk streaming throughput implementation summary

Status: implemented September 9, 2026.

This change removes serialisation from the chunk transfer stage. The pipeline previously
carried one chunk at a time per peer and would not even *request* the next chunk until the
current one had been applied and acknowledged, so the link sat idle for most of every load.

## Measured outcome

Both figures come from `ChunkStreamingThroughputTests`, which drives the real server/client
pair through a 60 Hz tick loop paced to real time.

| Neighbourhood | Before | After |
| --- | --- | --- |
| 75 chunks (h=2 v=1) | 380 ticks / 6.33 s | 48 ticks / 0.80 s |
| 245 chunks (h=3 v=2) | 1130 ticks / 18.83 s | 133 ticks / 2.22 s |

Roughly an 8x improvement, with zero idle ticks and exactly one transfer per chunk (no retries). Before the change,
65% of ticks were spent with the link idle waiting for the next snapshot to finish encoding.

Meshing was measured at 1.34 ms per chunk (mesh + visibility jobs, Burst, single worker) and
was never the bottleneck — about 2% of load time.

## What changed

### Transfer pipelining

`ChunkStreamingServer` keeps a bounded window of in-flight transfers per peer instead of one.
Snapshot encoding for later chunks now overlaps transfer of earlier ones, which is where the
idle time went. Acknowledgements still arrive per chunk and still confirm the baseline a delta
is built against; they simply no longer gate what starts next.

At most one transfer exists per lease index at a time, so the baseline a delta encodes against
can never be overtaken by another transfer of the same chunk.

### Ordering

Chunk *selection* is still strictly closest-first: the cursor scans leases in the interest's
radial order. Slice emission round-robins across the in-flight window so one large chunk cannot
monopolise the link, which means chunks within a window may complete out of order. Arrival order
is therefore radial to within the window depth rather than strictly radial. `AssertRadial` in
`ChunkStreamingTests` and `AssertRadialWithinWindow` in `ChunkStreamingThroughputTests` both
express that weaker-but-real guarantee.

### Out-of-order transfer identity

The client previously rejected any transfer id at or below a high-water mark, which was correct
only while exactly one transfer existed. With pipelining, declarations legitimately arrive out of
id order, so the client now tracks a settled floor plus the gaps above it. Abandoned transfers
leave permanent gaps, so the set collapses once it grows past four windows — never past a live
transfer.

### Budgets and transport

Per-tick byte budgets were sized for a lossy-UDP development scenario (16 KB global, 4 KB per
peer) and applied even over single-player IPC. They are now authored on
`ChunkStreamingSettings` alongside the window depth and encoder concurrency.

`BulkDriver.MaxPayloadBytes` moved from 1024 to 1200. It is the protocol ceiling, not the slice
size: the real limit is the connection's negotiated reliable-pipeline capacity, which depends on
path MTU. `BulkDriver.PayloadCapacity` exposes it and `ChunkStreamingServer` slices to it, so a
smaller path MTU degrades instead of throwing.

The reliable pipeline's `windowSize: 64` was left alone — Unity Transport's maximum is 2040, and
32 is only the default.

### Mesh invalidation

`ResidentChunkStore.PublishReplica` hashes each of the six boundary planes and reports which ones
changed. A neighbour's geometry depends only on the plane facing it, so `ChunkMeshScheduler` now
dirties a neighbour only when that plane actually moved. An absent neighbour is padded as air, so
a first arrival compares against the empty plane rather than counting as a change on every face.
This removes most of the previous behaviour where every arriving chunk re-meshed all six
neighbours during a radial load.

Fluids are excluded from the hash because the mesher reads only the solid channel.

## Main files

| File | Responsibility |
| --- | --- |
| `Networking/NetCode/Bulk/ChunkStreamingServer.cs` | In-flight window, round-robin emission, per-transfer retry |
| `Networking/NetCode/Bulk/ChunkStreamingClient.cs` | Concurrent reassembly, settled-id tracking, queued responses |
| `Networking/NetCode/Bulk/ChunkTransfer.cs` | Reassembler transfer/byte limits |
| `Networking/NetCode/Bulk/BulkDriver.cs` | Payload ceiling, negotiated capacity, queue depths |
| `Networking/NetCode/Bulk/ChunkStreamingSettings.cs` | Authored budgets, window depth, encoder concurrency |
| `Voxels/Runtime/ResidentChunkStore.cs` | Boundary-plane hashes, snapshot concurrency |
| `Client/Rendering/ChunkMeshScheduler.cs` | Face-aware neighbour invalidation |

Paths are relative to `Assets/_Project/Scripts`.

## Verification

- EditMode: 235 passed, 0 failed.
- PlayMode: 66 passed, 0 failed.
- `DigBlocksBootstrapTests` reaches `Playing` and meshes the full authored neighbourhood twice
  through the real bootstrap path.
- `ChunkStreamingThroughputTests` is the standing regression guard against returning to
  stop-and-wait: it asserts convergence within a tick budget and that no transfer was retried.

## Deferred work

- The single-player direct-delivery bypass is designed but deliberately not built now that the
  transfer stage is no longer the bottleneck. See
  [single-player-direct-delivery.md](single-player-direct-delivery.md).
- Client-side decode was indeed a problem: it cost 7.4 ms per chunk on the main thread and a whole
  window landed in one frame. Fixed in [chunk-apply-frame-cost.md](chunk-apply-frame-cost.md).
- Several tests previously pinned the authored render distances (75 chunks). They now derive the
  expected neighbourhood from `ChunkStreamingSettings`, since those distances are tuning rather
  than a contract. The authored value is currently horizontal 3 / vertical 2 (245 chunks).
