# Chunk networking design

Status: architecture approved for implementation; partially implemented as of
September 6, 2026. Companion to the approved
[chunk data architecture](chunk-data-architecture.md). See
[implementation progress](chunk-implementation-progress.md) for verified scope.
Values labeled provisional are test starting points, not performance claims.

## Goal and boundaries

Extend admitted connections from `AwaitingWorldData` to coherent, bounded 3D chunk
replication. Preserve authoritative ECS simulation, separate singleplayer worlds,
UDP remote connections, and the existing Netcode for Entities session foundation.
Target 32 concurrent peers with fair streaming and bounded memory, not unlimited
view distance or guaranteed throughput independent of hardware/link capacity.

No terrain generation, player controller, mesher, fluid simulation, full inventory
system, automatic reconnect, account authentication, asset downloading, or public
Internet hardening is implemented by this design. Dummy data and controlled test
interest anchors exercise streaming. Do not treat client-supplied test anchors as
a production authority model. No one-ghost-per-chunk/block replication.

Storage owns world contents and mutation. Shared portable protocol/codec code owns
wire validation, snapshots, deltas and identifiers without Unity.NetCode types.
Server/client ECS systems own interest and replica state. Networking.NetCode owns
carrier integration on the admitted connection. Session admission must not become
a monolithic chunk manager. Presentation readiness is separate from data readiness.

## Research and adaptation

