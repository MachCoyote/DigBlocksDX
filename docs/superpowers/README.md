# Superpowers notes

Use the Superpowers framework selectively in this repository.

- Skip it for small, mechanical tasks such as a focused method update or
  updating tests for a small signature change. Still plan and verify the
  change proportionately.
- Use the full relevant planning and implementation workflow for larger,
  cross-cutting, or architectural work.
- Preserve the user's development direction. Consultation and scaffolding
  requests do not authorize broad implementation work.

The canonical agent instructions are in [AGENTS.md](../../AGENTS.md).

## Unity tooling

Prefer the installed Unity CLI for supported Unity operations. Use Unity MCP
where it adds capabilities the CLI does not provide, but treat its server as
optional because it may not be running.
