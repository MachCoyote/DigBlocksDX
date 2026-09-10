# Chunk loading optimization plan

Date: 2026-09-09. **Completed 2026-09-10**; every step landed. Outcome and measurements in
[chunk-loading-optimization.md](../../chunk-loading-optimization.md).

One decision changed during implementation: the receipt/acknowledgement split in step 4 was dropped.
A sent transfer holds no payload, so simply deepening the window achieves the same decoupling with no
protocol change. One was added: the asynchronous snapshot encoder was removed outright once encoding
became a copy, which took roughly three ticks of latency out of every chunk. Driven by [chunk-loading-gap-analysis.md](../../chunk-loading-gap-analysis.md),
which holds the measurements this plan acts on.

## Goal

Take chunk loading from 245 chunks in 2.12 s with a hard 256-chunk ceiling to a 2,205-chunk
cylinder (384-block radius, roughly Minecraft render distance 24) filling in a few seconds, with
per-frame cost that fits inside a 16.6 ms budget.

Out of scope: level of detail, changing world height, a GPU-driven/mesh-shader renderer rewrite,
and disk-backed world persistence.

## Constraints

- Server stays authoritative. Single player runs both worlds in one process and must keep
  exercising the wire path *by default*; direct delivery is an opt-in debug toggle.
- Netcode for Entities only. Bulk chunk data stays out of ghost replication.
- Core lifecycle, voxel storage, save formats and protocol contracts stay independent of
  `Unity.NetCode`.
- `ChunkWireCodec` is a hostile-input boundary. Bounds checks and payload integrity stay.
- Hot paths stay Burst-compatible and unmanaged.
- Preserve unrelated worktree changes.

## Decisions taken

| Decision | Choice | Why |
| --- | --- | --- |
| Interest shape | Cylinder: `dx^2 + dz^2 <= h^2` and `abs(dy) <= v` | User call. Drops 29% of chunks at equal visible radius by removing corners further than the render distance. |
| Interest ceiling | `MaximumChunks` 256 -> 32768 | Covers every radius up to ~h=22; stops being a thing that needs changing. Real bound stays `maxResidents`, which is local config, not wire-controlled. |
| Authored distance | h=12 v=2 = 2,205 chunks | 384-block radius, the comparison target. |
| Packing layout | Non-spanning: entries never straddle a 64-bit word | One shift+mask to index. Matches Minecraft's `BitStorage` since 1.16. Wastes at most a few bits per word. |
| Palette widths | bits 0 (uniform), 1..15 indirect, 32 direct | A chunk holds at most 32,768 distinct values, so 15 bits always suffices for an indirect palette; direct exists only for the case where the palette costs more than raw values. |
| Payload integrity | Keep the checksum, make it fast | Byte-at-a-time CRC32 becomes the dominant cost once decode is a memcpy. The format is explicitly hardened against hostile input; dropping the check to buy speed weakens that. |
| Window vs apply budget | Deepen `PeerWindow`, no receipt frame | A sent transfer holds no payload memory, so a deep window is nearly free. Same decoupling as a receipt/ack split, with no protocol change. |
| Direct delivery default | Off | `docs/architecture.md` values single player exercising the multiplayer path. Toggle flips the *next* chunk, not the session. |
| Mesher input | Reads packed views directly | User call. Removes the 7-full-chunk expansion entirely rather than shrinking it. |
| Wire compatibility | None required | Pre-release, no shipped clients. Codec byte goes 0 -> 1. |

## Affected boundaries

| Assembly | What changes |
| --- | --- |
| `DigBlocks.Voxels` | `PaletteChannel` storage becomes bit-packed `ulong` words; `ChunkData` capture and bulk load follow it. `ChunkLayout` unchanged. |
| `DigBlocks.ChunkProtocol` | `ChunkInterest` becomes a cylinder with a raised ceiling. `ChunkWireCodec` v2 carries the packed layout. New packed snapshot types. |
| `DigBlocks.Voxels.Runtime` | `ResidentChunkStore` gains packed capture/publish and an asynchronous load pump. `IAuthoritativeChunkSource` contract changes. |
| `DigBlocks.Networking.NetCode` | `ChunkStreamingServer`/`Client` budgets and counters; optional direct-delivery delegate. `ChunkCompanionService` owns the single-player bridge. |
| `DigBlocks.Client.Rendering` | `ChunkMeshScheduler` reads packed views and picks work from distance buckets. `TerrainRenderer` upload ring. Settings caps. |
| `DigBlocks.Core.Diagnostics`, `DigBlocks.Client.Debugging` | One new debug toggle. |

Dependency direction is unchanged throughout: protocol and voxel storage stay below networking,
which stays below the client.

## Steps

Each step compiles, passes focused tests, and is committed on its own.

1. **Cylinder interest and lifted ceilings.** `ChunkInterest` shape + `MaximumChunks`;
   `maxResidents` default; `TerrainRenderSettings.MaxChunks` range; `ChunkOcclusionGraph` and
   `MeshRangeAllocator` capacity sizing. Authored distances stay small until step 9.
2. **Bit-packed `PaletteChannel`.** Storage only. The wire still expands at the boundary, so
   nothing else changes behaviour and the existing suite is the regression guard.
3. **Wire format v2.** Codec byte 1 carries bits-per-entry, palette and raw words. Capture and
   publish become palette plus memcpy. Fast checksum.
4. **Streaming budgets.** Deep `PeerWindow`, reassembler sized by `MaxBufferedPayloads` rather
   than window, O(1) payload counters in place of the nested rescans.
5. **Asynchronous worldgen.** `IAuthoritativeChunkSource` produces whole channels on a worker;
   `ResidentChunkStore` gains a load pump; `StartTransfers` skips chunks still generating.
6. **Mesher reads packed views.** `PaddingJob` reads each source chunk's packed words and palette
   directly; the seven `SolidCopyJob` expansions and their `NativeArray<uint>` buffers go away.
7. **Mesh scheduling and upload.** Distance-bucketed ready queue instead of the linear scan;
   upload ring buffer sized in bytes instead of two worst-case slots.
8. **Single-player direct delivery.** Optional delegate on the streaming server, bridge in
   `ChunkCompanionService`, debug toggle, off by default.
9. **Retune, re-measure, document.** Author h=12 v=2 and the new budgets, re-run the profiling
   harness, update the analysis and summary docs.

## Verification

- Behavioural contracts that must not move: radial-ish arrival order, exactly one transfer per
  chunk per load, delta baselines surviving pipelining, replica rejection of out-of-interest or
  conflicting chunks, and every storage width round-tripping through both mutation and bulk load.
- Focused checks per step; `ChunkStreamingThroughputTests` and `ChunkDataTests` are the standing
  guards.
- `ChunkPipelineProfileTests` (explicit, run by name) re-measured after steps 4, 6, 7 and 9.
- Full EditMode and PlayMode before declaring the work complete.
