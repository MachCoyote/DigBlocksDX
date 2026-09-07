---
name: systematic-debugging
description: "Use when a bug, failing test, regression, or unexpected behavior has a non-obvious root cause. Do not use for straightforward implementation or an obvious isolated fix."
---

<!-- Generated from .agents/skills/systematic-debugging/SKILL.md by tools/Sync-AgentSkills.ps1. Do not edit here. -->

# Systematic debugging

Reproduce before editing. Preserve the first useful failure evidence.

1. Reproduce the smallest reliable failure and record the exact symptom.
2. Trace data and control flow across the relevant boundary; compare a working
   path when available.
3. Form one concrete hypothesis and test it with the cheapest discriminating
   check. Avoid stacking speculative edits.
4. Identify the root cause, then make the smallest fix at that cause.
5. Add or update a regression test when it can observe the failure meaningfully.
6. Re-run the reproducer and the narrow affected suite. Inspect fresh logs.

If reproduction is impossible, state what evidence is missing and distinguish
confirmed facts from hypotheses. Do not hide failures, weaken assertions, or
change unrelated behavior to make a test pass.
