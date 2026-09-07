# Historical Superpowers records

The specs and plans below are retained as useful design history. They are not
the repository's active orchestration framework.

Superpowers 6.3.0 remains installed and enabled for the user globally, but
`.codex/config.toml` disables the plugin in this trusted repository. Removing
that local override restores the previous availability without reinstalling or
recovering deleted files.

Current workflow guidance lives in [AGENTS.md](../../AGENTS.md). Stable project
structure and architectural rationale live in
[architecture.md](../architecture.md). Four small project skills under
`.agents/skills/` load only when their trigger descriptions materially match.

New routine work should not add a design or plan here. Add durable design
documentation only when it will remain useful beyond the implementation task.
