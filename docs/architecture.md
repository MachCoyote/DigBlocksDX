# DigBlocksDX Architecture

## Status

This is the current approved project direction as of September 3, 2026.

## Technology Direction

DigBlocks uses Unity Entities/ECS as the primary gameplay simulation model and
Netcode for Entities as its networking framework. The game remains a
server-authoritative client/server application able to run as single-player, a
remote client, or a dedicated server from one codebase.

GameObjects remain appropriate for the Unity entry point, authoring workflows,
UI, cameras, audio, and presentation systems that do not benefit from ECS.
Network authority and high-volume simulation should not depend on per-object
MonoBehaviour updates.

## Runtime Topology

### Single-player

Single-player runs a server ECS world and a client ECS world in the same process.
They remain logically separate and communicate through the same Netcode for
Entities replication path used by remote clients.

### Remote client

A remote client runs the client ECS world plus client presentation. It sends
commands to, and consumes authoritative snapshots from, a remote server.

### Dedicated server

A dedicated server runs only the authoritative server ECS world and required
networking, persistence, generation, and simulation systems. Client-only
presentation and authoring code must be removable from this build.

## Assembly Boundaries

- `DigBlocks.Core` owns application lifecycle, launch options, logging contracts,
  and other framework-independent primitives.
- `DigBlocks.Bootstrap` is the Unity composition root. It chooses the launch mode
  and constructs the ordered application services.
- `DigBlocks.Client` owns client-world coordination and GameObject presentation
  bridges.
- `DigBlocks.Server` owns authoritative server-world coordination.
- `DigBlocks.Networking` owns transport-independent session and protocol
  contracts.
- `DigBlocks.Networking.NetCode` owns Netcode for Entities and Unity Transport
  integration. Transport-specific types should not leak into Core, voxel storage,
  or save data.
- Future shared simulation assemblies will own ECS components and deterministic
  systems used by both client and server worlds. They may depend on
  `Unity.Entities` without depending on `Unity.NetCode`.

The existing `ClientRuntime` and `ServerRuntime` services are lifecycle shells.
They will eventually create, expose, and dispose their respective ECS worlds;
they are not the location for per-entity gameplay logic.

## Entity Model

Dynamic objects such as players, mobs, dropped items, and projectiles are ECS
entities. Networked instances use ghosts where ordinary replication is a good
fit.

The locally controlled player can use owner prediction and rollback. Ordinary
mobs should normally be interpolated and server-authoritative. Prediction is
introduced only when its latency benefit justifies its client resimulation cost.

Simulation behavior is expressed through focused systems over compact unmanaged
components. Hot paths should be Burst-compatible and jobified when measurement
shows useful work can run in parallel. Avoid managed components and frequent
structural changes in high-volume simulation.

## Voxel World Model

Voxel blocks are not individual ECS entities or ghosts. World data is stored in
coarse three-dimensional chunks so vertical terrain, underground regions, and
multiple world layers do not require a fixed-height column model.

A chunk can have an ECS entity for identity, lifecycle, ownership, or scheduling,
but its block payload remains a dense or palette-compressed unmanaged container.
Block definitions are data-driven authoring content baked into immutable,
runtime-friendly lookup data.

Chunk replication uses a dedicated versioned bulk-data protocol:

- Initial snapshots transfer complete chunk state.
- Later messages carry versioned block deltas.
- Per-client interest determines which three-dimensional chunks are sent.
- Transmission is budgeted and batched rather than represented as one ghost per
  block.
- Save serialization and network serialization share domain concepts without
  requiring identical byte formats.

## Networking Model

Netcode for Entities is the only connection and entity-replication stack. Ghost
relevancy, importance, update rates, interpolation, and prediction policies are
configured by entity category.

Large voxel payloads remain an application-level protocol built on the Netcode
for Entities connection. Ghost replication is used for dynamic entities, not as
a replacement for chunk streaming.

The transport-independent `INetworkSession` contract remains the application
lifecycle seam. The Netcode adapter will configure and connect the client and
server worlds supplied by the runtime services without exposing Unity transport
details to the rest of the application.

When the session implementation is added, service registration follows dependency
order and shutdown occurs in reverse:

- Single-player: diagnostics, server world, client world, combined Netcode
  session.
- Remote client: diagnostics, client world, Netcode client session.
- Dedicated server: diagnostics, server world, Netcode server session.

The current scaffold stops before constructing the Netcode session.

## Performance Rules

- Profile server simulation, replication, and client presentation separately.
- Use spatial interest management for both ghosts and chunks.
- Do not run AI, pathfinding, collision, or replication at the same frequency for
  every mob regardless of distance or activity.
- Batch work by component layout and entity category.
- Quantize and delta-compress network state where precision permits.
- Keep inactive simulation asleep and avoid per-entity allocations.
- Treat prediction as a CPU budget, not a default ghost mode.

## Near-term Sequence

1. Add a minimal Netcode for Entities world/session lifecycle behind
   `INetworkSession`.
2. Verify single-player client/server worlds and a remote client can connect to a
   dedicated server with a protocol-version handshake.
3. Design and implement the transport-independent three-dimensional chunk data
   model.
4. Add chunk interest, snapshot, delta, and transmission systems.
5. Add client meshing and rendering.
6. Add dynamic entity simulation and ghost authoring incrementally.

This sequence deliberately establishes connection and world ownership before
chunk transmission while keeping the chunk model reusable in tests, persistence,
and offline tools.
