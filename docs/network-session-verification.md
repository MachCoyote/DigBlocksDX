# Network foundation verification — 2026-09-05

Environment: Windows editor 6000.6.0f1, Entities 6.6.0, NetCode for Entities 6.6.0.
Tests run in the user's open editor through Unity MCP. The Unity CLI is installed,
but no Pipeline-connected editor was available. MCP job initialization occasionally
lagged compilation/reload; only completed test XML and completed jobs count below.

## Baseline and regressions

The starting baseline had 32/33 EditMode tests passing: composition still expected
three services instead of the user's four-service singleplayer setup. The original
PlayMode smoke handshake reached readiness but failed during cleanup because an
EntityQuery outlived its world. The implementation preserves the working smoke-test
flow and corrects both lifecycle/test assumptions.

Observed red tests included invalid addresses, admission policy rejection/capacity,
identity persistence/corruption, CLI parsing, missing remote/server session
composition, server-build mode restrictions, native approval, wrong-direction RPC
cleanup, and stopping on the exact admission tick. The last regression also exposed
UniTask.Preserve's limitation with concurrent in-flight awaiters; shared stop
completion now uses UniTaskCompletionSource.

## Automated results

| Suite | Passed | Failed | Completed (UTC) |
| --- | ---: | ---: | --- |
| Full EditMode | 50 | 0 | 2026-09-05 06:47:41 |
| Full PlayMode | 12 | 0 | 2026-09-05 06:48:54 |

Local evidence: `.utmp/editmode-results.xml`, `.utmp/playmode-results.xml` (ignored
verification outputs). Corresponding completed MCP jobs:
`55b306b7066444f6b1e4883a878f1e48` and
`340c2c0060184a58ac0abcff701f1d95`.

PlayMode covers:

- Real scene/bootstrap startup, duplicate bootstrap prevention, bounded teardown.
- IPC approval, no gameplay activation, and cleanup of schema-valid wrong-direction
  RPCs sent over the actual connection.
- UDP protocol mismatch, duplicate identity, capacity rejection, keeping the first
  client healthy, kick, slot release, fresh-host rejoin, and server shutdown.
- Two concurrently starting clients contending for one slot.
- 32 actual UDP client worlds admitted, followed by rejection of client 33.
- Timeout, cancellation before startup, cancellation after the connect request,
  and world cleanup.
- Stopping on the admission tick before the startup continuation resumes.
- Concurrent bootstrap shutdown awaiters sharing the same cleanup operation.
- Repeated runtime start protection and cleanup with a cancelled stop token.

The 32-peer case is an admission/capacity check, **not** a mob/ghost/chunk throughput
benchmark. These tests do not construct a fake ready marker or globally suppress
unexpected logs. A test-only ECS system exercises the stop/admission ordering race.

The final editor run also emitted native NetCode tick-batching diagnostics while
starting/bootstrap-testing worlds. These are retained in the console, not suppressed.
Fresh compilation/console inspection found no C# compiler errors or managed-component
deprecation warnings. A networking correctness pass is not a claim of a warning-free
editor or measured steady-state gameplay performance.

## Standalone verification

Both Windows x64 strict development builds succeeded and the separate-process
checks passed. Players ran headlessly for these networking checks, not rendering QA.
Afterward, a diagnostic-only correction removed the doubled port from the listen
log message; the final full test runs above include that correction.

Client: final build succeeded at 2026-09-05 06:43:17 UTC (114.31 seconds, zero
errors, four UnityGLTF shader-node warnings as described below). Build job
`build-c0be13c8cb`; full report:
`Library/BuildHistory/20260905-064123Z-9bcdefe0/9bcdefe0917e5e4488d8ceb34a7feabf.buildreport`.

Dedicated server: strict Windows x64 development build succeeded at
2026-09-05 06:41:05 UTC (147.29 seconds, zero errors). Build job
`build-c2ffa97a81`; full report:
`Library/BuildHistory/20260905-063837Z-ce23f124/ce23f124224964f4598c8481d674da54.buildreport`.
Its four warnings are existing UnityGLTF shader graphs with newer node versions
available (`PBRGraph` and three legacy override graphs), not networking compiler
warnings. No third-party shaders were changed for this task.

Separate processes verified an actual dedicated-server build and ordinary client
build communicating over UDP with an OS-assigned port and distinct profile files:

- Dedicated server reported Listening and completed server-mode startup.
- Alice was admitted as peer 1.
- A second client claiming Alice's identity was rejected as DuplicateIdentity.
- Bob was rejected as ServerFull with the configured cap of one.
- A second server on the occupied port reported ListenFailed and rolled back.
- Terminating Alice's process caused native disconnect/timeout cleanup and released
  the reservation; Bob then joined as peer 2 without restarting the server.

Evidence: `.utmp/players-a120e8aa39404c22a92df283721e436c/*.log` and the ignored
verification script `.utmp/verify-players.ps1`. All processes launched for this
check were terminated afterwards; the user's editor was not closed. Graceful
shutdown/kick reasons are covered by PlayMode; the process test deliberately covers
an abrupt client exit.

The standalone server also emitted NetCode sleep-mode tick-count warnings during
the run (`NetcodeServerRateManager.WillUpdateInternal`: expected one step but ran
zero). This code does not set `Application.targetFrameRate`. Admission, cleanup and
rejoin succeeded despite those warnings. The native timing diagnostic remains a
known profiling/package-follow-up item; it is not hidden or described as a clean
runtime log. Do not reinstate busy-waiting merely to silence it.

The initial player build succeeded but exposed four Entities 6.6 deprecation
warnings for managed-component access. The implementation was changed to a managed
adapter system with an unmanaged active marker, then the complete PlayMode suite
was rerun successfully. Final build results below supersede that initial build.

## Remaining validation boundaries

- Other operating systems, IPv6, DNS resolution and browser platforms are untested
  or not implemented in this IPv4 desktop slice.
- No account authentication, encryption, hostile-Internet load test, or
  pre-admission connection-flood protection is claimed.
- No packet-loss/latency benchmark, ghost traffic, chunks or gameplay simulation is
  included. Close-reason RPC delivery before disconnection remains best-effort.
- Invalid names/identity values and duplicate policy calls are unit-tested; the
  integration suite is not an exhaustive packet-fuzzing suite.
