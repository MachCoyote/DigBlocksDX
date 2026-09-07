---
name: planning-architecture
description: "Use when work crosses architectural boundaries, introduces a subsystem or migration, changes an important public contract, has materially different strategies, or is a risky refactor whose order matters. Do not use for routine changes contained within an established subsystem."
---

<!-- Generated from .agents/skills/planning-architecture/SKILL.md by tools/Sync-AgentSkills.ps1. Do not edit here. -->

# Planning architecture

Inspect only the repository-map entries, interfaces, representative consumers,
and tests needed to establish the affected boundary. Prefer established
boundaries over parallel abstractions. Do not reread context already established
in the current session.

Normally state the design briefly in chat:

## Goal

Name the observable outcome and what is explicitly out of scope.

## Constraints

List project invariants, compatibility needs, and important risks.

## Affected boundaries

Name owners, public interfaces, dependency direction, and data flow that change.

## Proposed approach

Compare materially different options only when they exist. Recommend the
simplest approach that preserves cohesion, ownership, and minimal public surface.

Order implementation into bounded, independently verifiable steps.

## Verification

Identify behavioral contracts, focused checks, and broader checks warranted by
the blast radius.

Create a persistent plan only when work will span sessions, the plan is useful
project documentation, or repository convention requires it. Surface genuinely
material ambiguity; otherwise make a reasonable scoped assumption and proceed.
