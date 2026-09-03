# DigBlocksDX development guidance

DigBlocks is a Unity 6.6-based voxel game that aims to mimic and expand upon
early-release-era Minecraft. Development direction is primarily user-led.

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
