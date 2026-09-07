# Network session foundation

This slice owns connection admission and lifetime. It deliberately stops at
`AwaitingWorldData`: no player ghosts or `NetworkStreamInGame` are created.
The session also starts the [chunk companion service](chunk-residency-binding.md),
which owns separate stores, pins one server dummy chunk and waits for binding.
Dedicated servers can set `--bulk-port` (default game port + 1); clients use the
advertised port, and singleplayer uses IPC. Inspect
`DigBlocksBootstrap.ChunkCompanion.ClientState` for binding progress.
A running `GameHost` means its services started,
not that a playable world exists. Check `DigBlocksBootstrap.NetworkSession.State`
for current connection status; it continues to change after startup.

A client composes these services when the player chooses play rather than at
launch, and disposes them on returning to the title screen; a dedicated server
still starts its session directly. See the
[UI and menu foundation](ui-menu-foundation.md) for session lifetime, the explicit
world-ready gate that consumes `AwaitingWorldData`, and the accessors above.

## Run it

The SampleScene bootstrap composes all modes. The ordinary player build defaults
to singleplayer; a dedicated server build defaults to server mode and rejects
client-mode flags.

```text
DigBlocks.exe --singleplayer --name Alice
DigBlocksServer.exe --server --bind 0.0.0.0 --port 7979 --capacity 32
DigBlocks.exe --client --address 192.168.1.10 --port 7979 --name Alice
```

Use IPv4 literals for this slice. DNS names, IPv6, Relay/NAT traversal, server lists,
and automatic reconnect are not implemented. A LAN server needs its UDP port
allowed through the host firewall; this implementation does not change firewall
rules. Test clients on the same machine must use different identity files:

```text
DigBlocks.exe --client --address 127.0.0.1 --name Alice --identity-file "D:\TestProfiles\alice.dat"
DigBlocks.exe --client --address 127.0.0.1 --name Bob --identity-file "D:\TestProfiles\bob.dat"
```

Options use separate flag/value arguments. Missing, repeated, and invalid recognized
options fail before world creation. Unity's unrelated command-line options are
ignored. Names use 1–16 ASCII letters, digits, or underscores. Capacity defaults to
32 and accepts 1–1024; the upper bound is a configuration guard, **not a supported
gameplay concurrency claim**. Direct ports must be 1–65535. Programmatic server
options may use port zero to request an ephemeral test endpoint.

Singleplayer uses IPC-only drivers and a dynamically assigned private port, with
separate authoritative server and client worlds. It creates no UDP listener.
Endpoint flags are rejected in singleplayer. Remote/dedicated worlds explicitly
use UDP, including tests that put both worlds in one process, avoiding NetCode's
automatic IPC selection. Public-Internet deployment is outside this development
slice: identities are unauthenticated and no game-level encryption, account tickets,
ban system, or pre-admission DoS protection is provided.

## Identity and authority

`OfflineIdentityStore` creates a nonzero random 64-bit value using the platform's
cryptographic random generator, then publishes it atomically as 16 hexadecimal
characters in `Application.persistentDataPath/uid.dat`. It is persistent across
restarts and display-name changes. The format does not depend on Windows APIs or
machine identifiers; platform-specific filesystem locations are supplied by Unity.
Concurrent first launches reuse the winning file. Corrupt existing identities fail
explicitly rather than silently changing the player's identity. Back up this file;
deleting or replacing it creates a different identity. Cross-device continuity
requires transferring the file; there is no cloud identity sync.

Three identifiers have different meanings:

| Identifier | Owner | Lifetime / purpose |
| --- | --- | --- |
| Offline XUID | Client profile | Persistent, **unverified claim**, not an Xbox-issued ID |
| Peer ID | Server admission registry | Monotonically assigned within one server session |
| NetCode NetworkId | NetCode | Transport connection / ghost ownership integration later |

Duplicate active offline identities are rejected. Names are presentation labels,
not authentication or durable keys. Anyone able to edit their client can claim
another offline ID; duplicate detection does not prevent impersonation. Do not use
this trust model for privileged commands or valuable persistent player ownership.
Future account authentication must verify credentials **before** reserving a peer
and adding `ConnectionApproved`; map verified provider identities to game profiles
at that boundary. No account provider is selected in this slice.

## Connection flow and failure behavior

1. Runtime services create worlds; the session enables native connection approval
   before listening or connecting. Server bind succeeds synchronously before
   `Listening` is reported. Startup is bounded to 10 seconds by default.
2. NetCode checks its own RPC/component compatibility. The client then sends an
   `IApprovalRpcCommand` hello with application protocol version, nonce, offline
   XUID, and bounded display name. Application protocol is currently version 1.
3. The server checks protocol, identity, name, duplicate identity and capacity.
   A slot is reserved synchronously, including approved connections that have not
   finished native connection setup. Repeated hellos cannot allocate extra slots.
4. The response is targeted to its source connection. On acceptance the server
   adds `ConnectionApproved`. The client validates the response and waits for both
   acceptance and native `NetworkId` before reporting `AwaitingWorldData`.
5. ECS systems consume tick-scoped connection events into durable session state.
   Disconnect removes the server reservation and the client's admission marker;
   it cannot leave a stale ready state. One rejected peer does not fault the server.

The small admission policy/registry and adapter context are managed control-plane
objects, run on the main thread. `SessionContextSystem` owns the adapter context;
an unmanaged `SessionActive` marker gates its protocol systems. This avoids the
managed-component access APIs deprecated in Entities 6.6. These objects do not run
in per-mob jobs or voxel simulation.
RPC fields are fixed-width/bounded. This is not the future bulk-data channel.

