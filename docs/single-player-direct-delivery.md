# Single-player direct chunk delivery

Status: designed September 9, 2026, **implemented September 10** as the optional toggle this document
argued for. Off by default; F4 in the debug menu. See
[chunk-loading-optimization.md](chunk-loading-optimization.md).

The reasoning below is left as written, because it is still why the wire path is the default. One
figure has moved: encoding a chunk now costs nine microseconds rather than 0.65 ms, so direct
delivery saves the copy and the slicing rather than saving the codec, and is worth less than when it
was proposed.

## Idea

In single-player the client and server ECS worlds live in one process, so chunk data is
encoded to bytes, sliced, pushed through an IPC socket, reassembled and decoded — only to end
up in a store a few metres away in memory. Direct delivery would skip the middle: hand the
`ChunkImage` straight from the server store to the client store, behind a debug-menu toggle so
it can be switched against the wire path at runtime and compared.

## Sketch

The point of the design is to keep one scheduler, not to fork the pipeline. All interest,
lease, baseline, retry and acknowledgement logic would stay exactly as it is; only the payload
hop changes.

- `ChunkStreamingServer` gains an optional delivery delegate and a `Func<bool>` reading the
  toggle. It is consulted per chunk in `StartTransfers`, so flipping the toggle mid-session
  changes how the *next* chunk arrives rather than requiring a restart.
- When direct delivery is on, the server reads the resident chunk synchronously, hands the
  image over, and advances `peer.Baselines[index]` itself instead of creating a transfer.
  Deltas keep working because baselines still advance.
- The interest declaration still goes over the wire — it is tiny, and the client's replica
  interest epoch must already be set for `PublishReplica` to accept anything. A delivery that
  arrives before the client has processed the declaration is simply retried next tick.
- The bridge itself has to live in `ChunkCompanionService`, because that is the only place
  holding both `serverStore` and `clientStore`. The server's `BulkCompanionEndpoint` knows only
  its own world.

Only valid when `session.Role == ClientAndServer`.

## Why it is deferred

The transfer stage was the bottleneck when this was proposed. It no longer is. After
pipelining ([chunk-streaming-throughput.md](chunk-streaming-throughput.md)), a 245-chunk load
takes 133 ticks, of which roughly 35 are spent waiting on the encoder. Direct delivery would
therefore save on the order of half a second on a 2.2-second load, and less than that in
practice because it removes only the encode/decode halves, not the scheduling.

Against that:

- It makes single-player structurally different from a remote client. `docs/architecture.md`
  states single-player communicates "through the same Netcode for Entities replication path
  used by remote clients", and the value of that is that ordinary play exercises the code
  multiplayer depends on. A bypass creates a class of bug that can only appear in multiplayer.
- The bridge in `ChunkCompanionService` couples the two worlds' stores, which nothing else
  currently does.
- Two delivery paths means every future change to residency, baselines or eviction has to be
  reasoned about twice.

That trade was worth making when the alternative was an 18-second load. It is not obviously
worth making to save half a second.

## When to revisit

- Render distances grow enough that encode latency becomes a visible share of load time again.
- Profiling shows snapshot encoding, not scheduling or client-side decode, dominating.
- A single-player-only feature (very large paste/undo, world editing tools) needs chunk
  throughput the wire path cannot reach.

If it is revisited, build it as the toggle described above rather than as the default, so the
wire path stays the one that is exercised by default.
