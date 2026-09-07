# Network Session Foundation

Status: approved on 2026-09-05. The user approved direct IP/LAN and the foundation
boundary, with account authentication deferred and NeoLegacy-style persistent
offline XUIDs requested. Implementation evidence is recorded separately.

## Scope and stopping point

Turn the existing hello smoke test into the connection/session foundation for
single-player, remote clients, and dedicated servers. The slice ends with a peer
admitted by the server and waiting for world data. Do not choose chunk dimensions,
storage, serialization, registry representation, chunk interest rules, bulk transport,
or world-loading completion criteria here. Do not enable NetworkStreamInGame or
spawn a player ghost merely to make the connection look complete.

Approved scope: direct IPv4/IP-LAN connection settings; server approval;
application protocol compatibility; bounded display-name validation; configurable
capacity with a default of 32; per-connection state and server-owned session peer IDs;
structured rejection/disconnection outcomes; startup cancellation and rollback;
bounded shutdown; continued monitoring after startup; and manual leave/rejoin through
fresh session lifetimes. Automatic reconnect, session resumption, server browsing,
account-provider integration, Relay, and lobby services are separate slices.

## Research and trade-offs

### Minecraft

Mojang's Java 1.20.2 release notes describe a configuration phase between account
login and play. Data-driven registries and features are configured there; clients
can remain outside the world while configuration proceeds. This is a newer design
than the project's 1.5-1.10 gameplay inspiration. Adopt the separation of admission
from world readiness, without copying Minecraft's packet format or assigning a
configuration payload before our data model exists.

The same release notes discuss batching TCP gameplay packets and pacing chunk
batches to available bandwidth. This supports keeping connection readiness separate
from loading progress; it does not establish that TCP or Minecraft's chunk format
is appropriate for DigBlocks.

