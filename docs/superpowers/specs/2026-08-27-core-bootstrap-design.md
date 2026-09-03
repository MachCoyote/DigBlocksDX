# DigBlocks Core Bootstrap Design

> This document records the original Core/bootstrap milestone. The current
> project-wide direction is maintained in [architecture.md](../../architecture.md).

## Status

Approved in conversation for implementation as the first project milestone.

## Scope

This milestone establishes only the application bootstrap and service lifecycle used by later client and server systems. It does not integrate a networking framework, create client/server simulation runtimes, store voxel sections, generate terrain, transmit world data, or render meshes.

The result is a small, tested foundation that later milestones can extend without changing how the application starts and stops.

## Goals

- Provide one explicit Unity composition root for constructing the application.
- Represent the intended launch modes: single-player, remote client, and dedicated server.
- Start services in a deterministic order and stop them in reverse order.
- Support asynchronous initialization and shutdown with cancellation.
- Roll back already-started services when a later service fails to start.
- Keep the lifecycle implementation independent of scenes, networking, rendering, and voxel storage.
- Avoid global mutable service locators and hidden dependency discovery.
- Make lifecycle behavior verifiable in EditMode tests without entering Play Mode.

## Non-goals

- Dependency-injection framework or automatic dependency resolution.
- Runtime service addition or removal.
- Restarting the same host after shutdown.
- Networking, connection management, or endpoint configuration.
- Fixed-tick simulation scheduling.
- Block registries, mod loading, data packs, or content discovery.
- Save-game discovery or world selection.
- Loading gameplay scenes.
- Progress UI beyond exposing observable bootstrap state and failures.

## Project Layout

The milestone adds these assemblies under `Assets/_Project/Scripts`:

```text
Core/
    DigBlocks.Core.asmdef
    Hosting/
    Launch/

Bootstrap/
    DigBlocks.Bootstrap.asmdef
    DigBlocksBootstrap.cs
    UnityGameLogger.cs

Tests/EditMode/Core/
    DigBlocks.Core.Tests.asmdef
    Hosting/
    Launch/
```

`DigBlocks.Core` does not reference `UnityEngine`. It may use the .NET base class library. This keeps lifecycle tests fast and permits the same hosting code to be used by client and dedicated-server builds.

`DigBlocks.Bootstrap` references `DigBlocks.Core` and `UnityEngine`. It is the only composition root in this milestone.

`DigBlocks.Core.Tests` references `DigBlocks.Core` and Unity Test Framework assemblies. Tests target the public behavior of the host and launch parser.

Later assemblies described in the broader architecture are not created until their milestone begins. Empty assemblies and placeholder subsystems are deliberately avoided.

## Core Contracts

### `IGameService`

An application service exposes:

- A diagnostic name.
- `StartAsync(CancellationToken)`, returning a `UniTask`.
- `StopAsync(CancellationToken)`, returning a `UniTask`.

Startup and shutdown use the project's existing UniTask dependency. Service dependencies are supplied through constructors when the composition root creates each service.

### `IGameLogger`

Core receives a minimal logging abstraction with debug, information, warning, and error operations. The interface accepts an optional exception for error reporting. It does not prescribe formatting, files, or log sinks.

Bootstrap supplies a Unity implementation that writes to the Unity console. Dedicated-server logging can provide a console/file implementation later without changing Core.

### `LaunchMode`

The supported values are:

- `SinglePlayer`
- `RemoteClient`
- `DedicatedServer`

This enum describes which application graph Bootstrap should eventually construct. It contains no networking or world configuration.

### `LaunchOptions`

An immutable value containing the selected `LaunchMode`. It is intentionally small in this milestone. Later milestones can add separate strongly typed option objects rather than turning it into an unstructured property bag.

## `GameHost`

`GameHost` receives an ordered, immutable copy of service instances and an `IGameLogger`. Registration order is dependency order.

The host is single-use and has these externally observable states:

- `Created`
- `Starting`
- `Running`
- `Stopping`
- `Stopped`
- `Faulted`

### Startup

1. `StartAsync` is valid only from `Created`.
2. State changes to `Starting`.
3. Services start sequentially in registration order.
4. Each successful service is recorded.
5. When all services have started, state changes to `Running`.
6. If cancellation or failure occurs, successfully started services are stopped in reverse order using an internal rollback token that is not already cancelled.
7. State changes to `Faulted`.
8. If rollback succeeds, the original startup failure is rethrown unchanged.
9. If rollback also fails, the host throws a `GameHostStartException` whose inner exception is the original startup failure and whose rollback-errors collection contains every cleanup failure. This preserves the primary cause without discarding cleanup evidence.

