using System;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;

namespace DigBlocks.Networking.NetCode
{
    public sealed class ChunkStreamingOptions
    {
        internal Unity.Networking.Transport.Utilities.SimulatorUtility.Parameters? Simulation { get; set; }
        public int HorizontalRadius { get; }
        public int VerticalRadius { get; }
        public int GlobalBytesPerTick { get; }
        public int PeerBytesPerTick { get; }
        public int MaxPayloads { get; }
        public double ProgressTimeout { get; }
        public uint WorldId { get; }
        public ChunkStreamingOptions(int horizontalRadius = 1, int verticalRadius = 0,
            int globalBytesPerTick = 16384, int peerBytesPerTick = 4096, int maxPayloads = 4, double progressTimeout = 10,
            uint worldId = 1)
        {
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            _ = new ChunkInterest(1, new ChunkAddress(worldId, default), horizontalRadius, verticalRadius);
            if (globalBytesPerTick < BulkDriver.MaxPayloadBytes || globalBytesPerTick > 1048576) throw new ArgumentOutOfRangeException(nameof(globalBytesPerTick));
            if (peerBytesPerTick < BulkDriver.MaxPayloadBytes || peerBytesPerTick > globalBytesPerTick) throw new ArgumentOutOfRangeException(nameof(peerBytesPerTick));
            if (maxPayloads < 2 || maxPayloads > 16) throw new ArgumentOutOfRangeException(nameof(maxPayloads));
            if (!(progressTimeout >= 1 && progressTimeout <= 120)) throw new ArgumentOutOfRangeException(nameof(progressTimeout));
            HorizontalRadius = horizontalRadius; VerticalRadius = verticalRadius; GlobalBytesPerTick = globalBytesPerTick;
            PeerBytesPerTick = peerBytesPerTick; MaxPayloads = maxPayloads; ProgressTimeout = progressTimeout; WorldId = worldId;
        }
    }

    internal sealed class ChunkStreamingServer : IDisposable
    {
        private readonly ResidentChunkStore store;
        private readonly ChunkStreamingOptions options;
        private readonly Func<ulong, byte[], bool> send;
        private readonly Action<ulong> fail;
        private readonly IAuthoritativeChunkSource source;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly List<ulong> order = new();
        private readonly Dictionary<ulong, Peer> requests = new();
        private int cursor;
        private ulong nextTransfer = 1;
        private bool disposed;
        public long SentBytes { get; private set; }
        public long AppliedAcknowledgements { get; private set; }
        public long SentSnapshots { get; private set; }
        public long SentDeltas { get; private set; }
        public double MaxAppliedAckSeconds { get; private set; }
        public int PeakEncodedPayloadBytes { get; private set; }
        public int PayloadCount
        {
            get { int count = 0; foreach (var peer in peers.Values) if (peer.Request != 0 || peer.Payload != null) count++; return count; }
        }
        private sealed class Peer
        {
            public ulong Id;
            public ChunkInterest Interest;
            public ChunkLease[] Leases = Array.Empty<ChunkLease>();
            public ulong[] Baselines;
            public byte[] Declaration, Payload;
            public int Index, Offset, Retries;
            public ulong Request;
            public TransferStart Transfer;
            public bool Started, AwaitingAck, Failed;
            public double Deadline, TransferBegan;
        }

        public ChunkStreamingServer(ResidentChunkStore store, ChunkStreamingOptions options, Func<ulong, byte[], bool> send, Action<ulong> fail,
            IAuthoritativeChunkSource source = null)
        { this.store = store; this.options = options; this.send = send; this.fail = fail; this.source = source; }

        public bool Add(ulong id, double now)
        {
            var peer = new Peer { Id = id };
            if (!SetInterest(peer, new ChunkAddress(options.WorldId, default), options.HorizontalRadius, options.VerticalRadius, now)) return false;
            peers.Add(id, peer); order.Add(id); return true;
        }

        public bool SetInterest(ulong id, ChunkAddress anchor, int horizontal, int vertical, double now) =>
            peers.TryGetValue(id, out var peer) && SetInterest(peer, anchor, horizontal, vertical, now);

