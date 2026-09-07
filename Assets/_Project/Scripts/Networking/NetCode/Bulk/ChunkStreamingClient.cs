using System;
using DigBlocks.ChunkProtocol;
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
        private readonly ChunkTransferReassembler assembler = new(1);
        private ChunkInterest interest;
        private TransferStart active;
        private ulong highestTransfer;
        private byte[] response;
        private double deadline;
        private int retries;
        private bool disposed;
        public ChunkStreamingClient(ResidentChunkStore store, DigBlocks.Voxels.BlockRegistry registry, ChunkStreamingOptions options,
            Func<byte[], bool> send, Action fail, double now)
        {
            this.store = store; maxSolid = registry.MaxSolidStateId; maxFluid = registry.MaxFluidStateId; this.options = options;
            this.send = send; this.fail = fail; deadline = now + options.ProgressTimeout;
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
                interest = next; assembler.Clear(); active = default; response = null; retries = 0;
                deadline = now + options.ProgressTimeout;
                return;
            }
            if (kind == ChunkFrameKind.Start)
            {
                var start = ChunkTransferFrames.DecodeStart(packet);
                if (interest == null) throw new FormatException("Transfer precedes interest.");
                if (start.TransferId == active.TransferId)
                { assembler.Begin(start); return; }
                if (start.TransferId <= highestTransfer) return;
                if (start.SubscriptionGeneration < interest.Epoch) { highestTransfer = start.TransferId; return; }
                if (start.SubscriptionGeneration != interest.Epoch || !interest.Contains(start.Address)) throw new FormatException("Transfer outside live interest.");
                highestTransfer = start.TransferId; assembler.Clear();
                if (!assembler.Begin(start)) throw new FormatException("Reassembly budget exceeded.");
                active = start; deadline = now + options.ProgressTimeout;
                return;
            }
            if (kind != ChunkFrameKind.Slice) throw new FormatException("Unexpected server chunk frame.");
            ChunkTransferFrames.DecodeSlice(packet, out ulong id, out int offset, out var bytes);
            if (id != active.TransferId)
            {
                if (id > highestTransfer) throw new FormatException("Slice precedes declaration.");
                return;
            }
            deadline = now + options.ProgressTimeout;
            if (!assembler.AddSlice(id, offset, bytes, out var declaration, out var payload)) return;
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
            response = ChunkTransferFrames.EncodeAcknowledgement(id, image.Revision);
            active = default; retries = 0;
        }

        private void RequestResync(ulong id, double now)
        {
            assembler.Clear(); active = default;
            if (++retries > 2) { fail(); return; }
            response = ChunkTransferFrames.EncodeResync(id); deadline = now + options.ProgressTimeout;
        }

        public void Tick(double now)
        {
            if (disposed) return;
            if (response != null && send(response)) { response = null; deadline = now + options.ProgressTimeout; }
            if (now < deadline) return;
            if (active.TransferId != 0) RequestResync(active.TransferId, now);
            else if (response != null || interest == null || !store.DataReady) fail();
        }
        public void Dispose()
        {
            if (disposed) return;
            assembler.Clear(); response = null; store.ClearReplicas(); disposed = true;
        }
    }
}
