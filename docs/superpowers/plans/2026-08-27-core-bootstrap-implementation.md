# DigBlocks Core Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the first DigBlocks milestone: deterministic launch-mode resolution, ordered asynchronous service hosting with rollback, and a Unity composition root that starts an empty application host.

**Architecture:** `DigBlocks.Core` contains framework-neutral launch and hosting code. `DigBlocks.Bootstrap` is the Unity-facing composition root and logger adapter. EditMode tests exercise Core without Play Mode; the sample scene supplies the one persistent bootstrap object.

**Tech Stack:** Unity 6000.3.17f1, C#, UniTask/`CancellationToken`, Unity Test Framework 1.6.0, NUnit, Unity assembly definitions.

**Spec:** `docs/superpowers/specs/2026-08-27-core-bootstrap-design.md`

## Global Constraints

- Implement only Core/bootstrap lifecycle; do not add Netcode for Entities, client/server runtimes, voxel storage, generation, saving, or rendering.
- `DigBlocks.Core` references the project's UniTask assembly for asynchronous lifecycle APIs.
- Construct dependencies explicitly; do not add a service locator or reflection-based discovery.
- `GameHost` is single-use: it cannot restart after stop or failure.
- Start services sequentially in registration order and stop them in reverse order.
- Use tests without timing-dependent sleeps.
- Work in `Assets/_Project/Scripts` and `Assets/_Project/Tests/EditMode`.

---

## Planned File Structure

```text
Assets/_Project/Scripts/Core/
    DigBlocks.Core.asmdef
    Hosting/GameHost.cs
    Hosting/GameHostStartException.cs
    Hosting/GameHostState.cs
    Hosting/GameLogLevel.cs
    Hosting/IGameLogger.cs
    Hosting/IGameService.cs
    Launch/LaunchMode.cs
    Launch/LaunchModeResolver.cs
    Launch/LaunchOptions.cs

Assets/_Project/Scripts/Bootstrap/
    DigBlocks.Bootstrap.asmdef
    DigBlocksBootstrap.cs
    UnityGameLogger.cs

Assets/_Project/Tests/EditMode/Core/
    DigBlocks.Core.Tests.asmdef
    Hosting/GameHostTests.cs
    Hosting/RecordingGameService.cs
    Hosting/RecordingLogger.cs
    Launch/LaunchModeResolverTests.cs
```

`Assets/Scenes/SampleScene.unity` is modified only after Unity has imported the new scripts and generated stable `.meta` GUIDs.

---

### Task 1: Launch-mode contracts and resolution

**Files:**
- Create: `Assets/_Project/Scripts/Core/DigBlocks.Core.asmdef`
- Create: `Assets/_Project/Scripts/Core/Launch/LaunchMode.cs`
- Create: `Assets/_Project/Scripts/Core/Launch/LaunchOptions.cs`
- Create: `Assets/_Project/Scripts/Core/Launch/LaunchModeResolver.cs`
- Create: `Assets/_Project/Tests/EditMode/Core/DigBlocks.Core.Tests.asmdef`
- Create: `Assets/_Project/Tests/EditMode/Core/Launch/LaunchModeResolverTests.cs`

**Interfaces:**
- Produces: `LaunchMode`, `LaunchOptions`, and `LaunchModeResolver.Resolve(LaunchMode, IReadOnlyList<string>, bool)`.
- Consumes: .NET collections and `System` only.

- [ ] **Step 1: Create the Core and EditMode test assembly definitions**

Create `DigBlocks.Core.asmdef` with assembly name and root namespace `DigBlocks.Core`, `autoReferenced: true`, and no project references.

Create `DigBlocks.Core.Tests.asmdef` with assembly name/root namespace `DigBlocks.Core.Tests`, reference `DigBlocks.Core`, restrict it to Editor, add `optionalUnityReferences: ["TestAssemblies"]`, and set `autoReferenced: false`.

- [ ] **Step 2: Write failing launch-resolution tests**

Cover these exact cases in `LaunchModeResolverTests`:

```csharp
[TestCase(LaunchMode.SinglePlayer)]
[TestCase(LaunchMode.RemoteClient)]
[TestCase(LaunchMode.DedicatedServer)]
public void Resolve_WithoutOverride_ReturnsConfiguredDefault(LaunchMode configuredDefault)
{
    LaunchOptions options = LaunchModeResolver.Resolve(configuredDefault, Array.Empty<string>(), false);
    Assert.That(options.Mode, Is.EqualTo(configuredDefault));
}

[Test]
public void Resolve_ServerBuildWithoutOverride_ReturnsDedicatedServer()
{
    LaunchOptions options = LaunchModeResolver.Resolve(LaunchMode.SinglePlayer, Array.Empty<string>(), true);
    Assert.That(options.Mode, Is.EqualTo(LaunchMode.DedicatedServer));
}

[TestCase("--singleplayer", LaunchMode.SinglePlayer)]
[TestCase("--client", LaunchMode.RemoteClient)]
[TestCase("--server", LaunchMode.DedicatedServer)]
public void Resolve_WithModeFlag_ReturnsOverride(string flag, LaunchMode expected)
{
    LaunchOptions options = LaunchModeResolver.Resolve(LaunchMode.SinglePlayer, new[] { "app", flag }, false);
    Assert.That(options.Mode, Is.EqualTo(expected));
}

[Test]
public void Resolve_WithUnknownArguments_IgnoresThem()
{
    LaunchOptions options = LaunchModeResolver.Resolve(
        LaunchMode.RemoteClient,
        new[] { "app", "-logFile", "server.log" },
        false);
    Assert.That(options.Mode, Is.EqualTo(LaunchMode.RemoteClient));
}

[Test]
public void Resolve_WithMultipleModeFlags_Throws()
{
    Assert.That(
        () => LaunchModeResolver.Resolve(
            LaunchMode.SinglePlayer,
            new[] { "app", "--client", "--server" },
            false),
        Throws.TypeOf<ArgumentException>());
}
```

Add a second conflict assertion using the same flag twice so any second recognized mode flag is rejected.

- [ ] **Step 3: Run the focused EditMode tests and verify the red state**

Run Unity in batch mode with project path `D:\Projects\Unity\DigBlocksDX`, EditMode tests, and filter `DigBlocks.Core.Tests.Launch.LaunchModeResolverTests`.

Expected: compilation fails because `LaunchMode`, `LaunchOptions`, and `LaunchModeResolver` do not exist.

- [ ] **Step 4: Implement minimal launch contracts**

Implement:

```csharp
namespace DigBlocks.Core.Launch
{
    public enum LaunchMode
    {
        SinglePlayer,
        RemoteClient,
        DedicatedServer
    }

    public readonly struct LaunchOptions
    {
        public LaunchOptions(LaunchMode mode) => Mode = mode;
        public LaunchMode Mode { get; }
    }
}
```

`LaunchModeResolver.Resolve` must:

- Throw `ArgumentNullException` when `arguments` is null.
- Select `DedicatedServer` as the effective default when `isServerBuild` is true.
- Compare recognized flags with `StringComparison.OrdinalIgnoreCase`.
- Count every recognized mode flag; throw `ArgumentException` when the count exceeds one.
- Ignore every unrecognized argument.
- Return a new immutable `LaunchOptions`.

- [ ] **Step 5: Run the launch tests and verify green**

Expected: all `LaunchModeResolverTests` pass with zero unexpected test failures.

- [ ] **Step 6: Commit the launch slice**

```powershell
git add -- 'Assets/_Project/Scripts/Core' 'Assets/_Project/Tests/EditMode/Core'
git commit -m "feat: add launch mode resolution"
```

---

### Task 2: Service startup and rollback

**Files:**
- Create: `Assets/_Project/Scripts/Core/Hosting/GameLogLevel.cs`
- Create: `Assets/_Project/Scripts/Core/Hosting/IGameLogger.cs`
- Create: `Assets/_Project/Scripts/Core/Hosting/IGameService.cs`
- Create: `Assets/_Project/Scripts/Core/Hosting/GameHostState.cs`
- Create: `Assets/_Project/Scripts/Core/Hosting/GameHostStartException.cs`
- Create: `Assets/_Project/Scripts/Core/Hosting/GameHost.cs`
- Create: `Assets/_Project/Tests/EditMode/Core/Hosting/RecordingGameService.cs`
- Create: `Assets/_Project/Tests/EditMode/Core/Hosting/RecordingLogger.cs`
- Create: `Assets/_Project/Tests/EditMode/Core/Hosting/GameHostTests.cs`

**Interfaces:**
- Produces: `IGameService.StartAsync/StopAsync`, `IGameLogger.Log`, `GameHost.StartAsync`, `GameHost.State`, and rollback error reporting.
- Consumes: UniTask, `CancellationToken`, collections, and exceptions.

- [ ] **Step 1: Write service test doubles**

`RecordingGameService` accepts a name and shared `List<string> events`. It records `start:<name>` and `stop:<name>`. It exposes optional delegates:

```csharp
public Func<CancellationToken, UniTask> StartBehavior { get; set; }
public Func<CancellationToken, UniTask> StopBehavior { get; set; }
```

When a delegate is null, return `UniTask.CompletedTask`.

`RecordingLogger` records `(GameLogLevel Level, string Message, Exception Exception)` entries without writing to Unity output.

- [ ] **Step 2: Write failing startup tests**

Cover:

