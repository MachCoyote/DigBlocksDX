using System;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;

namespace DigBlocks.Networking.NetCode
{
    internal sealed class ChunkStreamingClient : IDisposable
    {
        private readonly ResidentChunkStore store;
        private readonly uint maxSolid, maxFluid;
        private readonly ChunkStreamingOptions options;
        private readonly Func<byte[], bool> send;
        private readonly Action fail;
        private readonly ChunkTransferReassembler assembler;
        //several chunks are in flight at once, so declarations are tracked per transfer rather than singly.
        private readonly Dictionary<ulong, TransferStart> active = new();
        private readonly Queue<byte[]> responses = new();
        private ChunkInterest interest;
        //transfers complete out of order, so staleness is a settled set rather than a high-water mark:
        //everything at or below the floor is settled, and the set holds the gaps above it.
        private readonly HashSet<ulong> retired = new();
        private ulong retiredFloor, highestSeen;
        private byte[] interestRequest;
        private double deadline;
        private int retries;
        private bool disposed;
        public ChunkStreamingClient(ResidentChunkStore store, DigBlocks.Voxels.BlockRegistry registry, ChunkStreamingOptions options,
            Func<byte[], bool> send, Action fail, double now)
        {
            this.store = store; maxSolid = registry.MaxSolidStateId; maxFluid = registry.MaxFluidStateId; this.options = options;
            this.send = send; this.fail = fail; deadline = now + options.ProgressTimeout;
            assembler = new ChunkTransferReassembler(options.PeerWindow, options.PeerWindow * ChunkWireCodec.MaxDeltaBytes);
        }

        public bool RequestInterest(ChunkAddress anchor)
        {
            if (disposed || anchor.World != options.WorldId) return false;
            if (interestRequest == null && interest != null && interest.Anchor.Equals(anchor)) return true;
            interestRequest = ChunkTransferFrames.EncodeInterestRequest(anchor);
            return true;
        }

        public void Receive(byte[] packet, double now)
        {
            if (disposed) return;
            var kind = ChunkTransferFrames.ReadKind(packet);
            if (kind == ChunkFrameKind.Interest)
            {
                var next = ChunkTransferFrames.DecodeInterest(packet);
                if (interest != null && next.Epoch <= interest.Epoch)
                {
                    if (next.Epoch == interest.Epoch && (!next.Anchor.Equals(interest.Anchor) || next.HorizontalRadius != interest.HorizontalRadius || next.VerticalRadius != interest.VerticalRadius))
                        throw new FormatException("Conflicting live interest.");
                    return;
                }
                if (!store.SetReplicaInterest(next)) throw new FormatException("Rejected live interest.");
                interest = next; assembler.Clear(); active.Clear(); responses.Clear(); retries = 0;
                //every transfer declared under the old epoch is dead, so settle the whole range at once.
                retired.Clear(); retiredFloor = Math.Max(retiredFloor, highestSeen);
                deadline = now + options.ProgressTimeout;
                return;
            }
            if (kind == ChunkFrameKind.Start)
            {
                var start = ChunkTransferFrames.DecodeStart(packet);
                if (interest == null) throw new FormatException("Transfer precedes interest.");
                //a repeated declaration for a live transfer is benign; the reassembler validates it matches.
                if (active.ContainsKey(start.TransferId)) { assembler.Begin(start); return; }
                if (Settled(start.TransferId)) return;
                highestSeen = Math.Max(highestSeen, start.TransferId);
                if (start.SubscriptionGeneration < interest.Epoch) { Retire(start.TransferId); return; }
                if (start.SubscriptionGeneration != interest.Epoch || !interest.Contains(start.Address)) throw new FormatException("Transfer outside live interest.");
                if (!assembler.Begin(start)) throw new FormatException("Reassembly budget exceeded.");
                active.Add(start.TransferId, start); deadline = now + options.ProgressTimeout;
                return;
            }
            if (kind != ChunkFrameKind.Slice) throw new FormatException("Unexpected server chunk frame.");
            ChunkTransferFrames.DecodeSlice(packet, out ulong id, out int offset, out var bytes);
            if (!active.ContainsKey(id))
            {
                if (!Settled(id)) throw new FormatException("Slice precedes declaration.");
                return;
            }
            deadline = now + options.ProgressTimeout;
            if (!assembler.AddSlice(id, offset, bytes, out var declaration, out var payload)) return;
            active.Remove(id);
            ChunkImage image;
            if (declaration.IsDelta)
            {
                var delta = ChunkWireCodec.DecodeDelta(payload, maxSolid, maxFluid);
                if (!delta.Address.Equals(declaration.Address) || delta.Incarnation != declaration.Incarnation || delta.ResultRevision != declaration.Revision)
                    throw new FormatException("Delta differs from declaration.");
                if (!store.TryReadReplica(delta.Address, out var baseline) || baseline.Incarnation != delta.Incarnation || baseline.Revision != delta.BaseRevision)
                { RequestResync(id, now); return; }
                image = delta.ApplyTo(baseline);
            }
            else
            {
                image = ChunkWireCodec.DecodeSnapshot(payload, maxSolid, maxFluid);
                if (!image.Address.Equals(declaration.Address) || image.Incarnation != declaration.Incarnation || image.Revision != declaration.Revision)
                    throw new FormatException("Snapshot differs from declaration.");
            }
            if (!store.PublishReplica(declaration.SubscriptionGeneration, image)) { RequestResync(id, now); return; }
            responses.Enqueue(ChunkTransferFrames.EncodeAcknowledgement(id, image.Revision));
            Retire(id); retries = 0;
        }

