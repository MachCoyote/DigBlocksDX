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
        public int PeerWindow { get; }
        public int SnapshotWorkers { get; }
        public int AppliesPerTick { get; }
        public ChunkStreamingOptions(int horizontalRadius = 1, int verticalRadius = 0,
            int globalBytesPerTick = 262144, int peerBytesPerTick = 131072, int maxPayloads = 64, double progressTimeout = 10,
            uint worldId = 1, int peerWindow = 8, int snapshotWorkers = 16, int appliesPerTick = 2)
        {
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            _ = new ChunkInterest(1, new ChunkAddress(worldId, default), horizontalRadius, verticalRadius);
            if (globalBytesPerTick < BulkDriver.MaxPayloadBytes || globalBytesPerTick > 8388608) throw new ArgumentOutOfRangeException(nameof(globalBytesPerTick));
            if (peerBytesPerTick < BulkDriver.MaxPayloadBytes || peerBytesPerTick > globalBytesPerTick) throw new ArgumentOutOfRangeException(nameof(peerBytesPerTick));
            if (maxPayloads < 2 || maxPayloads > 256) throw new ArgumentOutOfRangeException(nameof(maxPayloads));
            if (peerWindow < 1 || peerWindow > 64) throw new ArgumentOutOfRangeException(nameof(peerWindow));
            //a peer can never hold more in flight than the shared payload budget allows.
            peerWindow = Math.Min(peerWindow, maxPayloads);
            if (snapshotWorkers < 1 || snapshotWorkers > 64) throw new ArgumentOutOfRangeException(nameof(snapshotWorkers));
            if (appliesPerTick < 1 || appliesPerTick > 64) throw new ArgumentOutOfRangeException(nameof(appliesPerTick));
            if (!(progressTimeout >= 1 && progressTimeout <= 120)) throw new ArgumentOutOfRangeException(nameof(progressTimeout));
            HorizontalRadius = horizontalRadius; VerticalRadius = verticalRadius; GlobalBytesPerTick = globalBytesPerTick;
            PeerBytesPerTick = peerBytesPerTick; MaxPayloads = maxPayloads; ProgressTimeout = progressTimeout; WorldId = worldId;
            PeerWindow = peerWindow; SnapshotWorkers = snapshotWorkers; AppliesPerTick = appliesPerTick;
        }
    }

    internal sealed class ChunkStreamingServer : IDisposable
    {
        private readonly ResidentChunkStore store;
        private readonly ChunkStreamingOptions options;
        private readonly Func<ulong, byte[], bool> send;
        private readonly Action<ulong> fail;
        private readonly IAuthoritativeChunkSource source;
        private readonly Func<ulong, int> sliceCapacity;
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly List<ulong> order = new();
        private readonly Dictionary<ulong, Transfer> requests = new();
        private readonly List<Transfer> expired = new();
        private int cursor;
        private ulong nextTransfer = 1;
        private bool disposed;
        public long SentBytes { get; private set; }
        public long AppliedAcknowledgements { get; private set; }
        public long SentSnapshots { get; private set; }
        public long SentDeltas { get; private set; }
        public double MaxAppliedAckSeconds { get; private set; }
        public int PeakEncodedPayloadBytes { get; private set; }

        //payload slots held across every peer: an encode in flight or an encoded payload still being sliced.
        //This is the memory bound on the transfer stage and stays capped by options.MaxPayloads.
        public int PayloadCount
        {
            get
            {
                int count = 0;
                foreach (var peer in peers.Values)
                    foreach (var transfer in peer.Transfers)
                        if (transfer.RequestId != 0 || transfer.Payload != null) count++;
                return count;
            }
        }

        private sealed class Peer
        {
            public ulong Id;
            public ChunkInterest Interest;
            public ChunkLease[] Leases = Array.Empty<ChunkLease>();
            public ulong[] Baselines;
            public byte[] Declaration;
            public int Cursor, Retries, RoundRobin;
            public bool Failed;
            public double Deadline;
            public readonly List<Transfer> Transfers = new();
        }

        //one chunk on its way to one peer. Several are live at once per peer; at most one per lease index,
        //so the baseline a delta is built against can never be overtaken by another transfer of the same chunk.
        private sealed class Transfer
        {
            public Peer Owner;
            public ulong Id, RequestId, Incarnation, Revision;
            public int LeaseIndex, Offset;
            public ChunkAddress Address;
            public byte[] Payload;
            public bool Started, AwaitingAck, IsDelta;
            public double Deadline, Began;
        }

        public ChunkStreamingServer(ResidentChunkStore store, ChunkStreamingOptions options, Func<ulong, byte[], bool> send, Action<ulong> fail,
            IAuthoritativeChunkSource source = null, Func<ulong, int> sliceCapacity = null)
        {
            this.store = store; this.options = options; this.send = send; this.fail = fail; this.source = source;
            this.sliceCapacity = sliceCapacity;
        }

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
            CancelAll(peer);
            peer.Interest = interest; peer.Leases = leases; peer.Baselines = new ulong[leases.Length];
            for (int i = 0; i < leases.Length; i++) retainedBaselines.TryGetValue(leases[i].Address, out peer.Baselines[i]);
            peer.Declaration = ChunkTransferFrames.EncodeInterest(interest); peer.Cursor = 0; peer.Retries = 0; peer.RoundRobin = 0;
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
            ulong id2;
            if (kind == ChunkFrameKind.Acknowledgement)
            {
                ChunkTransferFrames.DecodeAcknowledgement(packet, out id2, out ulong revision);
                if (id2 >= nextTransfer) throw new FormatException("Unknown applied acknowledgement.");
                var transfer = Find(peer, id2);
                if (transfer == null) return;
                if (!transfer.AwaitingAck || revision != transfer.Revision) throw new FormatException("Invalid applied acknowledgement.");
                peer.Baselines[transfer.LeaseIndex] = revision; AppliedAcknowledgements++;
                MaxAppliedAckSeconds = Math.Max(MaxAppliedAckSeconds, now - transfer.Began);
                Retire(peer, transfer); peer.Retries = 0;
                return;
            }
            if (kind != ChunkFrameKind.Resync) throw new FormatException("Unexpected client chunk frame.");
            id2 = ChunkTransferFrames.DecodeResync(packet);
            if (id2 >= nextTransfer) throw new FormatException("Unknown resync transfer.");
            var pending = Find(peer, id2);
            if (pending != null) RetryTransfer(peer, pending, now);
        }

        public void Tick(double now)
        {
            if (disposed) return;
            store.PumpSnapshots();
            while (store.TryTakeSnapshot(out var result))
            {
                if (!requests.Remove(result.RequestId, out var transfer)) continue;
                var peer = transfer.Owner;
                transfer.RequestId = 0;
                if (result.Error != null) { peer.Failed = true; fail(peer.Id); continue; }
                var lease = peer.Leases[transfer.LeaseIndex];
                if (!result.Address.Equals(lease.Address) || result.Incarnation != lease.Incarnation)
                { peer.Transfers.Remove(transfer); continue; }
                transfer.Revision = result.Revision;
                Prepare(peer, transfer, result.Payload, false, now);
            }
            int budget = options.GlobalBytesPerTick;
            int count = order.Count;
            if (count == 0) return;
            cursor %= count;
            //rotate first access so no peer permanently wins the shared byte budget.
            for (int n = 0; n < count && budget > 0; n++)
            {
                ulong id = order[(cursor + n) % count];
                if (!peers.TryGetValue(id, out var peer) || peer.Failed) continue;
                int allowance = Math.Min(budget, options.PeerBytesPerTick);
                int slice = SliceLimit(peer);
                if (slice <= 0) continue;

                if (peer.Declaration != null)
                {
                    if (now >= peer.Deadline) { RetryPeer(peer, now); continue; }
                    if (!Send(peer, peer.Declaration, ref allowance, ref budget)) continue;
                    peer.Declaration = null; peer.Deadline = now + options.ProgressTimeout;
                }

                expired.Clear();
                foreach (var transfer in peer.Transfers) if (now >= transfer.Deadline) expired.Add(transfer);
                foreach (var transfer in expired) RetryTransfer(peer, transfer, now);
                if (peer.Failed) continue;

                StartTransfers(peer, now);
                Emit(peer, slice, ref allowance, ref budget, now);
            }
            cursor = (cursor + 1) % count;
        }

        //fills the peer's window from the interest order, so encoding runs ahead of the wire instead of
        //stalling it. Closest-first is preserved by the lease order; the cursor only skips settled chunks.
        private void StartTransfers(Peer peer, double now)
        {
            if (peer.Leases.Length == 0) return;
            int buffered = PayloadCount;
            for (int scanned = 0; scanned < peer.Leases.Length && peer.Transfers.Count < options.PeerWindow &&
                 buffered < options.MaxPayloads; scanned++)
            {
                int index = peer.Cursor;
                peer.Cursor = (peer.Cursor + 1) % peer.Leases.Length;
                if (IsBusy(peer, index)) continue;
                var lease = peer.Leases[index];
                if (peer.Baselines[index] == lease.Revision) continue;
                store.EnsureLoaded(lease, source);
                if (peer.Baselines[index] == lease.Revision) continue;
                var transfer = new Transfer
                {
                    Owner = peer, LeaseIndex = index, Address = lease.Address, Incarnation = lease.Incarnation,
                    Deadline = now + options.ProgressTimeout, Began = now
                };
                if (store.TryGetDelta(lease, peer.Baselines[index], out var delta))
                {
                    peer.Transfers.Add(transfer); transfer.Revision = delta.ResultRevision;
                    Prepare(peer, transfer, ChunkWireCodec.EncodeDelta(delta), true, now);
                }
                else if (store.TryRequestSnapshot(lease, out ulong request))
                {
                    transfer.RequestId = request; requests.Add(request, transfer); peer.Transfers.Add(transfer);
                }
                else break;
                buffered++;
            }
        }

        //round-robin across the peer's live transfers so one large chunk cannot monopolise the link.
        private void Emit(Peer peer, int slice, ref int allowance, ref int budget, double now)
        {
            int idle = 0;
            while (allowance > 0 && budget > 0 && peer.Transfers.Count > 0 && idle < peer.Transfers.Count)
            {
                var transfer = peer.Transfers[peer.RoundRobin % peer.Transfers.Count];
                peer.RoundRobin = (peer.RoundRobin + 1) % peer.Transfers.Count;
                if (transfer.Payload == null) { idle++; continue; }
                int length = 0;
                byte[] packet;
                if (!transfer.Started)
                    packet = ChunkTransferFrames.EncodeStart(new TransferStart(transfer.Id, peer.Interest.Epoch, transfer.Address,
                        transfer.Incarnation, transfer.Revision, transfer.Payload.Length, transfer.IsDelta));
                else
                {
                    length = Math.Min(slice, transfer.Payload.Length - transfer.Offset);
                    packet = ChunkTransferFrames.EncodeSlice(transfer.Id, transfer.Offset, transfer.Payload, transfer.Offset, length);
                }
                if (packet.Length > allowance || packet.Length > budget || !Send(peer, packet, ref allowance, ref budget)) break;
                if (!transfer.Started) transfer.Started = true;
                else
                {
                    transfer.Offset += length;
                    if (transfer.Offset == transfer.Payload.Length) { transfer.Payload = null; transfer.AwaitingAck = true; }
                }
                transfer.Deadline = now + options.ProgressTimeout;
                idle = 0;
            }
        }

        private int SliceLimit(Peer peer)
        {
            if (sliceCapacity == null) return ChunkTransferFrames.MaxSliceBytes;
            int capacity = sliceCapacity(peer.Id);
            if (capacity <= 20) return 0;
            return Math.Min(ChunkTransferFrames.MaxSliceBytes, capacity - 20);
        }

        private static Transfer Find(Peer peer, ulong id)
        {
            foreach (var transfer in peer.Transfers) if (transfer.Id == id && id != 0) return transfer;
            return null;
        }

        private static bool IsBusy(Peer peer, int leaseIndex)
        {
            foreach (var transfer in peer.Transfers) if (transfer.LeaseIndex == leaseIndex) return true;
            return false;
        }

        private bool Send(Peer peer, byte[] packet, ref int allowance, ref int budget)
        {
            if (packet.Length > allowance || !send(peer.Id, packet)) return false;
            allowance -= packet.Length; budget -= packet.Length; SentBytes += packet.Length; return true;
        }

        private void Prepare(Peer peer, Transfer transfer, byte[] payload, bool delta, double now)
        {
            if (nextTransfer == ulong.MaxValue) throw new InvalidOperationException("Transfer ID exhausted.");
            transfer.Id = nextTransfer++;
            transfer.Payload = payload; transfer.Offset = 0; transfer.Started = false; transfer.AwaitingAck = false;
            transfer.IsDelta = delta; transfer.Began = now; transfer.Deadline = now + options.ProgressTimeout;
            int buffered = 0;
            foreach (var item in peers.Values)
                foreach (var live in item.Transfers) buffered += live.Payload?.Length ?? 0;
            PeakEncodedPayloadBytes = Math.Max(PeakEncodedPayloadBytes, buffered);
            if (delta) SentDeltas++; else SentSnapshots++;
        }

        private void RetryTransfer(Peer peer, Transfer transfer, double now)
        {
            peer.Baselines[transfer.LeaseIndex] = 0;
            Retire(peer, transfer);
            peer.Deadline = now + options.ProgressTimeout;
            if (++peer.Retries > 2) { peer.Failed = true; fail(peer.Id); }
        }

        private void RetryPeer(Peer peer, double now)
        {
            CancelAll(peer);
            peer.Deadline = now + options.ProgressTimeout;
            if (++peer.Retries > 2) { peer.Failed = true; fail(peer.Id); }
        }

        private void Retire(Peer peer, Transfer transfer)
        {
            if (transfer.RequestId != 0) { store.CancelSnapshot(transfer.RequestId); requests.Remove(transfer.RequestId); }
            transfer.RequestId = 0; transfer.Payload = null;
            peer.Transfers.Remove(transfer);
            if (peer.Transfers.Count == 0) peer.RoundRobin = 0;
        }

        private void CancelAll(Peer peer)
        {
            for (int i = peer.Transfers.Count - 1; i >= 0; i--)
            {
                var transfer = peer.Transfers[i];
                if (transfer.RequestId != 0) { store.CancelSnapshot(transfer.RequestId); requests.Remove(transfer.RequestId); }
            }
            peer.Transfers.Clear(); peer.RoundRobin = 0;
        }

        public void Remove(ulong id)
        {
            if (!peers.Remove(id, out var peer)) return;
            CancelAll(peer); foreach (var lease in peer.Leases) lease.Dispose(); order.Remove(id);
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (var peer in peers.Values) { CancelAll(peer); foreach (var lease in peer.Leases) lease.Dispose(); }
            peers.Clear(); order.Clear(); requests.Clear(); disposed = true;
        }
    }
}
