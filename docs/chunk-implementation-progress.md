# Chunk implementation progress

## September 6, 2026: block registry

Completed `Assets/_Project/Scripts/Voxels/BlockRegistry.cs` against the existing
`Assets/_Project/Tests/EditMode/Voxels/BlockRegistryTests.cs` contract.

- Definitions validate lowercase namespaced identifiers and canonicalize finite
  state properties and tags with ordinal ordering. Input collections are detached.
- Solid air and fluid empty occupy ID zero; remaining states receive ordinal,
  dense uint IDs. Unknown keys and out-of-range IDs fail explicitly.
- SHA-256 compatibility fingerprints include both channel boundaries, state keys,
  behavior/model keys, fluid permissions and tags, with length-prefixed fields.
- Dummy definitions provide air, stone, empty fluid and water.

Verification: all 16 existing NUnit cases failed against the stub and then passed.
Fresh Unity 6000.6.0f1 EditMode execution also passed 16/16 in an isolated project
containing exact copies of the source/tests. No C# compiler errors or warnings.
This verifies registry behavior and Unity compatibility, not the full game assembly
graph. Temporary evidence: `.utmp/registry-verification/results.xml` and `editor.log`.

The registry commit includes its existing tests and the minimal Unity assembly
and asset metadata needed to compile those files. Other pre-existing storage,
networking, bootstrap, settings, texture and workflow changes remain outside it.

## September 6, 2026: transfer components and storage coverage

Registry work was committed as `728f49b`. The transfer milestone commit includes
the following changes and their previously untracked storage/codec/carrier
dependencies and Unity metadata. Unrelated session/bootstrap, project settings,
textures and workflow changes are excluded.

Follow-on implementation:

- `Assets/_Project/Scripts/Networking/NetCode/Bulk/ChunkTransfer.cs`: strict,
  versioned start/slice/applied-ACK/eviction frames; bounded two-transfer staging;
  idempotent duplicates; atomic overlap validation; exact-generation cancellation.
- `Assets/_Project/Scripts/Networking/NetCode/PlayModeTests/ChunkTransferTests.cs`:
  retained the five existing contracts and added four boundary/validation cases.
- `ChunkTransferIntegrationTests.cs` in the same test directory: actual IPC and
  loopback UDP snapshot/delta transfers, queue backpressure, capture during edits,
  replica publication before ACK and full solid/fluid comparison.
- `Assets/_Project/Tests/EditMode/Voxels/ChunkDataTests.cs` and
  `ChunkStorageTests.cs`: capture mutation/disposal safety, detached imports,
  matching-baseline replica changes, records/provenance cleanup, no-op/invalid
  batches, invalid cells and direct palette fallback. These tests passed against
  the existing storage implementation; storage runtime code was not changed.
- `Assets/_Project/Scripts/Networking/Chunks/ChunkImage.cs` and `ChunkWireCodec.cs`:
  reject zero revisions consistently with storage and transfer declarations.
  `Assets/_Project/Tests/EditMode/ChunkProtocol/ChunkWireCodecTests.cs` includes the
  regression and uses nonzero baselines in existing malformed-data fixtures so
  those assertions still reach their intended validation paths.

Fresh Unity 6000.6.0f1 results in an isolated project using the actual source and
installed package versions:

| Suite | Result |
| --- | --- |
| Transfer tests against original stub | 0/5, then 0/9 with added boundaries |
| Transfer frames/reassembler | 9/9 passed |
| Expanded storage and codec tests before revision fix | 44/45 passed |
| Final storage/registry/codec EditMode tests, edge 32 | 45/45 passed |
| Isolated edge-16 and edge-64 storage/codec variants | 45/45 passed each |
| Carrier, tickets, framing and composite IPC/UDP PlayMode tests | 19/19 passed |

The zero-revision regression failed before the fix. The first fixed run exposed
six older tests using zero-revision fixtures; these were updated to valid baselines
without weakening their original corruption, index, ownership or length checks.
Temporary XML/log evidence is under `.utmp/chunk-verification/`, using
`transfer-red`, `transfer-boundaries-red`, `transfer-green`, `storage-red`,
`storage-green`, `storage-final`, and `bulk-integration` names.
Variant evidence is in `.utmp/chunk-verification-edge16/size16.xml` and
`.utmp/chunk-verification-edge64/size64.xml`. Only the temporary copy of the
single chunk-edge constant changes for those runs; production remains edge 32.

The live project was left open. These runs validate the selected source components
in Unity, not the complete DigBlocksDX assembly graph, bootstrap or admission
integration. The integration fixture drives transfers directly; it is not a
production scheduler or a 32-peer/loss/latency performance result.

Documentation now reflects the approved independent carrier. The exact framing
contract and ownership limits are in [chunk transfer protocol](chunk-transfer-protocol.md).
The assembly map was regenerated and `Test-LeanWorkflow.ps1` passed.

## Remaining implementation

The dependency order, completion criteria and later rendering consultation points
are detailed in [meshing readiness](chunk-meshing-readiness.md).

World residency, bounded snapshot workers and admitted companion binding are now
implemented. Next: interest/subscription scheduling, atomic replica publication,
ACK/history/resync and independent data readiness, then loss/latency and multi-peer
streaming measurements.

The new milestone implements the user-approved leases and affected-peer failure
policy, including IPC/UDP binding, compatibility checks, ticket replay protection,
pending caps/deadlines, rollback and port configuration. Driver disposal now
flushes UTP disconnect notifications. Fresh checks passed 70/70 EditMode, 43/43
broad PlayMode and a later overlapping 12/12 companion suite. See the
[implementation summary](chunk-residency-binding.md) for files, evidence and limits,
and [research and decisions](chunk-residency-binding-plan.md) for comparisons.

The approved carrier uses IPC for singleplayer and a configurable second UDP port
for remote peers. Chunk payloads do not use NetCode RPC queues. No new major
architectural decisions have been made. Release budgets, view distances and
production timeouts remain subject to measurement and user consultation.
