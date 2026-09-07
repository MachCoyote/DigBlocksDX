---
name: test-driven-development
description: "Use when adding or changing observable behavior, fixing a regression worth preserving, or modifying critical domain logic with a clear testable contract. Do not use for documentation, generated files, configuration-only edits, or trivial mechanical refactors already covered by tests."
---

# Test-driven development

Use tests to define useful behavior, not to satisfy ceremony.

1. Choose the narrowest test level that observes the public contract.
2. Write one meaningful failing test and confirm it fails for the expected
   missing behavior, not for setup or compilation errors.
3. Implement the smallest coherent change that passes.
4. Re-run the focused test, then refactor while it stays green.
5. Cover important boundary and failure cases justified by risk.

Prefer real collaborators and state. Mock only external, slow, or nondeterministic
boundaries. A test must fail under a plausible production regression; do not
assert implementation trivia or duplicate the code's logic inside the test.

If test-first is impractical, explain why and provide the best executable
verification available. Never claim TDD when the test did not fail first.
