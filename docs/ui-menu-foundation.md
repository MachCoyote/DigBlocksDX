# UI and menu foundation

This slice owns what the client is doing and what UI is on screen. It establishes
the whole application lifecycle — intro, title, loading, playing, leaving — before
any world-selection or settings UI exists, so later menus plug into finished
contracts instead of replacing them.

The client no longer starts a `GameHost` at launch. Bootstrap composes
application-lifetime services, shows the title screen, and waits. A session is
created when the player chooses play and disposed when they leave, so a second
play is a genuinely fresh session. A dedicated server ignores all of this and
starts its session directly.

## Responsibility split

```text
DigBlocksBootstrap            composition root, process shutdown
├── GameSessionController     one session lifetime at a time, readiness, teardown
│   └── GameHost              ordered service startup, rollback, reverse shutdown
├── ApplicationFlowController Intro, Title, Loading, Playing, Leaving
├── MenuCoordinator           screen, overlay and modal navigation, focus
│   └── MenuRegistry          MenuId to factory and metadata
├── DebugMenuController       debug overlay, debug keybinds, toggle cycling
│   └── DebugOptions          toggle state, readable across assemblies
└── ClientInputRouter         gameplay action availability and cursor state
```

Application flow owns transitions with game-level meaning; the coordinator owns
only what is visible and interactive. Views emit typed intent (`PlayRequested`,
`ResumeRequested`, `ReturnToTitleRequested`) and never touch sessions, ECS worlds
or networking. Opening settings above pause is UI navigation and leaves the
application in `Playing`; returning to title is a flow operation that tears the
session down.

Navigation and session transitions are serialized through `AsyncGate`, so repeated
play clicks cannot start two sessions and repeated cancel input cannot stack two
pause menus.

## Session readiness

A started `GameHost` is not a playable world. `GameSessionController` starts the
session's services, then waits on every service implementing
`ISessionReadinessSource` before reporting `GameSessionPhase.Ready`; flow only
enters `Playing` on that signal, and reports `Failed` back to the loading view
otherwise.

`ChunkCompanionService` is the current readiness source: it reports ready when the
client bulk endpoint is bound, which is the world-data channel rather than the game
connection — `NetCodeSession` deliberately stops at `AwaitingWorldData`. Tighten
that gate to `ClientDataReady` once gameplay drives server chunk interest, so a
session is only playable with streamed chunks in hand. That is a one-line change in
`ChunkCompanionService.WaitUntilReadyAsync`, marked with a comment there.

## Menus

Menus are authored as uGUI prefabs under a persistent `UIRoot` and registered in a
`MenuCatalog` asset, so adding a menu is asset work rather than a code change. Each
entry supplies the metadata the coordinator needs:

| Field | Meaning |
| --- | --- |
| Menu Id | identity used by flow and the registry, for example `Title` |
| Layer | `Screen`, `Overlay` or `Modal` |
| Blocks Interaction Underneath | when cleared, the menu directly beneath stays interactive |
| Blocks Gameplay Input | contributes to disabling gameplay actions |
| Shows Cursor | contributes to releasing and showing the cursor |
| Back Behavior | `None` absorbs back, `Close` closes the menu, `ViewHandled` calls `IMenuBackHandler`, `PassThrough` routes back to the menu beneath |
| Takes Navigation Focus | when cleared, the menu never becomes the focus target and cannot steal the selected control |
| Retention | `Retain` keeps the instance disabled for reuse, `Destroy` disposes it |
| Stretch Root To Layer | fills the layer; clear it for menus that own their own layout |

Creation goes through `IMenuFactory`, which has a prefab implementation and a
delegate implementation. Both produce the same `IMenuView` lifecycle, so a future
code-authored or mod-provided menu participates in the same navigation, focus,
input and cleanup without touching the coordinator. Any well-known menu that is not
authored yet falls back to a code-built placeholder, which keeps the full flow
exercisable and doubles as a working example of the non-prefab factory path.

Exactly one screen is active; changing it clears the navigation belonging to the old
screen. Overlays preserve what is underneath, modals sit above everything. Only the
top eligible menu is interactive: covered menus have their `CanvasGroup`
interaction and raycasts disabled, their selected control is remembered, and it is
restored when they are uncovered. Back never leaks to a covered menu.

