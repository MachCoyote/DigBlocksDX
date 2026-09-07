# Chunk streaming implementation

Status: September 7, 2026. The dummy chunk-data pipeline is implemented through
client publication and separate data readiness. Meshing, terrain generation,
persistence, controllers and playable ghosts remain outside this milestone.

## Runtime path and ownership

Native admission offers a single-use ticket for the independent reliable IPC/UDP
companion. Binding checks the live native peer generation, chunk layout and
registry fingerprint. After binding, the server declares its permitted interest
and streams chunk data automatically. Payloads do not use NetCode RPC queues.

`ChunkWorldSystem` owns one `ResidentChunkStore` per ECS world. The server store
owns native chunk channels and a lightweight identity/revision entity for each
resident address. Per-peer subscriptions hold leases; overlaps share payloads and
entities. No voxel is an entity or ghost. Final release discards dummy contents
and history. Reentry after unload receives a new incarnation.

`TryReplaceLeases` validates the complete new neighborhood and computes final
union capacity, accounting for other consumers' leases. Capacity rejection leaves
the previous neighborhood intact. New entries are staged before obsolete leases
are released; this can temporarily allocate up to one extra bounded neighborhood.
Overlapping leases and their incarnations are retained.

The server chooses anchors through `ChunkCompanionService.SetServerInterest`.
This is a server API, not a client bulk request. Addresses include world identity;
horizontal/vertical radii are independent. The bounded cuboid enumerates its
anchor first and cycles through the rest deterministically. Coordinate overflow
and oversized declarations are rejected before allocation.

Each accepted interest replacement advances a connection-local epoch. It cancels
old captures/transfers and resets applied baselines. The client replaces the whole
interest set, including evicting overlapping replicas, then republishes under the
new epoch. This intentionally simple policy keeps eviction/reentry unambiguous;
retaining client overlap and adding hysteresis are later optimizations.

## Scheduling and revision recovery

The companion runs after ordinary admission simulation. The scheduler rotates
first access to capture capacity and application-byte budgets among peers, and
cycles through each peer's chunks. One peer can have one outstanding transfer.
Slow driver queues return backpressure; they do not allocate an unbounded backlog.

Existing coherent Burst captures feed detached images to bounded worker encoders.
Workers never access live ECS or native channel allocations. Cancelled capture
results cannot publish into a replacement subscription. Encoded payload storage
is released once all slices enter the bounded driver queue; waiting for an applied
ACK does not retain a global payload slot.

Transport queue acceptance is not an applied ACK. Only an ACK matching the active
transfer and declared revision advances that peer's baseline. Late ACKs for expired
or replaced transfers are ignored; unknown future IDs, premature ACKs and wrong
revisions fail the affected channel.

Edits retain a bounded per-resident history of absolute cell replacements. Deltas
coalesce final values across a contiguous exact baseline. A missing baseline,
expired history, oversized edit batch or explicit resync uses a fresh snapshot.
Edits during a snapshot remain eligible after its applied ACK, so replicas catch
up rather than silently dropping in-flight changes. Progress timeout retries use
new transfer IDs and a snapshot baseline; two retries are allowed before the
existing affected-peer failure policy closes the required companion/native peer.

## Atomic replicas and the meshing boundary

Client stores explicitly enable replica mode and reject authoritative lease
acquisition. Before publication the client validates the current interest epoch,
address, transfer declaration, decoded identity/revision, layout/CRC, registry IDs
and solid/fluid compatibility. A delta additionally requires the exact local
baseline. Native replacement data is built before publishing it with the ECS
revision; invalid input leaves the existing replica unchanged. Equal-revision
conflicts are rejected. The applied ACK is queued only after publication succeeds.

Eviction and connection/world teardown release native replicas after dependent
captures complete. Stale declarations, slices and worker results cannot recreate
an evicted replica. One in-progress reassembly and one pending response bound
client staging. Decode/import work is synchronous and bounded to the single
transfer completing for that peer; the current implementation does not introduce
a separate asynchronous decode queue.