```csharp
[Test]
public async Task StartAsync_StartsServicesInRegistrationOrder()
{
    var events = new List<string>();
    var first = new RecordingGameService("first", events);
    var second = new RecordingGameService("second", events);
    var host = new GameHost(new IGameService[] { first, second }, new RecordingLogger());

    await host.StartAsync(CancellationToken.None);

    Assert.That(events, Is.EqualTo(new[] { "start:first", "start:second" }));
    Assert.That(host.State, Is.EqualTo(GameHostState.Running));
}

[Test]
public void StartAsync_WhenServiceFails_RollsBackStartedServicesInReverseOrder()
{
    // first and second start; third throws. Assert stop:second then stop:first,
    // host Faulted, and original exception instance is rethrown.
}

[Test]
public void StartAsync_WhenCancelled_RollsBackAndPreservesCancellation()
{
    // second service returns UniTask.FromCanceled using the received token.
    // Assert first stops and OperationCanceledException is observed.
}

[Test]
public void StartAsync_WhenRollbackFails_ReportsPrimaryAndRollbackFailures()
{
    // first starts but throws during rollback; second fails during startup.
    // Assert GameHostStartException.InnerException is the startup failure and
    // RollbackErrors contains the cleanup failure.
}
```

- [ ] **Step 3: Run hosting tests and verify the red state**

Expected: compilation fails because the hosting contracts and `GameHost` do not exist.

- [ ] **Step 4: Implement hosting contracts and startup**

Use these signatures:

```csharp
public interface IGameService
{
    string Name { get; }
    UniTask StartAsync(CancellationToken cancellationToken);
    UniTask StopAsync(CancellationToken cancellationToken);
}

public interface IGameLogger
{
    void Log(GameLogLevel level, string message, Exception exception = null);
}

public sealed class GameHostStartException : Exception
{
    public GameHostStartException(
        string message,
        Exception startupException,
        IReadOnlyList<Exception> rollbackErrors);

    public IReadOnlyList<Exception> RollbackErrors { get; }
}
```

`GameHost` copies the supplied service sequence into an array, rejects null services, tracks successfully started services, updates `State`, and logs before starting/stopping each service.

When startup fails, rollback uses `CancellationToken.None`, attempts every started service in reverse order, and retains only services whose rollback failed so a later explicit stop from `Faulted` can retry them.

- [ ] **Step 5: Run hosting tests and verify green**

Expected: startup, rollback, cancellation, and rollback-error tests all pass.

- [ ] **Step 6: Run all Core EditMode tests**

Expected: launch and hosting test fixtures pass together.

- [ ] **Step 7: Commit startup hosting**

```powershell
git add -- 'Assets/_Project/Scripts/Core/Hosting' 'Assets/_Project/Tests/EditMode/Core/Hosting'
git commit -m "feat: add ordered game host startup"
```

---

### Task 3: Shutdown, state transitions, and transition rejection

**Files:**
- Modify: `Assets/_Project/Scripts/Core/Hosting/GameHost.cs`
- Modify: `Assets/_Project/Tests/EditMode/Core/Hosting/GameHostTests.cs`

**Interfaces:**
- Produces: `GameHost.StopAsync(CancellationToken)` with reverse shutdown, idempotence, aggregation, and single-use enforcement.
- Consumes: Task 2 hosting contracts and test doubles.

- [ ] **Step 1: Add failing shutdown tests**

Add tests for:

- An empty host starts and stops.
- Running services stop in reverse registration order.
- Stopping a `Created` host moves directly to `Stopped` without service calls.
- Calling stop twice produces no additional service calls.
- Stop continues after one service fails and ultimately throws `AggregateException` containing every stop failure.
- A stopped host rejects `StartAsync` with `InvalidOperationException`.
- A second `StartAsync` while startup is held by a `TaskCompletionSource` is rejected immediately.
- `StopAsync` while startup is held is rejected immediately.

Use a `TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)` to hold startup; do not use sleeps or timeouts as synchronization.

- [ ] **Step 2: Run hosting tests and verify the new tests fail**

Expected: failures identify missing/incomplete shutdown and transition behavior.

- [ ] **Step 3: Implement shutdown and transition guard**

Use an `Interlocked.CompareExchange` transition flag. If another lifecycle transition is active, throw `InvalidOperationException` rather than waiting.

`StopAsync` behavior:

- `Stopped`: return successfully.
- `Created`: set `Stopped`, return successfully.
- `Running` or `Faulted`: set `Stopping`, attempt every currently started service in reverse order, clear the started list, set `Stopped`, then throw one `AggregateException` if failures were collected.
- Other states: throw `InvalidOperationException`.

Always release the transition flag in `finally`.

- [ ] **Step 4: Run all Core tests and verify green**

