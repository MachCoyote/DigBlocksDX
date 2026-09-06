# Independent chunk transfer protocol

Implemented component contract, September 6, 2026. The carrier is
`Assets/_Project/Scripts/Networking/NetCode/Bulk/BulkDriver.cs`; framing and staging
are in `ChunkTransfer.cs` in the same directory. This is a companion-driver
protocol, not a NetCode RPC schema. Admission binding and streaming ownership are
still pending; these components must not be exposed as an admitted game service yet.

## Frame encoding

All multibyte integers are little-endian. Every message begins with the four-byte
header `44 42 01 kind`: uint16 magic 0x4244, byte version 1, byte kind. Unknown
versions/kinds and messages over 1,024 bytes are rejected. Fixed frames require
exact lengths, with no trailing bytes. Decoders reject malformed input with
`FormatException`; local encoders reject invalid arguments.

An address consists of uint32 world identity followed by int32 chunk X, Y and Z.
World zero and negative coordinates are valid. Transfer IDs, subscription
generations, incarnations and revisions are nonzero uint64 values.

| Kind | Total bytes | Fields following header, in order |
| --- | --- | --- |
| 16: start | 57 | transfer ID, subscription generation, address, incarnation, revision, int32 encoded length, byte delta flag (0 or 1) |
| 17: slice | 20 + count | transfer ID, int32 destination offset, int32 count, count payload bytes |
| 18: applied ACK | 20 | transfer ID, applied revision |
| 19: eviction | 28 | address, subscription generation |

Slices contain 1–1,004 bytes. Offsets/counts are checked without overflowing.
A snapshot declaration permits at most `ChunkWireCodec.MaxSnapshotBytes`
(8 × chunk volume + 128); a delta permits `MaxDeltaBytes` (12 × volume + 128).
At edge 32 these are 262,272 and 393,344 bytes. The encoded payload supplies its
own version/layout, address, incarnation, revision and CRC32 checks. Revision
zero is invalid consistently in storage imports, portable images/deltas and frames.
There is no outer compression in this version.

## Reassembly and ownership

Each companion connection owns a `ChunkTransferReassembler`. It permits at most
two active transfers and 786,688 encoded bytes by default. Constructor settings
can reduce these limits. `BufferedBytes` counts allocated payload buffers,
including bytes not yet received; coverage uses an additional bit per byte plus
bounded object/dictionary overhead. These limits do not include carrier queues,
completed payloads, decoded images or published replicas.

`Begin` validates before allocation. Identical active declarations are idempotent;
reuse of an active transfer ID with different metadata is rejected. Exhausted
capacity returns false without eviction or allocation. `AddSlice` accepts
out-of-order data and matching overlaps, counting each byte once. Conflicting
bytes invalidate the call before any part of that slice is written. Unknown
transfer IDs are ignored without allocation. Completion removes the entry and
transfers ownership of its byte array to the caller. It does not publish or ACK.

`Cancel(id)`, `Cancel(address, generation)` and `Clear()` release staging ownership.
Address cancellation matches the exact world/address/generation; a stale eviction
cannot cancel a newer subscription. This class does not keep completed IDs or
provide a clock. The future stream owner must reject stale/replayed starts, retain
live subscription identity, impose deadlines and call cancellation on expiry,
eviction, disconnect or world teardown. IDs must not wrap/reuse within a binding.

Before publication the owner must decode the payload, compare its identity and
revision with the declaration, check the current live subscription, and validate
registry IDs. Delta publication requires the exact acknowledged baseline. Only
successful atomic publication may produce an applied ACK; reassembly alone is
not evidence of valid or applied world data.

## Verification scope

`ChunkTransferTests.cs` covers round trips, malformed headers/lengths/values,
capacity, cancellation, duplicate declarations, out-of-order slices and atomic
overlap rejection. `ChunkTransferIntegrationTests.cs` composes the actual driver,
codec, reassembler and replica storage over IPC and loopback UDP. It exercises a
snapshot larger than the application queue, queue backpressure, an edit made after
capture, subsequent delta application, and ACKs after publication.

The integration fixture drives the sequence itself. It is not evidence that
session admission, interest scheduling, authenticated companion lifecycle,
loss/latency behavior or multi-peer streaming have been implemented. See
[implementation progress](chunk-implementation-progress.md) for measured results.
