# DigBlocksDX Architecture

## Status

This is the current approved project direction as of September 7, 2026.

## Repository Map

```text
Assets/_Project/
├── Scripts/
│   ├── Core/                 lifecycle, launch, logging, shared primitives
│   ├── Networking/           transport-independent session/protocol contracts
│   │   └── NetCode/          Unity NetCode and Transport integration
│   ├── Client/               client-world coordination, application flow, input
│   │   └── UI/               menu navigation, menu views, persistent UI root
│   ├── Server/               authoritative server-world coordination
│   ├── Voxels/               chunk storage, block definitions, registry, appearance
│   └── Bootstrap/            Unity entry point and composition root
└── Tests/
    ├── EditMode/             fast Core and Bootstrap contract tests
    └── PlayMode/             scene/bootstrap and multi-world integration tests

Assets/StreamingAssets/       authored game content loaded at runtime, block definitions included
Packages/                     Unity package manifest and resolved package lock
ProjectSettings/              project, rendering, build, and editor configuration
docs/                         architecture and focused subsystem documentation
.agents/skills/               selectively triggered repository workflows
.codex/config.toml            project-local Codex configuration
tools/                        small deterministic development/validation scripts
```

`Assets/Plugins/` and other vendor/package content are third-party inputs; edit
them only when a task explicitly owns that integration. `Library/`, `Temp/`,
`Logs/`, `obj/`, generated IDE projects, and `.utmp/` are generated or local
outputs and should not be committed or edited as source.

The checked-in [assembly map](generated/assembly-map.md) is generated from
`.asmdef` files. It is a compact structural index, not architectural rationale.

## Dependency Direction

```text
Unity entry / authoring
          |
          v
DigBlocks.Bootstrap --------------------+
     |             |                    |
     v             v                    v
DigBlocks.Client  DigBlocks.Server  DigBlocks.Networking.NetCode
     |             |                    |
     +-------------+----------+---------+
                              v
                    DigBlocks.Networking
                              |
                              v
                       DigBlocks.Core
                              ^
                              |
                     DigBlocks.Client.UI   (also referenced by DigBlocks.Client)
```

Client UI depends only on Core, uGUI and the input system. It must not reference
Networking, NetCode or ECS assemblies, so presentation cannot reach session or
simulation state directly; it reports intent and application flow interprets it.

Core must not depend on higher layers. Networking owns portable contracts;
NetCode implements them. Bootstrap may see all runtime assemblies because it is
the composition root. Client and Server coordinate their worlds but do not own
transport-specific implementation.

## Where to Look First

| Task | First locations |
| --- | --- |
| Launch modes or service lifetime | `Scripts/Core/Launch`, `Scripts/Core/Hosting`, then `Scripts/Bootstrap` |
| Composition or Unity startup | `Scripts/Bootstrap`, then Bootstrap EditMode/PlayMode tests |
| Portable network contract or admission policy | `Scripts/Networking`, then `docs/network-session-foundation.md` |
| Chunk storage, registry, or portable codec | `Scripts/Voxels`, `Scripts/Networking/Chunks`, and their EditMode tests |
| Block definitions, archetypes, or block content | `Assets/StreamingAssets/content/digblocks`, `Scripts/Voxels/Definitions`, then `docs/block-definitions.md` |
| NetCode RPC, transport, or world wiring | `Scripts/Networking/NetCode` and its PlayMode tests |
| Client/server world ownership | `Scripts/Client/Runtime` or `Scripts/Server/Runtime` |
| Menus, navigation, focus, or UI root | `Scripts/Client/UI`, then `docs/ui-menu-foundation.md` |
| Debug menu, debug toggles, or the terrain debug views | `Scripts/Client/Debugging`, `Scripts/Core/Diagnostics`, then `docs/debug-menu-implementation.md` |
| Terrain missing from the Scene view or another viewport | `Scripts/Client/Rendering/TerrainCameraSet.cs`, then `docs/terrain-secondary-camera-summary.md` |
| Application state, play/pause/quit intent | `Scripts/Client/Flow`, then `Scripts/Client/Input` |
| Session lifetime or world readiness | `Scripts/Bootstrap/Session`, then `Scripts/Core/Session` |
| Architecture or dependency question | this document, then the generated assembly map and relevant `.asmdef` |
| Package/API version question | `Packages/manifest.json`, `packages-lock.json`, and `docs/deprecations.md` |
| Test placement | the matching assembly under `Tests/EditMode`, `Tests/PlayMode`, or NetCode `PlayModeTests` |

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
  the debug toggle switchboard, and other framework-independent primitives.
