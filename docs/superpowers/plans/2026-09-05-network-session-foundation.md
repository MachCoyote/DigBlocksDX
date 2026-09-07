# Network Session Foundation Implementation Plan

**Goal:** Implement the approved admission/session foundation through awaiting world data.
**Architecture:** Core retains application lifecycle. Networking owns settings, offline identity,
and admission rules; the NetCode adapter owns RPCs, connection entities, and session monitoring.
**Tech stack:** Unity 6.6, Entities/NetCode 6.6.0, Unity Transport, UniTask, NUnit.
**Spec:** ../specs/2026-09-05-network-session-foundation-design.md

## Constraints

Preserve the user's working smoke-test baseline. Use Netcode for Entities exclusively.
No chunk formats, registry layouts, world streaming, or NetworkStreamInGame activation.
Keep singleplayer private, support 32 admitted clients by default, and keep account
authentication outside this slice. Use CRLF and concise comments. Execute in this
session on feature/network-session-foundation; do not commit unrelated user work.

## 1. Baseline and contracts

- [x] Record fresh baseline compiler/tests, including outdated scaffold assertions.
- [x] Add behavior tests for invalid endpoints/options and duplicate world startup.
- [x] Implement validated NetworkSessionOptions, OfflineIdentityStore, NetworkFailure,
  PeerRecord, and AdmissionRegistry. Tests cover identity persistence/corruption and
  capacity/duplicate rejection using real file storage and policy calls.
- [x] Define fixed-width, bounded RPC fields: protocol, nonce, offline ID, name,
  assigned peer ID, and response reason. Use the installed source-generator API.

## 2. Admission and lifecycle

- [x] Add a real-world integration test requiring server admission and client result.
- [x] Replace hello with IApprovalRpcCommand admission and an acceptance response.
  Capture tick-scoped connection events inside SimulationSystemGroup; expose durable
  status and server peer data to NetCodeSession. Reserve slots synchronously when
  deciding approval; consume all request entities, including invalid/duplicate ones.
- [x] Implement NetCodeSession startup, bounded failure paths, monitoring, typed
  outcomes, kick/disconnect, and idempotent stop. Clean partial starts before throwing.
- [x] Guard runtime world ownership against repeat start and cancelled cleanup.
- [x] Test mismatches, duplicate IDs, capacity races, wrong-direction RPC cleanup,
  disconnect slot release, and fresh join using real RPC traffic. Malformed names
  and duplicate policy calls are covered by portable policy tests.

## 3. Application integration

- [x] Parse address, bind address, port, name, capacity, and identity-file CLI options;
  reject missing/duplicate/invalid values. Keep Unity's unrelated flags ignored.
- [x] Compose sessions for all three roles with injected persistent identities.
  Composition stays side-effect free; identity load/create happens during service start.
- [x] Expose session status from the Unity bootstrap. Exercise explicit host stop
  and fresh-host rejoin without scene reload in tests; menu session switching is
  deferred. Keep application shutdown ordering intact.
- [x] Update existing composition and bootstrap tests for the asynchronous lifecycle.

## 4. Verification and handoff

- [x] Run full EditMode/PlayMode suites with fresh compiler and console inspection.
- [x] Test 32 clients and enforce the configured boundary independently of transport IDs.
- [x] Build client/server players and verify actual UDP admission/rejection between
  processes, with distinct identity files and explicit ports. Inspect result logs.
- [x] Document entry points, state transitions, defaults, identity semantics, research,
  test evidence, and the precise world-data integration boundary. Review the final diff.

## Representative acceptance assertions

```csharp
Assert.That(clientSession.State, Is.EqualTo(NetworkSessionState.AwaitingWorldData));
Assert.That(serverSession.Peers.Count, Is.EqualTo(1));
Assert.That(serverSession.Peers[0].OfflineXuid, Is.EqualTo(expectedOfflineId));
Assert.That(inGameQuery.CalculateEntityCount(), Is.Zero);
```

```csharp
//the rejected StartAsync throws NetworkSessionException with Reason = ServerFull.
Assert.That(serverSession.Peers.Count, Is.EqualTo(configuredCapacity));
```

The assertions describe consumer-visible outcomes; test bodies must drive actual
production systems. Do not construct a fake ready marker or suppress unexpected logs
to satisfy them. Each task's exact signatures are recorded with its implementation.