- [Mojang Java 1.20.2 release notes](https://feedback.minecraft.net/hc/en-us/articles/19703470383757-Minecraft-Java-Edition-1-20-2)
  describe configuration between login and play, and smaller bandwidth-sensitive
  chunk batches instead of one continuous send. Adopt phased readiness and pacing,
  not Java's TCP transport or full-height column protocol. This is a modern
  reference, not a claim about the 1.5-1.10 era.
- [Luanti network protocol source](https://github.com/luanti-org/luanti/blob/master/src/network/networkprotocol.h)
  separates serialized mapblocks, individual node changes, inventory messages and
  active objects. Use corresponding domain separation rather than a universal
  replication mechanism. Its dimensions and exact packet schema are not ours.
- [Glenn Fiedler: Sending Large Blocks of Data](https://gafferongames.com/post/sending_large_blocks_of_data/)
  explains why loss of one unreliable fragment can waste an entire large message,
  and presents bounded slices, acknowledgements and pacing. Adopt those principles;
  do not duplicate transport reliability over the reliable carrier or copy the article's
  single-transfer algorithm as a universal high-throughput solution.
- [LZ4 project](https://github.com/lz4/lz4) provides a fast lossless compression
  candidate. Actual Unity integration, allocation behavior and throughput require
  validation; it is not an installed dependency or a selected library wrapper.
- Installed NetCode 6.6.0 `Runtime/Rpc/RpcSystem.cs` sends via `reliablePipeline`,
  retries queue-full sends and rejects an RPC larger than the offered writer.
  `Runtime/Connection/NetworkStreamReceiveSystem.cs` dispatches known NetCode
  protocol types. These local sources are authoritative for this build. There is
  no assumption that creating an extra UTP pipeline automatically creates a public
  application-message receiver on the existing NetCode connection.

## Carrier decision: independent companion connection

The approved carrier is a standalone Unity Transport driver with a reliable
pipeline. NetCode carries targeted admission/control offers only; chunk payloads
never enter its RPC FIFO. Singleplayer uses IPC exclusively. Remote sessions use
a configurable second UDP port (game port + 1 by default), with no NetCode package
fork or independent polling of NetCode's driver.

After admission, a targeted offer supplies the bulk port and a short-lived,
128-bit, single-use ticket. The companion connection must bind to that ticket and
the live admitted peer/connection generation. Disconnect, rejection and teardown
revoke binding state. An offline XUID alone is insufficient. This ticket binds
the two connections; it does not provide account authentication or encryption.
Pending unauthenticated connections require caps and expiry before chunk allocation.

The carrier accepts at most 1,024 application bytes per reliable message, with
bounded per-peer/global queues and fair draining. Application framing divides
encoded chunks into slices. Applied acknowledgements report validated and
published revisions, not transport delivery. Both sockets still share bandwidth
and CPU; independent queues do not guarantee latency or throughput.

Keep the established loss/latency and concurrent-control measurements below as
integration requirements. [Lifecycle and admission binding](chunk-residency-binding.md)
are implemented and tested with actual IPC/UDP sessions. Automatic streaming
scheduling and publication remain unfinished.

## Configuration and registry agreement

After native/application admission, exchange a bounded configuration manifest:
application chunk-protocol/schema version, supported codecs, chunk edge length,
world identity/stream epoch, authoritative interest limits, and gameplay registry
fingerprint. Native NetCode RPC/ghost compatibility remains separately required.

Proposed initial policy: clients have the same required gameplay definitions,
including solid/fluid state schemas and behavior-relevant content. Derive runtime
IDs from a canonical sorted registration order and hash a specified canonical
representation. Matching names alone is insufficient. Reject mismatches clearly
before allocating chunks. Client-only cosmetic differences need not participate
unless they alter required gameplay behavior. A fingerprint is compatibility
checking, not authentication or protection against a modified client.

Do not send strings per voxel. With matching registries, wire palettes reference
agreed runtime state IDs. Future negotiated mapping/mod downloads are separate
features. Save identifiers remain stable keys, independent of this session policy.
Bound configuration time, message sizes, entry counts and outstanding work.

## Interest and scheduling

The server computes permitted interest from authoritative player location (or an
explicit server-owned test anchor before players exist). Client view requests are
preferences clamped to server limits, not permission to query arbitrary positions.

- Independent horizontal and vertical radii, initially expressed in chunks.
- Begin with cuboid membership and distance-based priority; defaults remain open.
- Recompute membership on anchor chunk/radius/world changes, not by testing every
  loaded chunk against every peer every frame.
- Server data residency is a union across peers; per-peer subscription/ack state
  is separate. Shared chunk encoding can be reused for the same revision, registry,
  codec and public-data policy, under a bounded cache budget.
- Prioritize a small safety/bootstrap neighborhood, urgent corrections and nearby
  edits, then nearby missing chunks. Add aging so distant eligible work cannot
  starve indefinitely. Directional bias can be added after basic correctness.
- Use per-peer and global byte/time budgets and fair peer scheduling. Account for
  packet overhead/retransmissions in measurement; reserved bulk application bytes
  are not a guarantee of actual link capacity or congestion control.
- Receiver credits bound in-flight compressed bytes, decoded staging bytes and
  pending apply work. Client throughput feedback can reduce its allocation, never
  force the server beyond global caps. Cap bursts and outstanding queue length.
- Cancel unsent work that leaves interest; bounded already-queued slices may still
  arrive. Use unload hysteresis and explicit eviction/resubscription identities.

A provisional experiment can start at 256 KiB/s bulk payload per peer and a shared
8 MiB/s ceiling, then sweep budgets. Thirty-two peers at 256 KiB/s total 8 MiB/s
(about 67 Mbit/s) before overhead/retransmissions/ghosts. These are configurable
experiments, not chosen release defaults. Memory caps and fairness are required
even on LAN. Startup transfer time depends on compressed content, not chunk count
alone. If 2,601 chunks average 8 KiB each, about 20 MiB takes at least 81 seconds at
256 KiB/s before overhead; do not block initial readiness on full view distance.

## Transfer identity and snapshot format

Logical identifiers include world/stream epoch, chunk address, chunk incarnation,
per-peer subscription generation, transfer ID, and revision. Field widths and the implemented frame contract
are specified in [chunk transfer protocol](chunk-transfer-protocol.md). Identifiers cannot wrap/reuse within their active
lifetime in a way that accepts stale data. Incarnation changes when recreating a
chunk with reset revisions; subscription generation changes after eviction/reentry.

A snapshot captures one coherent revision of the public replicated chunk view:
solid channel, independent fluid channel, explicitly public optional flags, and
public block-entity projections. Do not serialize the live object/native memory
layout or automatically publish every persisted record.

Each channel supports uniform, local palette plus bit-packed indices, and direct
fallback encoding. Select the smallest useful representation accounting for the
palette cost. Wire packing may differ from runtime byte/ushort storage. Define byte
order, bit order, padding, count encoding and canonical validation before coding.
Packed width derives from palette size; no global ten-bit registry restriction.

Candidate compression is LZ4 per independent snapshot payload after palette
encoding, with an uncompressed fallback when compression does not save enough.
Small deltas generally remain uncompressed. Codec selection is versioned/negotiated;
exact package and thresholds remain open. Compress once per eligible shared payload
where practical. Decoding must have a strict output bound, not trust embedded size.

Transfers declare encoded/decoded lengths, schema/codec, revision, slice layout and
an integrity check over defined payload bytes. Integrity checks detect corruption,
not malicious authorship. Validate all lengths, counts, positions, state IDs,
duplicate entries, flags and slice ranges before publishing. Slices identify their
transfer; duplicates must be harmless or rejected if conflicting. Time out and
release abandoned partial transfers. Reject stale epochs/generations before large
allocations. Final validation and atomic publication precede the applied ACK.

## Deltas and races

The server owns a monotonically advancing replication revision per chunk
incarnation. Storage may also track private/persistence revisions; private changes
must not create inexplicable gaps in a public replication sequence. Commit related
solid/fluid/public metadata changes as coherent batches.

A delta contains base revision, resulting revision and bounded absolute updates by
local position/channel, including explicit removals. Use agreed global runtime
state IDs or a delta-local palette, never unchecked indices into the mutable
server-side chunk palette. Coalesce final cell values where semantics allow;
gameplay events/effects are not reconstructed by replaying overwritten cell states.

Apply only when the replica matches the base revision. Ignore validated duplicates;
on a gap or incompatible incarnation request bounded resynchronization, not blind
application. Initially retain a bounded shared change history; if catch-up history
is unavailable or larger than a replacement snapshot, send a newer snapshot.

Capture snapshot revision R without retaining a write lock during network transfer.
The client atomically applies R and acknowledges R; server then sends the retained
R-to-current changes. If history expired, replace the baseline. Never silently drop
edits made while a snapshot was in flight. Under continuous edits, rate-limit
restarts and use bounded lag/timeout policy to avoid infinite allocation/restart
loops. Start with at most one unacknowledged revision batch per chunk subscription;
different chunks may progress concurrently. Measure RTT-induced limits later.

Client data publication is atomic per chunk batch, not across arbitrary adjacent
chunks. Mark affected mesh/collision work dirty after publication. Multi-chunk
gameplay transactions needing stronger client atomicity would be a later feature.
Old asynchronous decode results must not resurrect evicted/replaced chunks.

## Block entities, privacy and player actions

World replication is an explicit public projection, not a dump of save data.
Static sign text and visual machine state may be public. Inventory contents and
ownership/security data are not broadcast with every surrounding chunk. Future
container interaction has a server-authorized subscription and separate bounded
schema. Player-placed flags default to server-only unless client behavior needs
them. Replication redaction affects cache keys and revision policy.

Clients eventually send action intent (place/break/interact), never authoritative
chunk snapshots. Server validates reach, permissions, state, rate limits and item
ownership, then emits authoritative changes. Speculative local visual edits, if
added, must be separate from the acknowledged replica. Full interaction and
prediction implementation are outside this first streaming slice.

Sending a chunk reveals its contents to the recipient, including hidden blocks.
This proposal does not implement anti-xray filtering or protect data already sent.

## Readiness, eviction and failure

Conceptual client progression:

`AwaitingWorldData -> ConfiguringWorld -> StreamingInitialData -> WorldDataReady`

Names are proposed world-sync states, not authorization to modify session enums
yet. Configuration acceptance precedes streaming. WorldDataReady means the agreed
small bootstrap neighborhood is decoded/applied, not that every render-distance
chunk is present or that the player is playable. Initial data deadline and progress
timeouts must tolerate the configured byte budgets while remaining bounded.

Keep `NetworkStreamInGame` absent until later ghost and playable-world prerequisites
exist. Future player spawn/movement release additionally waits for required nearby
collision/presentation readiness. Do not allow a client to walk into unknown
terrain as if it were air; exact movement gating is a later controller contract.

On interest removal, invalidate subscription generation, release staged transfers
and retire the replica safely after dependent jobs. A later resubscription cannot
accept old payloads/ACKs. On disconnect/world change, invalidate the entire stream
epoch and release all per-peer state. Repeated malformed messages or abuse follow
bounded reject/disconnect policy; recoverable baseline gaps get bounded resync.

## Verification and tandem implementation sequence

1. Finalize identifiers, registry fingerprint and size/codec contracts alongside
   storage coordinates/definitions. Unit-test round trips and malformed inputs.
2. Prove the bounded independent carrier with dummy payloads using actual IPC and UDP;
   agree performance gates before adding substantial features on top of it.
3. Implement uniform/paletted storage and snapshot codecs, coherent capture and
   atomic replica publication; test solid/fluid independence and all supported sizes.
4. Add bounded 3D interest, receiver credits, fair scheduling and eviction.
5. Add revisions, delta coalescing, ACK/history/resync and public metadata policies.
6. Exercise multi-peer integrated dummy worlds and document observed limits before
   introducing meshing, terrain generation or playable ghost readiness.

Tests must include late join; edits during snapshot transfer; palette promotion;
duplicate/stale messages; revision gaps; unload/reenter; disconnect/rejoin; world
change; missing registries; invalid/compression-bomb lengths; missing/conflicting
slices; slow client; exhausted history; burst edits; negative coordinates; and
neighbor changes. Decoder tests inject reorder/duplication explicitly even though
the first carrier is ordered. No ghost/entity-per-voxel representation.

Run actual loss/latency/jitter simulation, not only same-process happy paths.
Exercise 1, 8 and 32 peers with overlapping and disjoint interests. Suggested sweep:
0/50/150 ms RTT and 0/1/5 percent loss, with declared bandwidth caps and hardware.
Track initial-neighborhood latency, p95/p99 control/delta delay, encoded bytes per
chunk, codec CPU, staging/resident memory, queue age, applied-ACK lag, resync count
and server tick time. Agree numerical pass criteria after baseline measurements;
32 successful admissions do not establish streaming performance. Compare end-state
replica contents to the authoritative public projection, not just log messages.

## Decisions and remaining consultation

Independent companion transport, strict required-gameplay-registry matching,
public-only projection, small-neighborhood data readiness, and bounded resync are
approved. Major changes to these choices require consultation.

Release bandwidth/memory targets, view distances, bootstrap-neighborhood size and
production timeout defaults remain open. Use explicitly provisional bounded test
settings until baseline measurements support a user-reviewed release policy.