`ChunkCompanionService.ClientDataReady` and `ResidentChunkStore.DataReady` mean all
addresses in the current small neighborhood have published replicas. Interest
replacement clears readiness until that set is complete. Binding remains a
separate state; native session state stays `AwaitingWorldData`, and
`NetworkStreamInGame` remains absent.

A future mesher can call `TryCaptureReplica(address, out capture)` for a coherent,
job-backed detached capture, retaining its address/incarnation/revision and the
store's interest epoch for stale-result checks. `TryReadReplica` supplies a managed
image for diagnostics. No meshes, mesh uploads, neighbor policy or rendering
components have been added.

## Provisional limits

These are development defaults, not release view distances or throughput claims.

| Resource | Default / hard bound |
| --- | --- |
| Initial server neighborhood | world 1/origin, horizontal radius 1, vertical radius 0: 9 chunks |
| Interest declaration | at most 256 chunks; independent validated integer radii |
| Authoritative store / client replicas | 256 residents per world |
| Capture/encoding requests | 2 per store, including completed results until consumed |
| Encoded payload/request reservations | 4 globally; configurable 2–16 |
| Outstanding transfers | 1 per peer |
| Application send budget | 16,384 bytes globally and 4,096 per peer per simulation tick, including frame headers |
| Driver application queues | 256 messages globally, 64 per connection, at most 1,024 bytes per message |
| Snapshot / delta wire limit | 262,272 / 393,344 bytes at edge 32 |
| Client reassembly | 1 payload within its codec limit, plus one coverage bit per byte |
| Edit history | 8 revisions and 4,096 cell updates per resident |
| Progress timeout | 10 seconds; configurable 1–120; two retries then affected-peer failure |

Four maximally sized codec payloads bound encoded server ownership to 1,573,376
bytes, separately from capture images, native residents, transport queues and
transient decoding allocations. The current retained deltas are smaller than the
codec's maximum. Fixed one-transfer admission is the receiver credit in this
slice; adaptive feedback, shared encoding caches, compression and production
budget tuning are not implemented. Budgets are per tick, so effective rates depend
on simulation scheduling. Metrics count admitted application bytes, not UDP/IP
headers, reliable retransmissions or native control traffic.

## Files

Paths below start at `Assets/_Project/`.

| File | Responsibility |
| --- | --- |
| `Scripts/Networking/Chunks/ChunkInterest.cs` | Validated interest, epoch and bounded enumeration |
| `Scripts/Voxels/Runtime/ResidentChunkStore.cs` | Residency replacement, history, captures, replica publication/readiness |
| `Scripts/Networking/NetCode/Bulk/ChunkTransfer.cs` | Interest/start/slice/ACK/resync frame contracts and reassembly |
| `Scripts/Networking/NetCode/Bulk/ChunkStreamingServer.cs` | Subscriptions, scheduling, recovery, limits and measurements |
| `Scripts/Networking/NetCode/Bulk/ChunkStreamingClient.cs` | Live validation, publication, ACK/resync and cleanup |
| `Scripts/Networking/NetCode/Bulk/BulkCompanionSystem.cs` | Bound routing, affected-peer lifecycle and ECS ordering |
| `Scripts/Networking/NetCode/Bulk/ChunkCompanionService.cs` | Host/store setup, server interest API and readiness diagnostics |
| `Scripts/Networking/NetCode/Bulk/BulkDriver.cs` | Reliable bounded carrier; internal optional bounded UTP test simulator |
| `Scripts/Networking/NetCode/AssemblyInfo.cs` | Internal test access without widening runtime public APIs |
| `Tests/EditMode/ChunkProtocol/ChunkInterestTests.cs` | Neighborhood bounds, worlds and coordinate overflow |
| `Tests/EditMode/VoxelRuntime/ChunkResidencyTests.cs`, `ChunkReplicaTests.cs` | Capacity/overlap/history, atomic publication and stale reentry |
| `Scripts/Networking/NetCode/PlayModeTests/ChunkTransferTests.cs`, `ChunkCompanionTests.cs` | Framing and production IPC/UDP lifecycle/streaming |
| `Scripts/Networking/NetCode/PlayModeTests/ChunkStreamingTests.cs`, `ChunkStreamingScaleTests.cs` | Recovery/adversarial cases, fairness and measured scale/loss runs |