        private void RequestResync(ulong id, double now)
        {
            //the server restarts a resynced chunk under a fresh identity, so this one is finished either way.
            assembler.Cancel(id); active.Remove(id); Retire(id);
            if (++retries > 2) { fail(); return; }
            responses.Enqueue(ChunkTransferFrames.EncodeResync(id)); deadline = now + options.ProgressTimeout;
        }

        private bool Settled(ulong id) => id <= retiredFloor || retired.Contains(id);

        private void Retire(ulong id)
        {
            if (id <= retiredFloor) return;
            retired.Add(id);
            while (retired.Remove(retiredFloor + 1)) retiredFloor++;
            if (retired.Count <= 4 * options.PeerWindow) return;
            //abandoned transfers leave permanent gaps. Collapse them, but never past a live transfer.
            ulong lowestActive = ulong.MaxValue;
            foreach (ulong live in active.Keys) lowestActive = Math.Min(lowestActive, live);
            ulong floor = retiredFloor;
            foreach (ulong settled in retired) if (settled < lowestActive) floor = Math.Max(floor, settled);
            retiredFloor = floor;
            retired.RemoveWhere(settled => settled <= retiredFloor);
        }

        public void Tick(double now)
        {
            if (disposed) return;
            //acknowledgements gate nothing on the server now, but they still confirm delta baselines,
            //so drain as many as the transport will take rather than one per tick.
            while (responses.Count > 0 && send(responses.Peek()))
            { responses.Dequeue(); deadline = now + options.ProgressTimeout; }
            if (responses.Count == 0 && interestRequest != null && send(interestRequest))
            { interestRequest = null; deadline = now + options.ProgressTimeout; }
            if (now < deadline) return;
            if (active.Count != 0)
            {
                //oldest declaration first; resyncing one is enough to restart progress.
                ulong oldest = ulong.MaxValue;
                foreach (ulong id in active.Keys) oldest = Math.Min(oldest, id);
                RequestResync(oldest, now);
            }
            else if (responses.Count != 0 || interest == null || !store.DataReady) fail();
        }

        public void Dispose()
        {
            if (disposed) return;
            assembler.Clear(); active.Clear(); responses.Clear(); retired.Clear(); interestRequest = null; store.ClearReplicas(); disposed = true;
        }
    }
}