`INetworkSession` exposes role, state, failure, the last native transport disconnect
reason (diagnostic string when available), and read-only server peer snapshots.
`NetCodeSession` additionally exposes `LocalPeerId`, `ListeningPort`, and
`Kick(peerId)`. The peer list includes reservations, not just fully connected peers.
`NetworkSessionException.Reason` preserves typed startup failures. Cancellation
still propagates as `OperationCanceledException`; `LastFailure` records it.
Unexpected filesystem/factory errors retain their original exception.

Client states progress through `Starting` → `Connecting` → `Approving` →
`AwaitingWorldData`. A failed startup ends `Faulted`; a lost admitted connection
ends `Disconnected`. Explicit teardown goes through `Stopping` → `Stopped`.
Servers remain `Listening` while individual peers join or leave.

Rejections and server close/kick reasons receive a short 250 ms send grace. Reliable
RPC delivery is best-effort before closure, not guaranteed: loss or process death
may leave only a transport reason or timeout. The client never trusts a close or
hello response from an unrelated connection. NetCode owns reliable retransmission
and native handshake/connection timeouts; no custom heartbeat is added.

Shutdown ignores caller cancellation for resource cleanup, requests disconnects,
and drains for at most two seconds by default. Runtime services then remove worlds
from the player loop and dispose their drivers, releasing listeners. Always stop
the **host**, not only the network service, to release world-owned sockets. A
partially started session cleans its own state before host rollback disposes the
worlds. Stop is idempotent; a session is one-shot. Manual rejoin composes a fresh
`GameHost`, worlds and session, as exercised by the integration tests. Menu-driven
session switching is owned by the [UI and menu foundation](ui-menu-foundation.md).

The existing 30 Hz simulation/network rates remain a starting value; forced
busy-waiting has been removed. Tune tick rates against real gameplay profiling.

## Where to extend next

- `Scripts/Networking`: validated options, launch parsing, identity storage,
  portable peer records and admission policy; no NetCode dependency.
- `Scripts/Networking/NetCode/Protocol`: native approval RPC fields and world-local
  adapter state. Bump protocol when changing application semantics; native NetCode
  also validates generated RPC/component layouts.
- `Scripts/Networking/NetCode/Systems`: approval, response validation, and durable
  disconnect monitoring. Keep admission out of hot simulation systems.
- `Scripts/Networking/NetCode/Runtime`: transport choice, world creation, session
  start/stop and server kick entry point.
- `Scripts/Bootstrap/GameServiceComposer.cs`: diagnostics → server world → client
  world → session (omit worlds unused by the selected role). Stop reverses this.
- `Scripts/Bootstrap/Session/GameSessionController.cs`: session creation, the
  explicit readiness wait, teardown, and rejection of overlapping operations.

The next design discussion begins at `AwaitingWorldData`. Decide the 3D chunk
coordinates, ownership/storage, registry compatibility, snapshot/delta formats,
interest and bandwidth budgets together. Only after world synchronization and
ghost prerequisites are satisfied should a later system add `NetworkStreamInGame`.
Do not make admission itself allocate chunks or spawn a player.

## Research and tradeoffs

- [Mojang Java 1.20.2 notes](https://feedback.minecraft.net/hc/en-us/articles/19703470383757-Minecraft-Java-Edition-1-20-2)
  describe a configuration phase between login and play. We use the same separation
  of admission from world readiness, not Minecraft's wire protocol or TCP transport.
  This is a modern reference, not a claim about the 1.5–1.10 era.
- [NeoLegacy XUID source snapshot](https://git.minecraftlegacy.com/backups/neoLegacy/src/commit/ed9cbae3f7cdcb8596b508f30075670e728655c6/Minecraft.Client/Windows64/Windows64_Xuid.h)
  provides the generate-once/persist identity precedent. Our implementation uses
  independent platform RNG and file code, not Windows seed mixing or a name hash.
- [Factorio's centralized multiplayer discussion](https://www.factorio.com/blog/post/fff-147)
  supports keeping membership decisions with one server. Its
  [deterministic lockstep model](https://www.factorio.com/blog/post/fff-302) has
  different simulation costs; we retain authoritative ECS and eventual ghost
  snapshots instead of adopting whole-world lockstep.
- [Steam authentication documentation](https://partner.steamgames.com/doc/features/auth)
  illustrates why a claimed identifier and verified account ticket are distinct.
  We preserve that boundary without committing to Steam or another provider now.
- [Unity connection documentation](https://docs.unity3d.com/Packages/com.unity.netcode@1.10/manual/network-connection.html)
  describes the native approval flow. The installed NetCode **6.6.0** sources are
  the API authority for this implementation, including approval RPCs, tick-scoped
  events and driver construction.

## Verification

Run Unity Test Runner EditMode and PlayMode suites. The installed CLI can launch
batch tests when the project is not already open; the open editor was tested through
Unity MCP. Test results and limitations are recorded in
[network-session-verification.md](network-session-verification.md).

Policy tests exercise real admission and filesystem operations. Integration tests
run actual IPC/UDP client/server ECS worlds, enforce the cap, reject incompatible
clients, kick/rejoin peers, observe server shutdown and bound failed startup.
The 32-peer test is an admission test only; it proves neither entity-simulation
throughput nor chunk-streaming bandwidth.
