using System;
using System.Collections.Generic;

namespace DigBlocks.Networking
{
    public enum NetworkFailure : byte
    {
        None, ProtocolMismatch, InvalidName, InvalidIdentity, DuplicateIdentity,
        ServerFull, InvalidResponse, Rejected, TimedOut, TransportClosed,
        ListenFailed, Cancelled, Kicked, ServerStopping,
        ChunkChannelFailed, ChunkBindingTimedOut, ChunkRegistryMismatch, ChunkLayoutMismatch
    }

    public sealed class NetworkSessionException : Exception
    {
        public NetworkFailure Reason { get; }
        public NetworkSessionException(NetworkFailure reason) : base($"Network session failed: {reason}.") => Reason = reason;
    }

    public readonly struct PeerRecord
    {
        public ulong PeerId { get; }
        public ulong OfflineXuid { get; }
        public string DisplayName { get; }
        public PeerRecord(ulong peerId, ulong offlineXuid, string displayName)
        {
            PeerId = peerId;
            OfflineXuid = offlineXuid;
            DisplayName = displayName;
        }
    }

    //admission runs on the main thread; slots include approved peers awaiting native connection completion.
    public sealed class AdmissionRegistry
    {
        private readonly NetworkSessionOptions options;
        private readonly Dictionary<ulong, PeerRecord> connections = new();
        private IReadOnlyList<PeerRecord> peers = Array.Empty<PeerRecord>();
        private ulong nextPeerId;
        public IReadOnlyList<PeerRecord> Peers => peers;
        public AdmissionRegistry(NetworkSessionOptions options) => this.options = options;

        public NetworkFailure Admit(ulong connection, uint protocol, ulong xuid, string name, out PeerRecord peer)
        {
            peer = default;
            if (protocol != options.ProtocolVersion) return NetworkFailure.ProtocolMismatch;
            if (xuid == 0) return NetworkFailure.InvalidIdentity;
            if (!NetworkSessionOptions.IsValidName(name)) return NetworkFailure.InvalidName;
            if (connections.TryGetValue(connection, out peer))
                return peer.OfflineXuid == xuid && peer.DisplayName == name ? NetworkFailure.None : NetworkFailure.InvalidResponse;
            foreach (var existing in connections.Values)
                if (existing.OfflineXuid == xuid) return NetworkFailure.DuplicateIdentity;
            if (connections.Count >= options.Capacity) return NetworkFailure.ServerFull;

            peer = new PeerRecord(++nextPeerId, xuid, name);
            connections.Add(connection, peer);
            Refresh();
            return NetworkFailure.None;
        }

        public void Release(ulong connection) { if (connections.Remove(connection)) Refresh(); }
        public void Clear() { connections.Clear(); peers = Array.Empty<PeerRecord>(); }
        private void Refresh() => peers = new List<PeerRecord>(connections.Values).AsReadOnly();
    }
}
