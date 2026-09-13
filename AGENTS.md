# DigBlocksDX agent guide

DigBlocksDX is a Unity 6.6 voxel game inspired by early-release Minecraft.

Development direction is user-led; scale implementation to the scope requested.

## Start here

* Use [docs/architecture.md](docs/architecture.md) when repository-wide architectural context, subsystem ownership, dependency direction, runtime topology, or task navigation is needed. Do not reread it when the relevant context is already established in the current session.
* Read focused docs only when relevant. Networking details live in [docs/network-session-foundation.md](docs/network-session-foundation.md), entities, ghosts and world coordinates live in [docs/entity-foundation.md](docs/entity-foundation.md), content authoring lives in [docs/block-definitions.md](docs/block-definitions.md) and [docs/entity-definitions.md](docs/entity-definitions.md), and known API migrations live in [docs/deprecations.md](docs/deprecations.md).
* Treat `docs/superpowers/specs/` and `docs/superpowers/plans/` as historical design records, not the current default workflow. Read them only when the current task specifically depends on an earlier design decision.
* Regenerate the machine-derived assembly index with `powershell -File tools/Update-RepositoryMap.ps1` after changing `.asmdef` files. Do not hand-edit `docs/generated/assembly-map.md`.
* This guide is the canonical agent context for every tool. `CLAUDE.md` only re-exports it for Claude Code; do not add tool-specific instructions there. Workflow skills are authored once under `.agents/skills/<name>/` (Codex reads these plus `agents/openai.yaml`); regenerate the Claude Code mirror under `.claude/skills/` with `pwsh -File tools/Sync-AgentSkills.ps1` after editing any skill.

## Context economy

* Reuse relevant context already established in the current session; do not reread files without a concrete reason.
* Prefer targeted searches and bounded file reads over repository-wide exploration or printing whole large files.
* Once the affected subsystem and relevant files are known, keep subsequent investigation focused there unless evidence requires expanding scope.
* Keep command and tool output narrow. Prefer summaries, structured results, targeted searches, and relevant log excerpts over complete logs or generated artifacts.
* Do not ingest complete Unity compiler logs, test logs, XML result files, or other large outputs when bounded evidence is sufficient. Expand output only to diagnose a failure or ambiguity.
* Avoid repeating already-successful inspections or checks unless subsequent changes could invalidate them.
* Use subagents only for genuinely independent workstreams when their likely benefit exceeds duplicated context and coordination overhead.

## Essential commands

* Lean workflow checks: `powershell -File tools/Test-LeanWorkflow.ps1`
* Fresh compile: `unity -batchmode -nographics -quit -projectPath . -logFile .utmp/compile.log`
* EditMode tests: `unity -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults .utmp/editmode-results.xml -logFile .utmp/editmode.log`
* PlayMode tests: `unity -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode -testResults .utmp/playmode-results.xml -logFile .utmp/playmode.log`

Use Unity MCP for live-editor checks when it adds value. Treat it as optional; prefer the CLI for checks that do not require live editor state, and continue with the CLI when the MCP server is unavailable.

When inspecting Unity results, start with structured test summaries, errors, warnings, and bounded log excerpts. Do not print complete logs by default.

### Running tests through Unity MCP

The editor is normally left open on this project, so the CLI commands above abort on the project
lock and tests have to go through MCP instead. That runner has three traps, all of which report
success while running nothing:

* **Run the unfiltered PlayMode suite first.** `run_tests` with `mode: PlayMode` and no filter is
  the whole suite (~450 tests, roughly two minutes). Almost every assembly registers as PlayMode
  regardless of living under `Tests/EditMode/`; an unfiltered EditMode run finds only the handful
  of Editor-platform tests.
* **Reload the domain before every PlayMode run; only the first run per domain executes.** A
  second PlayMode run in the same domain returns `total: 0` in about three seconds and still
  reports `resultState: "Passed"`, with nothing in the console to say otherwise. So begin each
  PlayMode run with `refresh_unity` (`mode: force`, `scope: scripts`, `compile: request`,
  `wait_for_ready: true`); it costs about fifteen seconds. Filters have nothing to do with this --
  two identical unfiltered runs reproduce it. EditMode is unaffected and repeats freely.
  The cause is two packages disagreeing: the MCP runner disables domain reload on entering Play
  Mode so its bridge survives the transition (and this project sets `DisableDomainReload`
  globally besides), while the Unity Test Framework assumes that reload is what resets its
  per-run state -- it never mentions `EnterPlayModeOptions`, and `PlaymodeLauncher.IsRunning` is
  set true by `MarkRunAsPlayModeTask` and never cleared in code. A manual reload while the editor
  is idle is safe and the bridge reconnects; only a reload landing mid-run strands the response,
  which is why the runner suppresses the automatic one. A run that times out wedges the domain
  the same way and also strands the editor in Play Mode.