        private bool SetInterest(Peer peer, ChunkAddress anchor, int horizontal, int vertical, double now)
        {
            if (peer.Failed) return false;
            if (peer.Interest != null && peer.Interest.Anchor.Equals(anchor) && peer.Interest.HorizontalRadius == horizontal &&
                peer.Interest.VerticalRadius == vertical) return true;
            var interest = new ChunkInterest(checked((peer.Interest?.Epoch ?? 0) + 1), anchor, horizontal, vertical);
            var retainedBaselines = new Dictionary<ChunkAddress, ulong>();
            for (int i = 0; i < peer.Leases.Length; i++) retainedBaselines.Add(peer.Leases[i].Address, peer.Baselines[i]);
            if (!store.TryReplaceLeases(peer.Leases, interest.Addresses(), out var leases)) return false;
            CancelTransfer(peer);
            peer.Interest = interest; peer.Leases = leases; peer.Baselines = new ulong[leases.Length];
            for (int i = 0; i < leases.Length; i++) retainedBaselines.TryGetValue(leases[i].Address, out peer.Baselines[i]);
            peer.Declaration = ChunkTransferFrames.EncodeInterest(interest); peer.Index = 0; peer.Retries = 0;
            peer.Deadline = now + options.ProgressTimeout;
            return true;
        }

        public void Receive(ulong id, byte[] packet, double now)
        {
            if (!peers.TryGetValue(id, out var peer)) throw new FormatException("No live streaming peer.");
            if (peer.Failed) return;
            var kind = ChunkTransferFrames.ReadKind(packet);
            if (kind == ChunkFrameKind.InterestRequest)
            {
                var anchor = ChunkTransferFrames.DecodeInterestRequest(packet);
                if (anchor.World != options.WorldId) throw new ArgumentException("Client interest requested the wrong world.", nameof(packet));
                if (!SetInterest(peer, anchor, options.HorizontalRadius, options.VerticalRadius, now))
                    throw new InvalidOperationException("Server chunk residency capacity exhausted.");
                return;
            }
            ulong transfer;
            if (kind == ChunkFrameKind.Acknowledgement)
            {
                ChunkTransferFrames.DecodeAcknowledgement(packet, out transfer, out ulong revision);
                if (transfer >= nextTransfer) throw new FormatException("Unknown applied acknowledgement.");
                if (transfer != peer.Transfer.TransferId) return;
                if (!peer.AwaitingAck || revision != peer.Transfer.Revision) throw new FormatException("Invalid applied acknowledgement.");
                peer.Baselines[peer.Index] = revision; AppliedAcknowledgements++;
                MaxAppliedAckSeconds = Math.Max(MaxAppliedAckSeconds, now - peer.TransferBegan);
                CancelTransfer(peer); peer.Retries = 0;
                peer.Index = (peer.Index + 1) % peer.Leases.Length;
                return;
            }
            if (kind != ChunkFrameKind.Resync) throw new FormatException("Unexpected client chunk frame.");
            transfer = ChunkTransferFrames.DecodeResync(packet);
            if (transfer >= nextTransfer) throw new FormatException("Unknown resync transfer.");
            if (transfer == peer.Transfer.TransferId) Retry(peer, now);
        }

