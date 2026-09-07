# From chunk data to meshing and visualization

Status: September 6, 2026. This describes remaining work and consultation points;
it does not change the approved implementation order or authorize a mesher.
See [implementation progress](chunk-implementation-progress.md) for completed work.

## What already exists

The project has chunk coordinates/layout, uniform/paletted unmanaged solid/fluid
storage, immutable registry definitions, revisioned edits, coherent job captures,
portable snapshot/delta codecs, an independent reliable IPC/UDP driver, single-use
tickets, transfer frames and bounded reassembly. Component tests combine these
pieces successfully. They are not yet wired into a live game-world streaming service.

World residency, snapshot workers and companion binding (steps 1 and 2 below)
are now complete. See [contracts and fresh verification](chunk-residency-binding.md).
The table retains their completion criteria for context; steps 3–5 remain.

## Foundation work, in dependency order

| Step | Work remaining | Observable completion condition |
| --- | --- | --- |
| 1. World residency and workers | World-owned chunk store; one lightweight ECS identity per resident chunk; lifetime/incarnation ownership; bounded capture/encoding jobs; cleanup on unload/world destruction. Dummy data only. | Server and client worlds own separate chunks; capture/edit/unload races cannot leak memory or publish stale results. |
| 2. Companion session lifecycle | Start/stop after/before the NetCode session respectively; configurable bulk port; targeted post-admission ticket offer and bind; live peer/generation checks; registry/layout compatibility; pending-binding caps/timeouts; disconnect/kick revocation. | An admitted singleplayer IPC or remote UDP session can bind its independent data connection; rejected/stale/mismatched peers cannot stream; failed startup and teardown release resources. |
| 3. Interest and bounded streaming | Server-owned dummy anchor; horizontal/vertical interest; union residency; per-peer subscriptions; fair byte budgets and bounded queues; cancellation, eviction and reentry generations. | A small permitted neighborhood streams automatically; leaving it releases replicas; slow peers cannot cause unbounded growth or prevent other peers progressing. |
| 4. Replica publication and revision recovery | Validate declaration against decoded payload and live subscription; atomic client-store publication; applied ACK tracking; bounded delta history; exact-baseline application; timeout/resync; separate data-ready state. | Edits during transfer converge to current server contents; gaps recover; old transfers/async completions cannot resurrect evicted chunks. Data readiness does not set NetworkStreamInGame. |
| 5. Integrated verification | Full-project compile and affected session/bootstrap tests; actual lifecycle IPC/UDP runs; late join, mismatch, failure, eviction/reentry, stale/replayed messages; loss/latency and multiple-peer measurements. | The production service, rather than a test-driven transfer sequence, delivers matching replicas and cleans up under failure. Measured limits and remaining risks are documented. |

Steps 3 and 4 will need bounded implementation together: subscription identity,
publication, acknowledgements and eviction must agree on one lifecycle. They are
separate concerns, not an invitation to ship an unsafe intermediate service.

Earlier isolated Unity results were 45/45 EditMode tests at each temporary
edge setting (16, 32, 64) and 19/19 PlayMode tests. Production edge is 32. These are
component correctness results; they do not establish full-project integration,
loss tolerance, or 32-peer streaming performance.

## Entry point for meshing

The approved data design places streaming integration before meshing/generation.
After the foundation above, visualization can consume published client replicas
without inventing another world-data owner. The first visual milestone can remain
small: a camera and a few dummy chunks showing updates and correct chunk borders.
Terrain generation, a player controller, playable ghosts, persistence and full
fluid simulation are not prerequisites for that milestone.

A standalone dummy-chunk preview could technically start earlier, but that would
change the agreed sequence and need user agreement. It would not demonstrate
integrated streaming. Multi-peer performance work is required for the current
streaming milestone; deciding to defer part of it for an earlier preview is also
a scope decision, not an assumption made here.

## Decisions to settle when beginning the meshing milestone

- Initial rendering scope: solid opaque cubes only, or include transparent solids,
  fluids and custom models. Existing model keys are identifiers, not render assets.
- Meshing approach: begin with exposed-face generation or invest immediately in
  greedy merging; define compatible material/texture and face-merging rules.
- Border behavior: neighbor sampling, how missing neighbors are represented, and
  which adjacent chunks become dirty when a boundary cell or residency changes.
- Presentation ownership: bounded mesh build/upload scheduling, stale-result
  rejection by incarnation/revision, mesh disposal, and main-thread Unity uploads.
- Visual milestone: texture/material setup, lighting expectations, camera and
  inspection controls; whether collision is needed at this stage.

These are consultation points, not selections already made. Existing ECS,
Burst/jobs where practical, server authority and separate client replicas remain
constraints. The next bounded task is defining interest/subscription and replica
publication contracts for steps 3 and 4; no rendering architecture choice is
needed yet.
