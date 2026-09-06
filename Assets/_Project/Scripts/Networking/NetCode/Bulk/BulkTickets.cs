using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace DigBlocks.Networking.NetCode
{
    public readonly struct BulkTicket : IEquatable<BulkTicket>
    {
        public BulkTicket(ulong high, ulong low) { High = high; Low = low; }
        public ulong High { get; }
        public ulong Low { get; }
        public bool Equals(BulkTicket other) => High == other.High && Low == other.Low;
        public override bool Equals(object other) => other is BulkTicket ticket && Equals(ticket);
        public override int GetHashCode() => HashCode.Combine(High, Low);
    }

    public sealed class BulkTickets
    {
        private readonly int capacity;
        private readonly Dictionary<BulkTicket, Binding> tickets = new();
        private readonly List<BulkTicket> removal = new();

        private readonly struct Binding
        {
            public Binding(ulong peer, ulong generation, double expires)
            { Peer = peer; Generation = generation; Expires = expires; }
            public readonly ulong Peer;
            public readonly ulong Generation;
            public readonly double Expires;
        }

        public BulkTickets(int capacity = 64)
        {
            if (capacity < 1 || capacity > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public BulkTicket Issue(ulong peerId, ulong generation, double now, double lifetimeSeconds = 10)
        {
            if (peerId == 0) throw new ArgumentOutOfRangeException(nameof(peerId));
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            ValidateTime(now);
            if (!(lifetimeSeconds > 0 && lifetimeSeconds <= 120)) throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));
            double expires = now + lifetimeSeconds;
            if (double.IsInfinity(expires)) throw new ArgumentOutOfRangeException(nameof(now));
            removal.Clear();
            foreach (var pair in tickets)
                if (pair.Value.Expires <= now || pair.Value.Peer == peerId) removal.Add(pair.Key);
            foreach (var key in removal) tickets.Remove(key);
            if (tickets.Count >= capacity) throw new InvalidOperationException("Bulk ticket capacity is exhausted.");

            //a fresh channel binding secret, not an account credential or replacement for encryption.
            using var random = RandomNumberGenerator.Create();
            var bytes = new byte[16];
            BulkTicket ticket;
            do
            {
                random.GetBytes(bytes);
                ticket = new BulkTicket(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
            } while (ticket.Equals(default) || tickets.ContainsKey(ticket));
            tickets.Add(ticket, new Binding(peerId, generation, expires));
            return ticket;
        }

        public bool TryConsume(BulkTicket ticket, ulong peerId, double now, out ulong generation)
        {
            ValidateTime(now);
            generation = 0;
            if (!tickets.TryGetValue(ticket, out var binding)) return false;
            if (binding.Expires <= now) { tickets.Remove(ticket); return false; }
            if (binding.Peer != peerId) return false;
            tickets.Remove(ticket);
            generation = binding.Generation;
            return true;
        }

        public void Revoke(ulong peerId)
        {
            removal.Clear();
            foreach (var pair in tickets)
                if (pair.Value.Peer == peerId) removal.Add(pair.Key);
            foreach (var key in removal) tickets.Remove(key);
        }

        public void Clear() { tickets.Clear(); removal.Clear(); }

        private static void ValidateTime(double now)
        {
            if (!(now >= 0) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        }
    }
}
