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
        public int AppliesPerTick { get; }
        public ChunkStreamingOptions(int horizontalRadius = 1, int verticalRadius = 0,
            int globalBytesPerTick = 262144, int peerBytesPerTick = 131072, int maxPayloads = 64, double progressTimeout = 10,
            uint worldId = 1, int peerWindow = 8, int appliesPerTick = 2)
        {
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            _ = new ChunkInterest(1, new ChunkAddress(worldId, default), horizontalRadius, verticalRadius);
            if (globalBytesPerTick < BulkDriver.MaxPayloadBytes || globalBytesPerTick > 8388608) throw new ArgumentOutOfRangeException(nameof(globalBytesPerTick));
            if (peerBytesPerTick < BulkDriver.MaxPayloadBytes || peerBytesPerTick > globalBytesPerTick) throw new ArgumentOutOfRangeException(nameof(peerBytesPerTick));
            if (maxPayloads < 2 || maxPayloads > 256) throw new ArgumentOutOfRangeException(nameof(maxPayloads));
            if (peerWindow < 1 || peerWindow > 1024) throw new ArgumentOutOfRangeException(nameof(peerWindow));
            if (appliesPerTick < 1 || appliesPerTick > 64) throw new ArgumentOutOfRangeException(nameof(appliesPerTick));
            if (!(progressTimeout >= 1 && progressTimeout <= 120)) throw new ArgumentOutOfRangeException(nameof(progressTimeout));
            HorizontalRadius = horizontalRadius; VerticalRadius = verticalRadius; GlobalBytesPerTick = globalBytesPerTick;
            PeerBytesPerTick = peerBytesPerTick; MaxPayloads = maxPayloads; ProgressTimeout = progressTimeout; WorldId = worldId;
            PeerWindow = peerWindow; AppliesPerTick = appliesPerTick;
        }
    }

    /// <summary>
    /// Hands one chunk straight to a client store that shares this process. Returns false when the
    /// client cannot take it yet, which means it has not caught up with the interest declaration and
    /// the chunk should simply be offered again.
    /// </summary>
    internal delegate bool ChunkDirectDelivery(ulong peerId, ulong epoch, ChunkAddress address,
        ulong incarnation, ulong revision, PackedChannelData solids, PackedChannelData fluids);

    internal sealed class ChunkStreamingServer : IDisposable
    {
        private readonly ResidentChunkStore store;
        private readonly ChunkStreamingOptions options;
        private readonly Func<ulong, byte[], bool> send;
        private readonly Action<ulong> fail;
        private readonly IAuthoritativeChunkSource source;
        private readonly Func<ulong, int> sliceCapacity;
        //optional single-player shortcut. Consulted per chunk, so flipping it changes how the next
        //chunk arrives rather than requiring a restart, and every other stage is left alone.
        private readonly Func<bool> directEnabled;
        private readonly ChunkDirectDelivery deliverDirect;
        private readonly PackedChannelData directSolids = new(), directFluids = new();
        private readonly Dictionary<ulong, Peer> peers = new();
        private readonly List<ulong> order = new();
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

        //encoded payloads still being sliced, across every peer. This is the memory bound on the
        //transfer stage and stays capped by options.MaxPayloads. A deep window makes the transfer list
        //long, so this is a counter rather than a walk of every peer's transfers.
        public int PayloadCount { get; private set; }
        private long payloadBytes;

        public string DescribePeer(ulong id)
        {
            if (!peers.TryGetValue(id, out var peer)) return "streaming peer is not registered";
            int started = 0, awaitingAck = 0, recoveringChunks = 0, maxTransferRetries = 0;
            foreach (var transfer in peer.Transfers)
            {
                if (transfer.Started) started++;
                if (transfer.AwaitingAck) awaitingAck++;
            }
            foreach (int attempts in peer.TransferRetries)
            {
                if (attempts != 0) recoveringChunks++;
                maxTransferRetries = Math.Max(maxTransferRetries, attempts);
            }
            return $"epoch={peer.Interest?.Epoch ?? 0}; anchor={peer.Interest?.Anchor.ToString() ?? "none"}; " +
                $"declarationQueued={peer.Declaration != null}; transfers={peer.Transfers.Count}; " +
                $"started={started}; awaitingAck={awaitingAck}; payloads={PayloadCount}; " +
                $"declarationRetries={peer.DeclarationRetries}; recoveringChunks={recoveringChunks}; maxTransferRetries={maxTransferRetries}";
        }

        private sealed class Peer
        {
            public ulong Id;
            public ChunkInterest Interest;
            public ChunkLease[] Leases = Array.Empty<ChunkLease>();
            public ulong[] Baselines = Array.Empty<ulong>();
            //Parallel to leases so unrelated transfers never share a retry allowance.
            public int[] TransferRetries = Array.Empty<int>();
            public byte[] Declaration;
            public int Cursor, DeclarationRetries, RoundRobin;
            public bool Failed;
            public double Deadline;
            public readonly List<Transfer> Transfers = new();
        }

        //one chunk on its way to one peer. Several are live at once per peer; at most one per lease index,
        //so the baseline a delta is built against can never be overtaken by another transfer of the same chunk.
        private sealed class Transfer
        {
            public Peer Owner;
            public ulong Id, Incarnation, Revision;
            public int LeaseIndex, Offset;
            public ChunkAddress Address;
            public byte[] Payload;
            public bool Started, AwaitingAck, IsDelta;
            public double Deadline, Began;
        }

        public ChunkStreamingServer(ResidentChunkStore store, ChunkStreamingOptions options, Func<ulong, byte[], bool> send, Action<ulong> fail,
            IAuthoritativeChunkSource source = null, Func<ulong, int> sliceCapacity = null,
            Func<bool> directEnabled = null, ChunkDirectDelivery deliverDirect = null)
        {
            this.store = store; this.options = options; this.send = send; this.fail = fail; this.source = source;
            this.sliceCapacity = sliceCapacity;
            this.directEnabled = directEnabled; this.deliverDirect = deliverDirect;
        }

        public long DirectDeliveries { get; private set; }

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
            peer.TransferRetries = new int[leases.Length];
            for (int i = 0; i < leases.Length; i++) retainedBaselines.TryGetValue(leases[i].Address, out peer.Baselines[i]);
            peer.Declaration = ChunkTransferFrames.EncodeInterest(interest); peer.Cursor = 0; peer.DeclarationRetries = 0; peer.RoundRobin = 0;
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
                peer.Baselines[transfer.LeaseIndex] = revision;
                peer.TransferRetries[transfer.LeaseIndex] = 0;
                AppliedAcknowledgements++;
                MaxAppliedAckSeconds = Math.Max(MaxAppliedAckSeconds, now - transfer.Began);
                Retire(peer, transfer);
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
            store.PumpLoads();
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
                    peer.Declaration = null; peer.DeclarationRetries = 0; peer.Deadline = now + options.ProgressTimeout;
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
            //PeerWindow bounds outstanding chunks, most of which have been sent and hold no payload;
            //MaxPayloads bounds the encoded bytes actually waiting on the wire. They are separate now.
            for (int scanned = 0; scanned < peer.Leases.Length && peer.Transfers.Count < options.PeerWindow &&
                 buffered < options.MaxPayloads; scanned++)
            {
                int index = peer.Cursor;
                peer.Cursor = (peer.Cursor + 1) % peer.Leases.Length;
                if (IsBusy(peer, index)) continue;
                var lease = peer.Leases[index];
                if (peer.Baselines[index] == lease.Revision) continue;
                //a chunk still being generated on a worker is skipped, not waited on; the cursor comes
                //back round to it once PumpLoads has adopted the result.
                if (store.RequestLoad(lease, source) != ChunkLoadState.Loaded) continue;
                if (peer.Baselines[index] == lease.Revision) continue;
                if (deliverDirect != null && directEnabled != null && directEnabled())
                {
                    //nothing is encoded, sliced, reassembled or decoded: the client store is a few
                    //metres away in memory and takes the chunk in the layout it is already held in.
                    //Advancing the baseline here is what keeps deltas working afterwards.
                    ulong revision = store.CopyPacked(lease, directSolids, directFluids);
                    if (deliverDirect(peer.Id, peer.Interest.Epoch, lease.Address, lease.Incarnation, revision, directSolids, directFluids))
                    { peer.Baselines[index] = revision; DirectDeliveries++; }
                    continue;
                }
                var transfer = new Transfer
                {
                    Owner = peer, LeaseIndex = index, Address = lease.Address, Incarnation = lease.Incarnation,
                    Deadline = now + options.ProgressTimeout, Began = now
                };
                peer.Transfers.Add(transfer);
                if (store.TryGetDelta(lease, peer.Baselines[index], out var delta))
                {
                    transfer.Revision = delta.ResultRevision;
                    Prepare(peer, transfer, ChunkWireCodec.EncodeDelta(delta), true, now);
                }
                else
                {
                    //encoding is a palette copy and a word copy now, so it happens here rather than
                    //going to a worker and coming back a tick or more later.
                    transfer.Revision = lease.Revision;
                    Prepare(peer, transfer, store.EncodeSnapshot(lease), false, now);
                }
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
                    if (transfer.Offset == transfer.Payload.Length) { SetPayload(transfer, null); transfer.AwaitingAck = true; }
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
            SetPayload(transfer, payload);
            transfer.Offset = 0; transfer.Started = false; transfer.AwaitingAck = false;
            transfer.IsDelta = delta; transfer.Began = now; transfer.Deadline = now + options.ProgressTimeout;
            PeakEncodedPayloadBytes = Math.Max(PeakEncodedPayloadBytes, (int)Math.Min(int.MaxValue, payloadBytes));
            if (delta) SentDeltas++; else SentSnapshots++;
        }

        //the one place a payload is attached or released, so the slot and byte counts stay exact
        //without anything having to walk the transfer lists.
        private void SetPayload(Transfer transfer, byte[] payload)
        {
            if (transfer.Payload != null) { PayloadCount--; payloadBytes -= transfer.Payload.Length; }
            transfer.Payload = payload;
            if (payload != null) { PayloadCount++; payloadBytes += payload.Length; }
        }

        private void RetryTransfer(Peer peer, Transfer transfer, double now)
        {
            int leaseIndex = transfer.LeaseIndex;
            peer.Baselines[leaseIndex] = 0;
            Retire(peer, transfer);
            peer.Deadline = now + options.ProgressTimeout;
            if (++peer.TransferRetries[leaseIndex] > 2) { peer.Failed = true; fail(peer.Id); }
        }

        private void RetryPeer(Peer peer, double now)
        {
            CancelAll(peer);
            peer.Deadline = now + options.ProgressTimeout;
            if (++peer.DeclarationRetries > 2) { peer.Failed = true; fail(peer.Id); }
        }

        private void Retire(Peer peer, Transfer transfer)
        {
            SetPayload(transfer, null);
            peer.Transfers.Remove(transfer);
            if (peer.Transfers.Count == 0) peer.RoundRobin = 0;
        }

        private void CancelAll(Peer peer)
        {
            foreach (var transfer in peer.Transfers) SetPayload(transfer, null);
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
            peers.Clear(); order.Clear(); disposed = true;
        }
    }
}
