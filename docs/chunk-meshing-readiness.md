# From chunk data to meshing

Status: September 7, 2026. The pre-meshing chunk-data path is implemented.
See [streaming contracts and evidence](chunk-streaming-implementation.md) for
current limits and verification, and [implementation progress](chunk-implementation-progress.md)
for earlier milestones.

## Completed data foundation

| Foundation | Current behavior |
| --- | --- |
| Storage and codecs | 32-cube world-qualified chunks, immutable registry, native solid/fluid channels, coherent captures, portable snapshots and deltas |
| Residency and workers | World-owned ECS identities, shared leases, incarnations, atomic capacity-checked interest replacement, bounded captures/encoding |
| Companion lifecycle | Native admission, single-use generation-bound ticket, independent reliable IPC/UDP connection, compatibility checks and affected-peer failure |
| Interest streaming | Server-owned dummy neighborhood, independent radii, epochs, fair bounded sends, cancellation and safe eviction/reentry |
| Replicas and recovery | Validated atomic client publication, applied ACK baselines, bounded delta history, snapshot recovery and separate data readiness |
| Verification | Production IPC/UDP lifecycle tests, late join/in-flight edits, stale/malformed traffic, teardown, multi-peer and delay/loss tests; current compile/test details are in the streaming summary |

The production companion delivers replicas automatically. Default initial interest
is a provisional nine-chunk cuboid. Changing interest replaces the client epoch
and republishes the new neighborhood; server overlap leases remain shared.
Final authoritative release discards dummy edits. Nothing persists across unload.

## Entry point for meshing

`ChunkCompanionService.ClientDataReady` reports that the current small interest
set has published replicas. The client world's `ChunkWorldSystem.Store` exposes
`TryCaptureReplica` for coherent detached data and `TryReadReplica` for inspection.
Consumers must reject stale results by current interest epoch and captured
address/incarnation/revision. `NetworkStreamInGame` remains unset.

A future visual milestone can use a camera and a few dummy chunks to inspect
updates and borders. It does not require terrain generation, a controller,
playable ghosts, persistence or full fluid simulation. The existing storage owner
should remain the source of mesh input.

Measured multi-peer progress is development evidence, not a production frame-time
or WAN guarantee: the 32-world test emits tick-batching warnings and allocator
measurements include its editor/world overhead. No graphical or player-build
validation is claimed by this data milestone.

## Decisions to settle when beginning the meshing milestone

- Initial rendering scope: solid opaque cubes only, or include transparent solids,
  fluids and custom models. Existing model keys are identifiers, not render assets.
- Meshing approach: begin with exposed-face generation or invest immediately in
  greedy merging; define compatible material/texture and face-merging rules.
- Border behavior: neighbor sampling, how missing neighbors are represented, and
  which adjacent chunks become dirty when a boundary cell or residency changes.
- Presentation ownership: bounded mesh build/upload scheduling, stale-result
  rejection by incarnation/revision, mesh disposal, and main-thread Unity uploads.
- Visual milestone: texture/material setup, lighting expectations, camera and
  inspection controls; whether collision is needed at this stage.

These are consultation points, not selections already made. ECS/Burst where
practical, server authority and separate client replicas remain constraints.
The next development milestone is choosing the initial meshing scope; this
chunk-data implementation stops here without adding a renderer.