The UI root is built in code — canvas, scaler, an `EventSystem` with the input
system module, and screen, overlay and modal attachment points, with nested canvases
on overlay and modal so their rebuilds stay off the screen layer. An authored
`UIRoot` prefab may be assigned on the bootstrap object to replace it. Menu prefabs
need no canvas or event system of their own.

## Wiring an authored menu

1. Put a view component on the prefab root: `TitleMenuView`, `LoadingView`,
   `PauseMenuView`, `IntroView` or `DebugMenuView`, and assign its controls. The view
   adds its own button listeners, so no `onClick` entries are needed in the inspector.
2. Create a catalog with Assets > Create > DigBlocks > Menu Catalog and add an entry
   for the prefab.
3. Assign the catalog to `Menu Catalog` on the `DigBlocks Bootstrap` object in the
   boot scene.

Registering `Intro` makes launch show it and advance to title on skip or completion;
leaving it unregistered goes straight to title. `Pause` belongs on the overlay layer
with `ViewHandled` back behavior so cancel input resumes rather than merely closing.

## Debug overlay

`F3` shows and hides a debug overlay registered as the `Debug` menu. It is an
overlay in the same coordinator as every other menu, but it is authored to be
purely informational: it blocks neither gameplay input nor the cursor, leaves the
menu beneath interactive, never takes navigation focus, and passes back through.
That last pair is what lets it sit above the pause menu without swallowing
`Escape` or the pause menu's selected button.

Toggles are declared in code in `DebugToggleCatalog`, each one a display name, a
key and its cycle of states. `DebugMenuController` builds one standalone
`InputAction` per toggle, so debug keys never depend on which action maps the
input router has enabled, and enables them only while the overlay is bound — the
menu key works at any time, the toggles only while the menu is on screen. The
view is populated from the same declarations, one `Name - Key` line per toggle,
so a new toggle is a catalog entry and nothing else.

State lives in `DebugOptions` in Core, keyed by `DebugToggleId`. That keeps the
switchboard readable by subsystems that must not depend on client UI:
`TerrainDebugBinder` in `DigBlocks.Client.Rendering` observes the rendering
toggles and drives the terrain shader's globals, and `TerrainRenderService`
observes the overdraw toggle for the part that globals cannot carry. The current
toggles are `F8` wireframe (off, over the surface, wires only), `F7` fullbright,
and `F6` overdraw (off, every geometry layer, shaded fragments only). See
`docs/chunk-meshing-rendering.md` for what each one does to the shader.

## Input and pause

`ClientInputRouter` combines application state with the coordinator's reported
requirements. Gameplay actions are enabled only while `Playing` with no menu that
blocks them; the cursor is locked and hidden in that case and released otherwise.
UI actions stay enabled because cancel drives back and pause — hidden menus are
made non-interactive by the coordinator rather than by switching the map off.

Pause is four separate responsibilities: the coordinator shows and focuses the
overlay, the router disables gameplay input and releases the cursor, flow decides
that a local session should freeze, and the session controller applies that freeze
through services implementing `ISimulationPauseSink`. Nothing implements that sink
yet, so single-player pause currently freezes input and UI only; the request is
logged rather than silently dropped. Menu animation and flow timing use unscaled
time so they keep working when a simulation clock is frozen.

## Verified

EditMode and PlayMode suites pass, including a bootstrap flow test that drives
title, play, session readiness, playing, return to title and a fully stopped session
without leaking a network world. Covered by tests: screen replacement, duplicate
overlay suppression, back routing and absorption, covered-menu interaction loss and
restoration, retained-instance reuse, reported input requirements, explicit session
readiness, rejected overlapping sessions, startup failure rollback, and a fresh
session after leaving. The debug overlay adds coverage for back routing over a
pass-through overlay, back reported unhandled when only pass-through menus are open,
a non-blocking overlay leaving the menu beneath interactive, the input requirements
it reports, toggle state cycling and notification, and the catalog invariants that
every toggle has a unique identifier, a unique key and at least two states.

## Not in this slice

Worlds and settings menus, confirmation and error dialogs, richer loading progress,
per-menu presenters, the code-authored menu builder API, mod scripting and its
permission model, menu pooling, and simulation freeze itself. Debug toggles are
declared in code rather than authored as assets, hold no state across runs, and
report no live values; a debug HUD with frame timings, chunk counts and position
readouts is a later slice on the same overlay. Play enters the fixed
development world; world selection later feeds the same
`ApplicationFlowController` and `GameSessionController` path.
