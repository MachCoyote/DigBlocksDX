# DigBlocksDX development guidance

DigBlocks is a Unity 6.6-based voxel game that aims to mimic and expand upon
early-release-era Minecraft. Development direction is primarily user-led.

## Architecture direction

- Use Unity Entities/ECS as the primary runtime model for gameplay simulation.
- Use Netcode for Entities as the sole networking framework. Do not introduce
  Netcode for GameObjects or `NetworkObject`-based replication.
- Keep the server authoritative. Ordinary mobs are interpolated ghosts; reserve
  prediction and rollback for locally controlled or otherwise latency-sensitive
  entities.
- Run separate client and server ECS worlds in the same process for
  single-player. Dedicated servers run only the authoritative server world.
- Keep Core lifecycle code, voxel storage, save formats, and protocol contracts
  independent of `Unity.NetCode` wherever practical. Package-specific network
  integration belongs in `DigBlocks.Networking.NetCode`.
- Use entities and ghosts for dynamic objects such as players, mobs, dropped
  items, and projectiles. Do not create one entity or ghost per voxel block.
- Store voxel data in coarse three-dimensional chunks using unmanaged,
  job-friendly containers. Transmit chunk snapshots and deltas through a
  dedicated bulk-data protocol rather than ordinary per-block ghost replication.
- Prefer unmanaged components, Burst-compatible systems, jobs, blob assets, and
  baking for hot simulation paths and runtime content data. Keep GameObjects for
  bootstrapping, authoring, UI, and presentation where they are the better fit.

## Scope and autonomy

- Treat architectural discussions, implementation guidance, and research as
  consultative work. Do not independently make broad or major project changes
  unless the request clearly authorizes them.
- For boilerplate or scaffolding requests, create only the requested folder
  layout, types, member signatures, and other high-level structure. Do not
  fill in deeper implementations unless asked.
- Implement a complete system only when the user explicitly requests an
  end-to-end implementation.

## Superpowers workflow

- For small, mechanical changes (for example a focused method edit or tests
  updated for a small signature change), do not invoke the Superpowers
  framework. Plan and verify the work proportionately.
- Use the full relevant planning and implementation toolset for architectural,
  cross-cutting, or larger implementation work.

## Code style

- Prefer `Cysharp.Threading.Tasks.UniTask` for asynchronous Unity and game
  code. Do not introduce `System.Threading.Tasks.Task` unless a required
  external API or test runner explicitly requires it.
- Add only concise comments where they give useful context in high-traffic
  code. Write them in this form: `//like this` (no space or capital letter
  immediately after `//`). Avoid comment clutter.
- Separate important/grouped bits of code with line breaks to improve readability, but don't be overzealous.

## Verification

- Before reporting work complete, inspect fresh Unity compiler and test output
  for errors; do not infer success from a command exit code or a partial log.

## Unity tooling

- The Unity CLI is installed. Prefer it for Unity operations it supports.
- Unity MCP is also available; use it to supplement the CLI when needed. The
  MCP server may not be running, so check its availability and continue with
  the CLI or another appropriate local approach when it is unavailable.
- If that is the case, and the work being done would heavily benefit from usage of the MCP server, prompt the user to enable it.

## File handling and Line Endings
- Maintain the existing line-ending style of every touched file; default to CRLF for new files unless the target location dictates LF.
