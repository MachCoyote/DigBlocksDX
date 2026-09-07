---

name: verification-before-completion
description: "Use when about to declare meaningful code changes complete, fixed, or passing. Do not use as a mandatory ceremony for documentation-only or trivial reversible edits unless the user requests validation."
------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

# Verification before completion

Gather the cheapest sufficient fresh evidence.

1. Inspect the final diff and status for scope, accidental edits, duplication,
   architectural violations, missing tests, and stale documentation.

2. Run the narrowest tests that exercise the changed behavior.

3. Run compile, static analysis, formatting, or broader tests only when the
   affected boundary or repository definition of done warrants them.

4. Inspect structured results, summaries, failures, warnings, and relevant log
   excerpts. Do not ingest complete logs or generated result files when bounded
   output provides sufficient evidence. Expand only when diagnosing a failure
   or ambiguity.

5. Report the commands run and actual outcomes. Clearly list checks not run and
   any remaining uncertainty.

Do not repeat already-green checks or broaden verification after sufficient
relevant evidence is available unless subsequent changes, new failures, or
blast-radius concerns justify it.
