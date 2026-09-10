# Dynamic chunk loading implementation summary

Status: implemented September 9, 2026.

This change replaces the permanently pinned nine-chunk demonstration with moving,
server-authoritative chunk interest. It also adds a bounded Perlin terrain source for
rendering tests while preserving the original authored fixture.

## Outcome

- The development fly camera currently supplies the local player's position.
- Horizontal X/Z and vertical Y render distances are configured independently.
- The client reports only its current chunk anchor; the server owns the admitted world
  and distances.
- Newly required chunks stream closest-first, so initial loads and movement propagate
  radially out from the current anchor.
- Overlapping chunks survive an interest change. Chunks that leave interest are removed
  individually on the client, and server chunks unload after their final peer lease ends.
- Authoritative content is loaded lazily through a source interface that can later be
  backed by saved chunks.
- The original 3x3 sample remains unchanged. Other horizontal chunks through six chunks
  from the world origin use deterministic sample Perlin terrain.

## Runtime flow and authority

`TerrainRenderService` converts the fly-camera position to a chunk address using
mathematical floor. `ChunkInterestReporter` sends only chunk-boundary changes to
`ChunkCompanionService`, which queues the latest anchor on the existing reliable
bulk-data connection. A rejected report is retried instead of being treated as sent.

The server validates the requested world and constructs the interest from
`Assets/_Project/Resources/ChunkStreamingSettings.asset`. The development defaults are
world 1, horizontal distance 2, and vertical distance 1, producing a 5 x 3 x 5 neighborhood
of 75 chunks. Clients cannot select their own radii. `ChunkInterest.MaximumChunks` caps a
single neighborhood at 256 chunks, which also fits the default renderer's 256 chunk slots.

The existing `SetServerInterest` entry point remains available for a future authoritative
player system. Replacing the temporary camera report with a player position therefore does
not require changes to residency, transfer, or rendering.

## Scheduling and movement

`ChunkInterest.Addresses` orders the whole neighborhood by increasing squared 3D chunk
distance from its anchor. Equal distances use Y, then Z, then X as deterministic
tie-breakers. The server scans that order, skips retained chunks whose baseline is already
applied, and starts the closest missing transfer. Loading consequently expands radially at
startup and restarts around the newest anchor after movement.

Moving the anchor creates a new interest epoch. Both sides retain addresses shared by the
old and new neighborhoods:

- The server keeps their leases and applied baselines, so only newly entered chunks need
  snapshots or deltas.
- The client removes only departed replicas. The mesh scheduler retires those GPU slots,
  dirties their neighbors, and rejects stale work from the previous epoch.
- A server chunk remains resident while any peer leases it. Releasing the final lease
  removes its current data and entities from the resident store.

Rapid movement can supersede an interest before its declaration reaches the client. In that case
an address may leave and re-enter server residency while appearing continuously interested to the
client. The live-epoch full snapshot is authoritative and replaces the client's retained older
incarnation, including when the new incarnation's revision sequence restarted. Deltas remain strict
and still require the exact incarnation and base revision. Recovery allowances are tracked per chunk
so unrelated baseline failures in one apply batch cannot consume a shared retry counter. See
[chunk interest incarnation recovery](chunk-interest-incarnation-recovery.md).

Superseded on September 9, 2026: the scheduler described here carried one active transfer
per peer and would not request the next chunk until the current one was acknowledged. That
serialisation dominated load time and was replaced by a bounded in-flight window. See
[chunk-streaming-throughput.md](chunk-streaming-throughput.md). Selection is still
closest-first; arrival order is now radial only to within the window depth.

## Authoritative content and persistence seam

`IAuthoritativeChunkSource.LoadOrGenerate` is called lazily immediately before the first
snapshot of a new resident incarnation. An explicitly authored or edited chunk is marked
loaded and is not overwritten by the source. This gives future save loading one place to
resolve saved data before falling back to generation.

The seam is deliberately synchronous for this development slice. There is no save format,
disk I/O, asynchronous load queue, dirty tracking, or save-before-eviction policy yet.
Because final lease release currently discards the resident data, persistence work must
stage or complete a save before that disposal path. The final terrain generator will also
need world seed/version metadata rather than depending on this fixture source.

`TerrainFixtureChunkSource` remains test content only:

- The original 3x3 fixture at chunk Y=0 is preserved, including its wall, height steps,
  and test-block marker.
- Other chunk columns with X and Z coordinates from -6 through +6 use deterministic
  classic Perlin noise.
- Every generated column contains bedrock, one base stone block, zero to four additional
  stone blocks, one dirt block, and grass on top.
- Vertical chunks other than Y=0 and horizontal chunks outside the bounded sample area
  are empty.

Biomes, caves, structures, unbounded generation, final terrain rules, and persistence are
outside this sample-data implementation.

## Main files

| File | Responsibility |
| --- | --- |
| `Resources/ChunkStreamingSettings.asset` | Authored world and independent render distances |
| `Networking/Chunks/ChunkInterest.cs` | Validated interest volume and radial ordering |
| `Networking/NetCode/Bulk/ChunkTransfer.cs` | Client anchor-request frame |
| `Networking/NetCode/Bulk/ChunkStreamingClient.cs` | Latest-anchor queue and incremental replica interest |
| `Networking/NetCode/Bulk/ChunkStreamingServer.cs` | World validation, server radii, retained baselines, and source scheduling |
| `Voxels/Runtime/ResidentChunkStore.cs` | Loaded-incarnation seam, leases, and incremental replica eviction |
| `Voxels/Runtime/IAuthoritativeChunkSource.cs` | Future saved-chunk/generation boundary |
| `Client/Rendering/TerrainRenderService.cs` | Temporary camera-position adapter |
| `Client/Rendering/ChunkMeshScheduler.cs` | Per-chunk mesh removal and neighbor rebuilds |
| `Bootstrap/TerrainFixtureService.cs` | Preserved fixture plus bounded sample Perlin source |

Paths in this table are relative to `Assets/_Project`.

## Verification

The behavior was implemented with focused regression tests for overlap retention and radial
ordering, including red tests against the previous behavior. Fresh final checks produced:

- Unity compilation: passed with no compiler errors.
- EditMode: 231 passed, 0 failed, 1 ignored.
- Full NetCode PlayMode suite: 54 passed, 0 failed.
- Focused dynamic-streaming and sample-content PlayMode selection: 18 passed, 0 failed.
- Focused moving-interest radial-order test: passed; both initial and post-move transfer
  starts were nondecreasing by distance.
- DX12 terrain scheduler tests: 2 passed, 0 failed.
- Lean workflow and diff checks: passed.

The graphical bootstrap checks reached `Playing`, built all 75 default-interest chunks,
and produced nonzero geometry. Their batch run cannot complete the return-to-title cleanup
because Unity does not support the existing `WaitForEndOfFrame` teardown wait in
`-batchmode`; the renderer itself was covered by the direct DX12 scheduler tests. No player
build or long-distance manual movement soak was run for this slice.

## Deferred work

- Replace the fly-camera adapter with the authoritative player entity once player ghosts
  and movement exist.
- Add asynchronous saved-chunk lookup/generation and save-before-final-unload semantics.
- Replace the bounded sample source with the separately designed final terrain generator.
- Consider hysteresis, prefetch, or direction-aware priority only if movement profiling
  shows radial chunk-distance ordering is insufficient.