Expected: all launch and hosting tests pass, including deterministic concurrency tests.

- [ ] **Step 5: Commit completed GameHost lifecycle**

```powershell
git add -- 'Assets/_Project/Scripts/Core/Hosting/GameHost.cs' 'Assets/_Project/Tests/EditMode/Core/Hosting/GameHostTests.cs'
git commit -m "feat: complete game host shutdown lifecycle"
```

---

### Task 4: Unity composition root and scene integration

**Files:**
- Create: `Assets/_Project/Scripts/Bootstrap/DigBlocks.Bootstrap.asmdef`
- Create: `Assets/_Project/Scripts/Bootstrap/UnityGameLogger.cs`
- Create: `Assets/_Project/Scripts/Bootstrap/DigBlocksBootstrap.cs`
- Modify after import: `Assets/Scenes/SampleScene.unity`

**Interfaces:**
- Consumes: `GameHost`, `IGameLogger`, `LaunchModeResolver`, and `LaunchOptions`.
- Produces: one Unity entry point that starts an empty host and exposes `HostState`, `LaunchOptions`, and `LastFailure`.

- [ ] **Step 1: Create the Bootstrap assembly**

Create `DigBlocks.Bootstrap.asmdef` with root namespace `DigBlocks.Bootstrap`, reference `DigBlocks.Core`, and allow UnityEngine references.

- [ ] **Step 2: Implement `UnityGameLogger`**

Map:

- `Debug` and `Information` to `UnityEngine.Debug.Log`.
- `Warning` to `UnityEngine.Debug.LogWarning`.
- `Error` to `UnityEngine.Debug.LogError`, followed by `Debug.LogException` when the exception is non-null.

- [ ] **Step 3: Implement `DigBlocksBootstrap`**

Required members:

```csharp
[DefaultExecutionOrder(-10000)]
public sealed class DigBlocksBootstrap : MonoBehaviour
{
    [SerializeField] private LaunchMode defaultLaunchMode = LaunchMode.SinglePlayer;

    public GameHostState HostState { get; private set; } = GameHostState.Created;
    public LaunchOptions LaunchOptions { get; private set; }
    public Exception LastFailure { get; private set; }
}
```

Behavior:

- Destroy duplicate instances in `Awake`; keep the first and call `DontDestroyOnLoad`.
- Create one `CancellationTokenSource`.
- In `Start`, resolve launch options from `Environment.GetCommandLineArgs()` and `UNITY_SERVER`, construct an empty `IGameService[]`, create `GameHost`, and await startup.
- Update `HostState` after lifecycle calls.
- Catch and log startup failures; preserve them in `LastFailure`.
- In `OnApplicationQuit`, cancel startup.
- In `OnDestroy`, only the owning instance requests idempotent host shutdown, catches/logs shutdown failures, disposes the cancellation source, and clears the singleton.
- Never use `FindObjectOfType`, reflection, or static service access.

- [ ] **Step 4: Import scripts and add the bootstrap to the sample scene**

Open/import the project with Unity batch mode once so `.meta` files and script GUIDs exist. Add a root `GameObject` named `DigBlocks Bootstrap` with the `DigBlocksBootstrap` component to `Assets/Scenes/SampleScene.unity`. Save the scene through Unity, not by inventing a script GUID in YAML.

- [ ] **Step 5: Run all EditMode tests**

Expected: all Core tests pass.

- [ ] **Step 6: Run a Play Mode smoke test**

Open the sample scene and enter Play Mode, or execute a short Unity batch-mode player-loop smoke path if supported by the installed editor.

Verify:

- Exactly one bootstrap object exists.
- The resolved mode is logged.
- The empty host reaches `Running`.
- Exiting Play Mode reaches shutdown without an unobserved exception.

- [ ] **Step 7: Inspect Unity logs for compiler, native-container, and unobserved-task errors**

Expected: none attributable to the new scaffold.

- [ ] **Step 8: Commit and push the Core scaffold**

```powershell
git add -- 'Assets/_Project/Scripts' 'Assets/_Project/Tests' 'Assets/Scenes/SampleScene.unity' 'docs/superpowers/plans/2026-08-27-core-bootstrap-implementation.md'
git commit -m "feat: scaffold DigBlocks core bootstrap"
git push origin main
```

---

## Post-implementation Guidance

After verification, document for the user:

1. How `DigBlocksBootstrap` selects a launch mode.
2. How to create a future service by implementing `IGameService`.
3. Where Milestone 2 will register `ServerRuntime`, `ClientRuntime`, and Netcode for Entities adapters.
4. Which Core invariants must remain unchanged: explicit ownership, ordered lifecycle, no UnityEngine dependency, no service locator.
5. The recommended next exercise: add one harmless diagnostic service in a test before adding networking.