        public void Tick(double now)
        {
            if (disposed) return;
            store.PumpSnapshots();
            while (store.TryTakeSnapshot(out var result))
            {
                if (!requests.Remove(result.RequestId, out var peer)) continue;
                peer.Request = 0;
                if (result.Error != null) { peer.Failed = true; fail(peer.Id); continue; }
                if (!result.Address.Equals(peer.Leases[peer.Index].Address) || result.Incarnation != peer.Leases[peer.Index].Incarnation) continue;
                Prepare(peer, result.Payload, result.Revision, false, now);
            }
            int budget = options.GlobalBytesPerTick;
            int count = order.Count;
            if (count == 0) return;
            cursor %= count;
            //rotate first access to both capture slots and byte budget; waiting ACKs retain no payload slot.
            for (int n = 0; n < count; n++)
            {
                ulong id = order[(cursor + n) % count];
                if (!peers.TryGetValue(id, out var peer) || peer.Failed) continue;
                if ((peer.Declaration != null || peer.Request != 0 || peer.Transfer.TransferId != 0) && now >= peer.Deadline)
                { Retry(peer, now); continue; }
                int allowance = Math.Min(budget, options.PeerBytesPerTick);
                if (peer.Declaration != null)
                {
                    if (!Send(peer, peer.Declaration, ref allowance, ref budget)) continue;
                    peer.Declaration = null; peer.Deadline = now + options.ProgressTimeout;
                }
                if (peer.Transfer.TransferId == 0 && peer.Request == 0 && PayloadCount < options.MaxPayloads)
                {
                    for (int i = 0; i < peer.Leases.Length; i++)
                    {
                        if (peer.Baselines[peer.Index] != peer.Leases[peer.Index].Revision) break;
                        peer.Index = (peer.Index + 1) % peer.Leases.Length;
                    }
                    var lease = peer.Leases[peer.Index];
                    if (peer.Baselines[peer.Index] != lease.Revision)
                    {
                        store.EnsureLoaded(lease, source);
                        if (store.TryGetDelta(lease, peer.Baselines[peer.Index], out var delta))
                            Prepare(peer, ChunkWireCodec.EncodeDelta(delta), delta.ResultRevision, true, now);
                        else if (store.TryRequestSnapshot(lease, out ulong request))
                        { peer.Request = request; requests.Add(request, peer); peer.Deadline = now + options.ProgressTimeout; }
                    }
                }
                if (peer.Payload == null) continue;
                if (!peer.Started)
                {
                    if (!Send(peer, ChunkTransferFrames.EncodeStart(peer.Transfer), ref allowance, ref budget)) continue;
                    peer.Started = true; peer.Deadline = now + options.ProgressTimeout;
                }
                while (peer.Offset < peer.Payload.Length)
                {
                    int length = Math.Min(ChunkTransferFrames.MaxSliceBytes, peer.Payload.Length - peer.Offset);
                    if (allowance < length + 20 || budget < length + 20) break;
                    var packet = ChunkTransferFrames.EncodeSlice(peer.Transfer.TransferId, peer.Offset, peer.Payload, peer.Offset, length);
                    if (!Send(peer, packet, ref allowance, ref budget)) break;
                    peer.Offset += length; peer.Deadline = now + options.ProgressTimeout;
                }
                if (peer.Offset == peer.Payload.Length) { peer.Payload = null; peer.AwaitingAck = true; }
            }
            cursor = (cursor + 1) % count;
        }

        private bool Send(Peer peer, byte[] packet, ref int allowance, ref int budget)
        {
            if (packet.Length > allowance || !send(peer.Id, packet)) return false;
            allowance -= packet.Length; budget -= packet.Length; SentBytes += packet.Length; return true;
        }
        private void Prepare(Peer peer, byte[] payload, ulong revision, bool delta, double now)
        {
            if (nextTransfer == ulong.MaxValue) throw new InvalidOperationException("Transfer ID exhausted.");
            var lease = peer.Leases[peer.Index];
            peer.Transfer = new TransferStart(nextTransfer++, peer.Interest.Epoch, lease.Address, lease.Incarnation, revision, payload.Length, delta);
            peer.Payload = payload; peer.Offset = 0; peer.Started = false; peer.AwaitingAck = false; peer.TransferBegan = now;
            int buffered = 0;
            foreach (var item in peers.Values) buffered += item.Payload?.Length ?? 0;
            PeakEncodedPayloadBytes = Math.Max(PeakEncodedPayloadBytes, buffered);
            peer.Deadline = now + options.ProgressTimeout;
            if (delta) SentDeltas++; else SentSnapshots++;
        }
        private void Retry(Peer peer, double now)
        {
            CancelTransfer(peer); peer.Baselines[peer.Index] = 0;
            peer.Deadline = now + options.ProgressTimeout;
            if (++peer.Retries > 2) { peer.Failed = true; fail(peer.Id); }
        }
        private void CancelTransfer(Peer peer)
        {
            if (peer.Request != 0) { store.CancelSnapshot(peer.Request); requests.Remove(peer.Request); }
            peer.Request = 0; peer.Payload = null; peer.Transfer = default; peer.Offset = 0;
            peer.Started = false; peer.AwaitingAck = false;
        }
        public void Remove(ulong id)
        {
            if (!peers.Remove(id, out var peer)) return;
            CancelTransfer(peer); foreach (var lease in peer.Leases) lease.Dispose(); order.Remove(id);
        }
        public void Dispose()
        {
            if (disposed) return;
            foreach (var peer in peers.Values) { CancelTransfer(peer); foreach (var lease in peer.Leases) lease.Dispose(); }
            peers.Clear(); order.Clear(); disposed = true;
        }
    }
}
