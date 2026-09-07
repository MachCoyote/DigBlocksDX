# World residency and companion binding: implementation plan

Scope: milestones 1 and 2 of the meshing-readiness guide. Requested September 6,
2026. This work establishes world ownership, bounded snapshot workers, and an
admitted companion data connection. It does not implement interest streaming,
delta history, meshing, terrain generation or playable ghosts.

## Research and implications

- Minecraft Java 1.20.2 introduced configuration after login and before play,
  including registry/feature setup. The same release describes smaller,
  bandwidth-sensitive chunk batches. Adopt explicit compatibility/binding states;
  do not copy its TCP transport or allow indefinite configuration waits.
  Source: https://www.minecraft.net/en-us/article/minecraft-java-edition-1-20-2
- Luanti distinguishes loaded mapblocks from active mapblocks, with activity near
  players and additional activation behaviors. Adopt residency separate from
  simulation activity and client subscriptions. No ticking policy is added here.
  Source: https://api.luanti.org/map-terminology-and-coordinates/
- Vintage Story exposes asynchronous priority column loading, keep-loaded control,
  and explicit unload that informs clients. Adopt explicit lifetime ownership and
  worker completion; retain DigBlocks' approved 3D addresses rather than columns.
  Source: https://apidocs.vintagestory.at/api/Vintagestory.API.Server.IWorldManagerAPI.html
- Voxel Tools documents moving copies into threaded tasks and the complexity/cost
  of per-buffer locking, including neighbor lock ordering. Retain existing Burst
  capture jobs, then encode detached images. The worker never accesses live ECS
  entities or mutable palette allocations.
  Source: https://voxel-tools.readthedocs.io/en/latest/performance/

These are scoped primary-source comparisons, not benchmark results or claims
that Minecraft uses our ECS store or companion-ticket protocol.

## Options and consultation

1. Recommended: an independent DigBlocks.Voxels.Runtime assembly with a managed
   world-owned control system, unmanaged chunk identity components and existing
   unmanaged payload allocations. Explicit leases let multiple future residency
   reasons share one chunk. Alternative: explicit load/unload only (smaller API,
   but later consumers must coordinate ownership outside the store).
2. Recommended: required companion failure disconnects only its associated peer,
   with a concrete failure reason, and startup rolls back on binding failure.
   Alternative: retain a non-playable game connection after bulk failure, which
   needs additional recovery/UI policy. No automatic reconnect is proposed.

User approved both recommendations: residency leases and disconnecting only the
affected peer on required chunk-channel failure. Previously approved: independent reliable UTP, IPC-only
singleplayer, configurable second remote UDP port, targeted post-admission offer,
short-lived single-use ticket, strict registry/layout matching, no InGame marker.

## Implemented boundaries

- Voxels: portable storage and mutation remain independent of ECS and NetCode.
- Voxels.Runtime: one store per ECS world, chunk entity/lease lifetime and bounded
  snapshot workers. References Voxels, ChunkProtocol, Entities, Collections and
  Mathematics. Encoding uses ThreadPool; service async uses UniTask.
- Networking.NetCode: companion offers/control RPCs, binding frames, connection
  ownership, deadlines/revocation and service lifecycle.
- Networking launch options: explicit bulk-port configuration; reject invalid
  derived port overflow and public endpoint overrides in IPC singleplayer.
- Bootstrap: compose the companion service after NetCodeSession. Reverse teardown
  releases it before NetCode and world destruction.

## Ordered implementation and evidence

1. Specify and test fixed bounded binding frames independent of failure policy.
2. Implement the agreed world store and worker ownership. Test shared residency,
   edit/capture races, cancellation, unload/recreate identity and world teardown.
3. Add targeted offers and required compatibility checks; bind only live admitted
   nonzero peers with established native NetCode connections. Test wrong/replayed/
   expired tickets, registry/layout mismatch, direction/source validation.
4. Integrate service startup/stop and launch parsing. Test IPC and UDP host/session
   binding, kick/disconnect cleanup, occupied port/cancellation/startup rollback,
   world destruction and absence of playable-world readiness markers.
5. Run fresh Unity tests against actual source/assembly definitions, regenerate
   assembly index and update network/progress docs with verified scope.

Full peer interest, automatic chunk transmission and applied revision scheduling
remain later milestones. Component readiness here means companion binding only.

## Completion and additional comparison

Steps 1 and 2 are implemented. See [runtime contracts and verification](chunk-residency-binding.md)
for the file map, limits and fresh Unity results.
[Paper's chunk API](https://jd.papermc.io/paper/1.21.4/org/bukkit/Chunk.html)
provides explicit plugin tickets that retain chunks until removed. This supports
leases over uncoordinated load/unload calls; it describes Paper, not vanilla internals.