* **`test_names` needs a complete `Namespace.Class.TestMethod`.** A namespace or class prefix
  matches nothing, and `assembly_names` does not match this project's assemblies at all. One named
  test runs in about twenty seconds, which is the fast loop worth having while debugging.

`total: 0` is a failed run, never a pass. Check the count against the suite size before believing
a green result, and read the console immediately after `refresh_unity` and before starting tests,
because a test run clears the console and a compile failure then looks like a clean build.

## Architectural constraints

* Use Unity Entities/ECS as the primary gameplay simulation model.
* Use Burst-compiled code for performance-sensitive code whenever practical.
* Move work to jobs or background threads where practical, especially simulation work, without violating Unity thread-safety constraints.
* Use Netcode for Entities exclusively; do not add Netcode for GameObjects or `NetworkObject` replication.
* Keep the server authoritative. Predict only locally controlled or otherwise latency-sensitive entities; ordinary mobs are interpolated ghosts.
* Single-player runs separate client and server ECS worlds in one process.
* Dedicated servers run only the authoritative server world.
* Keep Core lifecycle, voxel storage, save formats, and protocol contracts independent of `Unity.NetCode` where practical. NetCode integration belongs in `DigBlocks.Networking.NetCode`.
* Use entities and ghosts for dynamic objects, never one entity or ghost per voxel block.
* Store voxels in coarse three-dimensional chunks using unmanaged, job-friendly containers. Send snapshots and deltas through a bulk-data protocol rather than per-block ghost replication.
* Prefer unmanaged components, Burst-compatible systems, jobs, blob assets, and baking in hot paths. GameObjects remain valid for bootstrap, authoring, UI, and presentation.

## Scope and autonomy

* Consultation, guidance, and research do not authorize broad project changes.
* For scaffolding requests, create only the requested structure and signatures.
* Implement a complete system only when the request explicitly asks for it.
* Preserve unrelated user changes in the worktree.
* Do not expand task scope merely because adjacent cleanup or refactoring is possible.

## Lean workflow

Classify work by actual risk and blast radius:

* Small: inspect only the relevant code, make the change, and run focused verification. No workflow skill, plan file, worktree, or subagent.
* Medium: inspect the affected subsystem, implement with focused tests where useful, then verify. Load at most one matching workflow skill when it materially improves confidence.
* Large/cross-cutting: use `planning-architecture`, implement in bounded steps, use TDD where behavior has meaningful testable contracts, then verify.

Use workflow skills selectively:

* Unknown bug or unexpected failure: `systematic-debugging` when structured root-cause investigation is needed.
* New or changed observable behavior: `test-driven-development` when a meaningful regression test is practical and the workflow adds value.
* New subsystem, migration, risky refactor, public contract change, or change crossing architectural boundaries: `planning-architecture`.
* High-risk or multi-step completion where routine focused verification is insufficient: `verification-before-completion`.

Do not invoke a skill merely because it could be relevant. Routine implementation, compilation, testing, diff inspection, and straightforward fixes do not require a workflow skill.

Avoid skill chains unless each additional skill addresses a distinct unresolved risk. Do not reload a skill whose relevant instructions are already established in the current session.

## Code and guidance style

* Prefer `Cysharp.Threading.Tasks.UniTask` for Unity/game async code. Use `System.Threading.Tasks.Task` only when an external API or test runner requires it.
* Add concise comments only where they clarify non-obvious, high-traffic code. Format them as `//like this`.
* Separate important groups with whitespace without fragmenting the code.
* Preserve each touched file's line endings; default new files to CRLF.
* In implementation guides, name every file being changed and cover each system consistently from start to finish. Give code examples and explain important signatures or syntax choices.
* Present small focused types in one piece. Guide larger systems incrementally; do not skip implementations near the end.

## Definition of done

* Tests validate observable behavior and real contracts, not mocks or trivial implementation details.
* Run the narrowest useful checks during development; broaden only when the affected blast radius warrants it.
* Inspect the final diff for unrelated edits, duplicate abstractions, architectural violations, missing tests, and stale comments/docs.
* Before reporting meaningful code work complete, obtain fresh evidence from the relevant Unity compiler or test checks.
* Inspect summaries, failure details, warnings, and relevant log excerpts rather than complete logs unless deeper diagnosis is necessary.
* Do not repeat already-green checks unless subsequent changes could have affected them.
* Report actual verification results and disclose anything not run.