Sequential startup is intentional: service construction and initialization are not performance-critical, while deterministic dependency behavior is valuable. Independent expensive initialization can be parallelized inside a future coordinating service when justified.

### Shutdown

1. `StopAsync` is valid from `Running` or `Faulted`.
2. Stopping a host in `Created` moves it directly to `Stopped`.
3. State changes to `Stopping`.
4. Started services stop in reverse order.
5. Every service gets a stop attempt even if an earlier stop fails.
6. State changes to `Stopped` after all attempts.
7. If one or more services fail to stop, an aggregate failure is returned after cleanup finishes.
8. Calling `StopAsync` when already `Stopped` succeeds without additional work.

Concurrent start/stop calls are rejected with a clear invalid-operation failure. The Unity composition root serializes normal lifecycle calls, so Core does not add locking for unsupported concurrent control.

## Launch-Mode Resolution

Bootstrap has a serialized default launch mode for Editor and ordinary client builds. Command-line arguments override that default:

- `--singleplayer`
- `--client`
- `--server`

Supplying more than one mode flag is an error. Unknown arguments are ignored by this milestone so Unity and platform-specific command-line arguments can coexist.

When compiled with `UNITY_SERVER`, `DedicatedServer` is the default unless an explicit mode flag is supplied. A later dedicated-server milestone may choose to reject client modes in server builds; this milestone only resolves intent.

The launch parser is implemented in Core as a pure function and tested without Unity APIs. Bootstrap obtains arguments from `Environment.GetCommandLineArgs()`.

## Unity Composition Root

`DigBlocksBootstrap` is a scene `MonoBehaviour` and the only application entry point in this milestone.

Responsibilities:

1. Enforce a single active bootstrap instance.
2. Persist across scene loads.
3. Resolve launch options from the serialized default and command-line arguments.
4. Create the Unity logger.
5. Construct the ordered service list explicitly.
6. Create and start `GameHost`.
7. Expose current host state and the last startup failure for diagnostics.
8. Cancel startup and request host shutdown during application teardown.

The initial service list is empty. That is a valid running host and proves the lifecycle without inventing placeholder services. Milestone 2 will add the first client and server services through this composition root.

Unity lifecycle entry points catch and log asynchronous failures; exceptions are not allowed to escape an `async void` Unity callback unnoticed. Shutdown is made idempotent because Unity may invoke application-quit and object-destruction paths during the same teardown.

## Error Handling

- Invalid launch-mode combinations prevent startup and produce a clear error.
- A service startup failure triggers reverse-order rollback.
- Shutdown attempts all services and aggregates failures.
- Cancellation is preserved as cancellation rather than converted into a generic error.
- Logs identify the service being started or stopped.
- Core never calls `UnityEngine.Debug` directly.

## Testing

EditMode tests cover:

- Empty host starts and stops.
- Services start in registration order.
- Services stop in reverse order.
- Startup failure rolls back only services that started successfully.
- Cancellation during startup performs rollback.
- Shutdown continues after a service stop failure and returns an aggregate error.
- Stop is idempotent.
- A stopped host cannot restart.
- Concurrent or otherwise invalid lifecycle transitions are rejected.
- Default launch mode is retained without an override.
- Each command-line mode override is recognized.
- Conflicting mode flags are rejected.
- Unknown command-line arguments are ignored.

Test doubles record lifecycle calls and can be configured to complete, cancel, or fail. No timing-dependent sleeps are used.

## Completion Criteria

The milestone is complete when:

- The three assemblies compile without circular references.
- All specified EditMode tests pass.
- `DigBlocksBootstrap` can start an empty host in the sample scene without errors.
- Each launch mode can be resolved and logged.
- Entering and exiting Play Mode leaves no running host or undisposed cancellation source.
- No client, server, networking, voxel, or rendering implementation has leaked into Core.

## Architecture Evolution After Milestone 1

The next scaffold added `DigBlocks.Client`, `DigBlocks.Server`,
`DigBlocks.Networking`, and `DigBlocks.Networking.NetCode`. Netcode for Entities
now supplies the client/server networking model, while Bootstrap continues to
construct services based on `LaunchMode`. The Core lifecycle contracts and
launch resolution remain independent of ECS and networking packages.