## Verification and measured limits

Unity 6000.6.0f1 checks on exact project scripts, asmdefs and SampleScene in the
isolated verification project passed 108/108 EditMode and 55/55 broad PlayMode.
These suites include IPC/UDP binding, late join, in-flight edits with full channel
comparison, overlap/disjoint interest, eviction/reentry, registry/layout rejection,
malformed/stale traffic, cancellation, affected-peer failure, teardown, blocked-peer
isolation and baseline resync. A subsequent 4/4 focused streaming suite also
passed deliberate applied-ACK loss, snapshot retry after an in-flight edit and
late-ACK rejection. These focused cases overlap the broad suite except the new
ACK-loss case; do not sum their counts. Assembly definitions were unchanged.

Full-project compilation also succeeded in a separate copy containing all current
Assets, Packages and ProjectSettings. The final cached compile of the latest
source exited successfully without C# compiler diagnostics. This is compile
evidence, not a player build or visual inspection.

The scale fixture first admits peers, then simultaneously replaces their interest
with one nonuniform solid/fluid chunk and compares every cell. It records queue,
replica and ACK observations on the production companion. UTP simulation uses
50 ms outgoing delay, +/-10 ms jitter, deterministic loss of every tenth sent
packet, and bounded simulator buffers on both endpoints.

| Run | Application bytes | First / last replica applied | Maximum transfer-to-applied-ACK | Peak encoded payload bytes |
| --- | --- | --- | --- | --- |
| 4 UDP peers with delay/loss | 34,156 | 0.539 / 0.659 s | 0.629 s | 33,064 |
| 32 UDP peers, loopback | 273,248 | 0.727 / 4.204 s | 0.906 s | 33,064 |

Both observed at most four payload reservations and two captures. Process managed
peaks were about 30.7 / 40.9 MiB; Unity allocated peaks about 669 / 1,809 MiB for
4 / 32 peers. These include the test editor and all NetCode worlds, not just chunk
storage. The runs shared the machine with initial full-project import and emitted
server tick-batching warnings. They establish bounded progress under these tested
conditions, not acceptable gameplay tick rates or production WAN capacity.

Evidence lives under `.utmp/residency-binding-verification/` in
`streaming-all-editmode`, `streaming-all-playmode`, `streaming-scale-verified`,
`streaming-integrated` and focused result/log pairs. Full-project logs are under
`.utmp/streaming-full-project/`. These are local verification artifacts; source
tests are the reproducible contracts. No player build, graphical inspection,
real-WAN test, sustained production load or meshing benchmark was performed.

## Completion audit

| Pre-meshing requirement | Evidence |
| --- | --- |
| Shared lifecycle contracts | Validated `ChunkInterest`, epoch-qualified declarations, bounded options and documented ownership |
| Server interest / union residency / reentry | Capacity/overlap/history EditMode contracts; IPC reentry and UDP disjoint-interest production tests |
| Worker-to-companion delivery / fair bounds | Production IPC/UDP snapshots; blocked-peer test; measured four-payload/two-capture maxima with 32 peers |
| Atomic validated client publication | Replica EditMode contracts; declaration mismatch, missing baseline, publish-before-ACK and stale epoch tests |
| Applied ACK / history / resync / edits in flight | History retention contracts; UDP in-flight edit convergence; explicit missing-baseline and lost-ACK recovery tests |
| Separate initial data readiness | Production tests await `ClientDataReady`, compare all required replicas, and assert `NetworkStreamInGame` remains absent |
| Integrated failure and scale verification | 108 EditMode, 55 broad PlayMode, 4 focused recovery cases; full-project copied compile; bounded delay/loss and multi-peer measurements |
| Stop before rendering | No mesher, terrain generation, persistence or playable readiness added; remaining rendering choices are listed in meshing readiness |

Final diff and lean workflow checks passed. Unrelated user worktree edits remain
preserved. Compiler/test evidence applies to the copied project inputs and source
checks recorded above; no graphical/player-build claim is implied.
