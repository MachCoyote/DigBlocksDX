# Chunk data and independent streaming implementation

User-authorized implementation, September 5, 2026. Unity 6000.6.0f1 release,
Entities/NetCode 6.6.0. Preserve unrelated worktree changes. Scoped commits authorized September 6, 2026.

## Decisions

- Approved: chunk-data architecture including independent fluid channels.
- Approved: independent bulk path, not chunk payload RPCs on NetCode's queue.
- Approved deployment: companion Unity Transport
  reliable connection; configurable second UDP port, IPC-only singleplayer;
  short-lived single-use ticket tied to an admitted peer and revoked on disconnect.
- Keep full required-content matching, bounded public projection, dummy data and
  data readiness separate from ghosts/playable readiness. No generated terrain,
  rendering, player controls or inventory simulation in this slice.

## Bounded milestones

1. Storage contracts: size/coordinates, immutable registry, unmanaged uniform and
   paletted solid/fluid channels; sparse flags/records; coherent revisioned edits.
   Verify coordinates, palette promotion/fallback, independent channels, job reads,
   disposal and invalid input. Tests precede observable behavior where practical.
2. Portable replication: bounded snapshot/delta codecs, registry compatibility,
   staged transfer identity, atomic publication and resync. Verify actual round
   trips and malformed/stale data; codecs operate on detached capture buffers.
3. Independent carrier: UTP lifecycle/reliable slices, bounded sends/reassembly,
   ticket binding and timeouts; no Unity package fork. Verify actual IPC and UDP,
   queue-full handling, expired/replayed/wrong-peer tickets and disconnect cleanup.
4. Tandem integration: world-owned resident chunk identities, small server-owned
   dummy interest neighborhood, fair per-peer/global budgets, applied revisions,
   configuration/data-ready states and host registration. Test late joins, edits
   during snapshots, overlap/disjoint interest, eviction/reentry and shutdown.
5. Verification: fresh Unity compile, focused then affected existing EditMode and
   PlayMode suites, loss/latency tests and multi-peer results. Record limitations;
   regenerate assembly map, review final diff and keep docs aligned with reality.

## Ownership

Storage and portable codecs do not depend on Unity.NetCode. ECS owns world-local
chunk lifetime and worker dependencies. NetCode adapter supplies admission/control
and binds a separate carrier; bulk packets never poll NetCode's driver or use its
RPC payload queue. All caps, cleanup paths, and failure states are testable.

## Progress

- Planning and installed-package inspection complete.
- Storage, portable codecs, independent carrier and tickets are implemented; see
  [implementation progress](chunk-implementation-progress.md) and the local
  `.utmp/task-handoff.md` for verification and remaining coverage.
- Immutable block registry implemented: canonical state properties/tags, reserved
  empty IDs, ordinal state IDs, strict validation and SHA-256 compatibility hash.
  All 16 existing registry tests passed in an isolated Unity 6000.6.0f1 EditMode
  project; full-project integration verification remains pending.
- Transfer framing/reassembly implemented and covered by component IPC/UDP tests.
  Expanded edge-32 storage/codec tests pass 45/45; bulk PlayMode tests pass 19/19.
- World residency, bounded snapshot workers and admitted companion lifecycle are
  implemented; see [contracts and evidence](chunk-residency-binding.md).
- Interest streaming, atomic client publication, ACK/history/resync scheduling
  and production IPC/UDP verification are implemented. See the
  [streaming summary](chunk-streaming-implementation.md) for current evidence,
  configurable bounds, scale measurements and limits.

Earlier result counts above describe individual historical milestones. Current
full-path verification supersedes their pending integration notes. See
[meshing readiness](chunk-meshing-readiness.md) for the stopping point and the
unimplemented rendering decisions.
