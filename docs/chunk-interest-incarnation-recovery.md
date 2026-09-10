# Chunk interest incarnation recovery implementation summary

Status: implemented September 10, 2026. Companion to
[dynamic chunk loading](dynamic-chunk-loading.md) and
[chunk loading optimization](chunk-loading-optimization.md).

## Outcome

Rapid camera movement no longer disconnects the session when a chunk leaves and re-enters server
interest before the intermediate interest declaration reaches the client. A full snapshot from the
client's live interest epoch can replace a retained older incarnation, while deltas keep their strict
incarnation and base-revision requirements.

Recovery attempts are counted per chunk rather than across the whole stream. Several independent
chunks can therefore request snapshots in one apply batch without being mistaken for repeated
failure of one chunk.

## Root cause

Interest declarations may be superseded before they enter the bounded transport queue. That is a
valid coalescing behavior, but it means the client can observe epoch 50 followed directly by epoch
52. During the skipped server epoch, a chunk can leave residency and later re-enter:

1. The client retains incarnation A because the address belongs to both interests it observed.
2. The server releases the address during the skipped intermediate interest.
3. Re-entry creates incarnation B with a fresh revision sequence.
4. The server sends an authoritative full snapshot for incarnation B under the live epoch.
5. The client previously rejected it solely because incarnation A was still resident.

The rejection requested a resync, but the server correctly returned the same incarnation B. The
client also used one global retry counter. With 32 applies per tick, three different rejected chunks
could exhaust that counter in one frame and deliberately fail the companion channel, which then
closed the main NetCode connection.

## Implementation

`ResidentChunkStore.PublishReplica` now treats a changed incarnation as a replacement when the
publication belongs to the current interest epoch and address. Revision regression and conflicting
same-revision validation still apply when the incarnation is unchanged. The streaming client already
checks a delta against its exact local incarnation and base revision before publication, so delta
semantics remain unchanged.

`ChunkStreamingClient` lazily creates a retry dictionary only after a resync is first needed. A
successful publication clears recovery state for that address and releases the dictionary once no
chunk is recovering; a new interest also clears all old recovery state. The ordinary successful
path allocates nothing and pays only a null check while no recovery is active.

The server keeps the matching retry count in an integer array parallel to its existing lease and
baseline arrays. Acknowledging one lease resets only that lease's count. Interest-declaration retry
accounting remains separate because it represents a peer-wide control message, not a chunk transfer.

Companion-channel terminal failures are logged before teardown. Protocol exceptions retain their
original exception and stack trace; retry exhaustion includes the interest epoch, anchor, queue and
transfer counts, readiness, and number of chunks currently recovering. Server diagnostic counts are
also computed only while formatting a terminal failure.

## Files

Paths are relative to `Assets/_Project`.

| File | Change |
| --- | --- |
| `Scripts/Voxels/Runtime/ResidentChunkStore.cs` | Accept live-epoch authoritative incarnation replacement |
| `Scripts/Networking/NetCode/Bulk/ChunkStreamingClient.cs` | Per-address lazy resync accounting and client diagnostic state |
| `Scripts/Networking/NetCode/Bulk/ChunkStreamingServer.cs` | Per-lease retry accounting and failure-only diagnostic state |
| `Scripts/Networking/NetCode/Bulk/BulkCompanionSystem.cs` | Preserve failure context and original exceptions before teardown |
| `Tests/EditMode/VoxelRuntime/ChunkReplicaTests.cs` | Skipped-interest incarnation replacement regression |
| `Scripts/Networking/NetCode/PlayModeTests/ChunkStreamingTests.cs` | Independent per-chunk resync regression |
| `Scripts/Networking/NetCode/PlayModeTests/ChunkCompanionTests.cs` | End-to-end failure logging regression |

## Verification

Fresh Unity 6.6 editor verification on September 10, 2026:

- Script refresh and compilation completed with zero console errors.
- The skipped-interest incarnation replacement regression passed (1/1).
- The client/server per-chunk retry regressions passed (2/2).
- `DigBlocks.Voxels.Runtime.Tests` passed (13/13).
- The complete `ChunkStreamingTests` and `ChunkCompanionTests` fixtures passed (25/25).
- `tools/Test-LeanWorkflow.ps1` passed after regenerating the repository map.
