# World residency and companion binding

Implemented September 6, 2026. This completes steps 1 and 2 of the
[meshing readiness guide](chunk-meshing-readiness.md). The approved choices and
research comparisons are in the [implementation plan](chunk-residency-binding-plan.md).

## World ownership

`DigBlocks.Voxels.Runtime` owns a separate `ResidentChunkStore` per configured ECS
world. `ChunkWorldSystem.Configure` creates it; world destruction releases it.
`Acquire` returns an explicit `ChunkLease`. Multiple leases share one unmanaged
chunk payload and one lightweight `ResidentChunk` entity. The final release frees
the payload/entity; reacquiring the address creates a new incarnation. This slice
creates empty dummy chunks and does not persist edits after final release.

Store operations run on the owning thread. Registry validation precedes atomic
edits and updates the ECS revision. Snapshot requests use the existing Burst
capture job, copy a coherent detached image, then encode on a bounded ThreadPool
worker. Workers never touch live ECS or native palette allocations. Completed
results occupy capacity until consumed. Cancellation and unload suppress results;
an edit alone preserves the coherent older snapshot for future delta recovery.
Shutdown completes captures and joins workers before releasing resources.

Provisional defaults are 256 resident chunks and two snapshot requests per world;
these are constructor/configuration limits, not measured production budgets.
The service pins one server dummy chunk at world 1, origin. Its client store is
separate and empty until replica publication is implemented.

## Session flow and failure policy

Bootstrap starts `ChunkCompanionService` after `NetCodeSession`, and stops it first.
After native admission, a targeted offer advertises the separate port, peer and
connection generation, short-lived single-use ticket, chunk edge and registry
fingerprint. The client checks compatibility and binds through independent UTP.
The server checks live membership, generation, deadline and ticket possession
before accepting. A wrong generation does not consume the ticket. Replay cannot
replace a bound connection; an invalid ticket cannot evict the claimed peer.

Singleplayer uses independent IPC. Remote servers use a second UDP port, normally
game port + 1. `--bulk-port 7778` overrides it on a dedicated server; clients use
the advertised port. CLI values must be 1..65535 and differ from the game port;
game port 65535 needs an explicit override. Singleplayer/client overrides are
rejected. The programmatic server constructor additionally permits port 0 for an
ephemeral test listener.

Defaults: 10 seconds to bind, 16 pending unauthenticated connections, and at most
five seconds per pending connection. Required channel loss disconnects only the
affected native peer. Typed failures include `ChunkChannelFailed`,
`ChunkBindingTimedOut`, `ChunkRegistryMismatch` and `ChunkLayoutMismatch`.
Kick reasons retain the native close grace period. No automatic reconnection is
implemented. Startup failure/cancellation rolls back resources. Driver disposal
flushes UTP disconnect notifications with a completed update before destruction.

`DigBlocksBootstrap.ChunkCompanion.ClientState` reports binding progress; `Bound`
means only that the required channel is available. Native session state remains
`AwaitingWorldData`; no `NetworkStreamInGame` marker is added. Bound endpoints do
not yet route chunk-transfer frames or automatically send snapshots.

## Implementation files

Paths below are relative to `Assets/_Project/`.

| Files | Responsibility |
| --- | --- |
| `Scripts/Voxels/Runtime/ResidentChunkStore.cs`, `DigBlocks.Voxels.Runtime.asmdef` | Lease/entity ownership, captures, worker results, world system |
| `Scripts/Networking/NetCode/Bulk/BulkBindingFrames.cs` | Fixed versioned request/accept frames |
| `Scripts/Networking/NetCode/Bulk/BulkCompanionSystem.cs` | Offer RPC, peer binding, caps, deadlines and revocation |
| `Scripts/Networking/NetCode/Bulk/ChunkCompanionService.cs` | Host startup/rollback/teardown and stores |
| `Scripts/Networking/NetCode/Bulk/BulkDriver.cs` | Disconnect flush on disposal |
| `Scripts/Networking/AdmissionRegistry.cs`, `NetworkLaunchSettings.cs` | Failure values and port parsing |
| `Scripts/Networking/NetCode/Runtime/NetCodeSession.cs` | Internal lifecycle/world access and typed close reuse |
| `Scripts/Bootstrap/GameServiceComposer.cs`, `DigBlocksBootstrap.cs` | Service composition and diagnostics |
| Bootstrap/NetCode/NetCode PlayMode `.asmdef` files | New dependency references |
| `Tests/EditMode/VoxelRuntime/ChunkResidencyTests.cs`, test `.asmdef` | Residency/worker lifetime contracts |
| `Tests/EditMode/Bootstrap/ChunkLaunchTests.cs`, `GameServiceComposerTests.cs` | Launch validation and service order |
| `Scripts/Networking/NetCode/PlayModeTests/BulkBindingTests.cs`, `ChunkCompanionTests.cs` | Wire validation and real IPC/UDP lifecycle/adversarial tests |

## Verification and remaining scope

Fresh Unity 6000.6.0f1 results: 70/70 EditMode, 43/43 broad PlayMode, then 12/12
focused companion tests after adding three adversarial cases. The latter overlaps
the broad run; these counts must not be summed. No C# compiler diagnostics were
found in those successful logs. Actual project scripts, assembly definitions and
SampleScene were copied to an isolated Unity verification project while the live
editor stayed open. Presentation/vendor assets were not all copied; this is not
a full-project visual or build verification. Evidence is under
`.utmp/residency-binding-verification/` in `ownership-launch-final`,
`session-playmode-verified` and `companion-adversarial` XML/log pairs.

Next are interest/subscriptions and atomic client replica publication with ACKs,
bounded history, eviction generations and resync, followed by integrated streaming
failure/performance measurements. Existing 32-peer admission tests do not establish
32-peer chunk throughput. Meshing, rendering, generation and persistence remain
outside this slice.

## Commit scope

The residency/binding commit includes its required, previously uncommitted native
session foundation and bootstrap/runtime integration. Unrelated assets, project
settings and workflow edits are excluded. This documentation/commit pass performs
no additional implementation or verification; the results above are from the
preceding implementation pass.