- `DigBlocks.Bootstrap` is the Unity composition root. It chooses the launch mode
  and constructs the ordered application services.
- `DigBlocks.Client` owns client-world coordination, GameObject presentation
  bridges, application flow, and client input routing.
- `DigBlocks.Client.UI` owns menu navigation, menu creation, menu views and the
  persistent UI root. It is client presentation only and holds no session,
  simulation or transport knowledge.
- `DigBlocks.Server` owns authoritative server-world coordination.
- `DigBlocks.Networking` owns transport-independent session and protocol
  contracts.
- `DigBlocks.Networking.NetCode` owns Netcode for Entities and Unity Transport
  integration. Transport-specific types should not leak into Core, voxel storage,
  or save data.
- Future shared simulation assemblies will own ECS components and deterministic
  systems used by both client and server worlds. They may depend on
  `Unity.Entities` without depending on `Unity.NetCode`.

`ClientRuntime` and `ServerRuntime` coordinate lifecycle around the native
session. `NetCodeSession` and `NetCodeWorldFactory` create, expose and dispose
the separate ECS worlds. Per-entity gameplay logic belongs in ECS systems.

`GameSessionController` owns one session lifetime at a time and holds the session
`GameHost`; application-lifetime services stay in Bootstrap. A client's session is
created on the play action and disposed on returning to the title screen, so a
session is never assumed to exist while a menu is on screen.

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

The approved [chunk data design](chunk-data-architecture.md) records the 32-cube,
paletted-channel and independent-fluid direction. The companion
[chunk networking design](chunk-networking-design.md) now uses the approved
independent Unity Transport companion connection. Storage, registry, codecs,
carrier, bounded transfer components, world residency and admitted companion
binding, bounded interest streaming, atomic client replica publication and applied
revision recovery are implemented. The [streaming summary](chunk-streaming-implementation.md)
records current resource limits, verification and the meshing boundary. See [residency and binding](chunk-residency-binding.md) and the
[implementation progress](chunk-implementation-progress.md) and
[transfer protocol](chunk-transfer-protocol.md) for current contracts and evidence.
The [meshing readiness guide](chunk-meshing-readiness.md) describes the remaining
foundation work and the decisions to settle before visualization.

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
lifecycle seam. The Netcode adapter configures and connects the client and
server worlds supplied by the runtime services without exposing Unity transport
details to the rest of the application.

Service registration follows dependency
order and shutdown occurs in reverse:

- Single-player: diagnostics, server world, client world, combined Netcode
  session.
- Remote client: diagnostics, client world, Netcode client session.
- Dedicated server: diagnostics, server world, Netcode server session.

The implemented foundation uses native connection approval, persistent unverified
offline XUIDs, and server-owned capacity reservations. Singleplayer uses private
IPC; remote clients and dedicated servers use UDP. Admission ends at
`AwaitingWorldData`, without `NetworkStreamInGame`. See
[the network foundation guide](network-session-foundation.md) for options,
ownership, failure handling and the next integration boundary.

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

1. Core bootstrap and service/world lifetime are implemented.
2. The network admission/session foundation is implemented; see its verification
   record for tested scenarios and remaining platform limitations.
3. The client UI, menu navigation, application flow and session lifetime
   foundation is implemented; see `docs/ui-menu-foundation.md`.
4. Design and implement the transport-independent three-dimensional chunk data
   model.
5. Add chunk interest, snapshot, delta, and transmission systems.
6. Add client meshing and rendering.
7. Add dynamic entity simulation and ghost authoring incrementally.

This sequence deliberately establishes connection and world ownership before
chunk transmission while keeping the chunk model reusable in tests, persistence,
and offline tools.

## Terrain presentation

The client meshing and indirect rendering milestone is documented in [chunk meshing and terrain rendering](chunk-meshing-rendering.md), with the later conservative portal-culling work recorded in the [terrain chunk occlusion implementation summary](terrain-chunk-occlusion-summary.md), per-camera submission in the [terrain secondary camera implementation summary](terrain-secondary-camera-summary.md), and the moving residency path in the [dynamic chunk loading implementation summary](dynamic-chunk-loading.md). `DigBlocks.Voxels.Meshing` owns Burst greedy geometry, packed quad contracts and revision-matched chunk face connectivity; `DigBlocks.Client.Rendering` owns scheduling, camera graph traversal, GPU memory, shaders, the inspection camera, and which cameras terrain is submitted to. Replica publication/removal/reset notifications originate in Voxels.Runtime. Bootstrap composes presentation only for graphical clients and supplies a bounded authoritative development fixture/Perlin source through ordinary replication.