Source: [Mojang Java 1.20.2 release notes, Network Protocol and Network Optimizations](https://feedback.minecraft.net/hc/en-us/articles/19703470383757-Minecraft-Java-Edition-1-20-2).

### Factorio

Factorio's multiplayer rewrite describes the complexity of peer agreement on
join/leave events and the benefit of central server authority. Its later networking
article describes deterministic simulation of shared game state on clients, with
separate latency state. Adopt central ownership of membership and isolate a client's
connection failure from other clients. Do not adopt deterministic whole-world
lockstep: our approved architecture uses authoritative simulation and ghost snapshots.

Sources: [Friday Facts 147](https://www.factorio.com/blog/post/fff-147),
[Friday Facts 302](https://www.factorio.com/blog/post/fff-302).

### Steam-supported games

Steamworks distinguishes a claimed user identity from a validated session ticket.
Ticket validation involves the platform backend and has its own lifecycle. Therefore
an offline display name or session peer ID must not be described as an authenticated
account or used as a persistent save identity. Integrating Steam or another account
provider now would require a user choice about distribution and backend ownership.

Source: [Steamworks user authentication and ownership](https://partner.steamgames.com/doc/features/auth).

### Unity Netcode for Entities

Use the installed 6.6.0 package source and bundled documentation as the API authority.
Its approval flow uses IApprovalRpcCommand, RequireConnectionApproval on both worlds,
and ConnectionApproved on the server connection entity. NetworkId follows approval.
NetworkStreamInGame separately gates commands and snapshots. Connection events are
valid for a simulation tick and must be captured inside SimulationSystemGroup, not
polled from a MonoBehaviour or asynchronous application loop.

The installed default driver can choose IPC for an in-process client. A passing
single-process test does not establish UDP or standalone build correctness.

References: `com.unity.netcode` 6.6.0, bundled `Documentation~/network-connection.md`,
`Runtime/Connection/DefaultDriverConstructor.cs`, and
[public Unity connection documentation](https://docs.unity3d.com/Packages/com.unity.netcode@1.10/manual/network-connection.html)
(the public link is version 1.10; verify signatures against the installed package).

### Alternatives considered

1. Continue ordinary hello RPCs after connection and implement our own admission
   phase. Smallest change, but duplicates the approval boundary NetCode provides.
2. Use native NetCode approval, then an explicit admitted/awaiting-world state.
   Recommended: one authority for admission, a bounded lifecycle, and room for future
   authentication and world configuration without starting gameplay prematurely.
3. Integrate platform authentication/lobbies now. Useful for an online launch, but
   couples this slice to a provider and expands operational and testing requirements.

## Proposed behavior

### Identity and admission

Use explicitly unauthenticated development identities in this slice. Generate a
random nonzero 64-bit offline XUID once and persist it as fixed-width hex text under
Application.persistentDataPath, with a configurable identity file for local clients.
Use portable .NET randomness and encoding. Fail on corrupt existing identity files
rather than silently replacing identities. Reject duplicate active XUIDs; names are
independent of identity. These are DigBlocks IDs, not Microsoft-issued Xbox XUIDs.
Copying a file can impersonate its identity. Account integration will need an
explicit provider namespace and migration policy.

The server also assigns a session-scoped peer ID. NetworkId remains a transport
connection identifier; neither transient ID is a persistent save identifier.
No credentials are sent by the development handshake. Display names are bounded
and validated on both sides; the server remains the authority on acceptance.

Admission checks application protocol version, name validity, shutdown state, and
capacity. Capacity counts reservations made during approval as well as admitted
peers, preventing two same-tick requests from claiming the final slot. Disconnects
and failed approvals release reservations. Repeated requests are consumed without
allocating repeated slots or producing unbounded response traffic.

Native NetCode protocol/RPC schema compatibility remains in force. Application
version compatibility is an additional check, not a promise that arbitrary builds
or modded RPC schemas can communicate. Content-registry compatibility is postponed
until the content model is designed.

### Session lifecycle

Client progress distinguishes connecting, approving, awaiting world data, and a
terminal failure/disconnection. Server listening state is distinct from peer state.
A successful bind is required before reporting listening. A dedicated server starts
without waiting for clients and survives individual client failures.

Application code observes copied session status and peer snapshots, without holding
Unity Entity handles. ECS systems own per-connection state. Capture transport events
in simulation and retain the relevant results for managed lifecycle/diagnostics.
Disconnection must invalidate readiness; a stale singleton must not make a new
attempt appear successful.

Start operations validate options and world ownership, reject overlapping lifecycle
operations, and have bounded deadlines. A partially started session cleans its own
resources because GameHost only rolls back services whose startup returned. Stop
is idempotent, requests disconnection, and waits a bounded interval while worlds
still tick. Runtime services dispose their owned worlds afterwards, including after
cancellation. New attempts use fresh session/world lifetimes; no automatic retry.

### Launch modes and settings

All three launch modes construct a NetCodeSession. Remote clients honor the configured
address. Dedicated servers honor an explicit bind address and port. Single-player is
private by default; opening it to LAN is an explicit future operation, not a side
effect of binding every interface. Avoid fixed-port contention for private sessions.

Validate addresses, port ranges, capacity, and deadlines before allocating worlds.
Keep transport endpoint types inside the adapter. Support explicit command-line
overrides and documented defaults. Preserve background ticking. Keep the current
30 Hz simulation/network rates as a starting setting, not a performance guarantee;
avoid BusyWait as an unconditional CPU policy. Gameplay tick-rate tuning is deferred.

### Errors, diagnostics, and resource bounds

Expose application rejection reasons and transport disconnect reasons separately.
Do not guarantee delivery of a last message over a severed transport. If a rejection
explanation cannot be delivered, retain a useful fallback outcome and server log.
Include session role and phase in diagnostics; do not log every frame or secret tokens.
Bound admission deadlines and pending application work. The 32-player admission cap
is not a claim of volumetric denial-of-service protection or measured gameplay capacity.
Use the transport's connection health mechanisms; add no redundant ping RPC loop.

## Verification requirements

- Unit tests for settings parsing, validation, admission decisions, capacity
  reservations, duplicate requests, and identifier lifetime semantics.
- Composition tests for every launch role; constructor/composition work must not
  create or connect ECS worlds.
- Integration tests with real NetCode worlds and real RPC exchange. Assert server
  admission and client acceptance, peer IDs, no NetworkStreamInGame, and cleanup.
- Multiple simultaneous clients, last-slot contention, rejection while other clients
  remain healthy, disconnect releasing a slot, and a subsequent fresh join.
- Mismatched application versions, invalid names, unanswered approval, duplicate or
  stale RPCs, and transport failure during/after startup.
- Cancellation, occupied port, all listen-result states, rollback, repeated stop,
  and disposal with no surviving project-owned connection/configuration entities.
- Update preexisting bootstrap tests to wait for bounded asynchronous startup and
  clean up their sessions; do not weaken them or globally ignore unexpected logs.
- A real UDP test that cannot silently select IPC, plus a separate server/client
  player-build check. Capture fresh compiler output and complete test result files.
- Use 32 lightweight connections as an admission soak when feasible; do not describe
  that as a chunk-streaming, mob-simulation, or bandwidth benchmark.

## Implementation organization

Retain Core's framework-independent lifecycle and the runtime-owned world factories.
Keep protocol data/contracts and admission rules in DigBlocks.Networking; Unity RPC
components, ECS connection tracking, and transport translation stay in the NetCode
adapter. Split NetCodeSession into focused lifecycle/connection support where needed.
Use UniTask and concise explanatory comments at ownership/order-sensitive code.

Document final lifecycle states, settings, entry points, test commands, and the
handoff to world loading. The user approved this boundary and offline persistent
XUID direction. Implementation/test evidence is recorded separately.

## Current baseline observations

The user's hello RPCs, runtime factories, session, and smoke test are present as
uncommitted work and form the implementation baseline. Single-player composes the
session; other roles currently compose only worlds. The client endpoint is hardcoded
to loopback. The server echoes hello but has no admission/capacity model. Readiness
is stored as a world singleton and not invalidated after disconnection.

The listen loop handles Pending/Succeeded/Failed but not refusal states, and
DestroyEntity alone does not remove NetworkStreamRequestListenResult because it is
a cleanup component. These are concrete lifecycle cases for the implementation tests.
Existing composition/bootstrap tests still express the earlier scaffold expectations.
These are inspection findings, not a claim that the current full suite has been run.

The user enabled Unity MCP and editor compilation/tests are available through its
local HTTP endpoint. The installed CLI does not currently have a Pipeline-connected
editor. Use MCP for this open project rather than opening a conflicting batch editor.

## Implementation refinements

The adapter uses synchronous native `NetworkStreamDriver.Listen`, eliminating the
old asynchronous listen-request result/cleanup state machine. Bind errors surface
before reporting Listening. World owners dispose listeners after bounded session
draining. The approved slice does not add a menu-level session-switching UI; fresh
host/session rejoin is tested programmatically. See `docs/network-session-foundation.md`
for the implemented contracts and `docs/network-session-verification.md` for evidence.
