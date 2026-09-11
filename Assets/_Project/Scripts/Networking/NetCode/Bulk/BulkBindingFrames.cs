using System;
using System.IO;
using System.Text;
using DigBlocks.ChunkProtocol;
namespace DigBlocks.Networking.NetCode
{
    public readonly struct BulkBindRequest
    {
        public readonly ulong PeerId, Generation;
        public readonly BulkTicket Ticket;
        public readonly ushort Edge;
        //the client's own configured view distance. The server streams min(this, its own), so a client
        //never receives more than it is built to hold and is never forced past what it asked for.
        public readonly ushort HorizontalRadius, VerticalRadius;
        public readonly string Fingerprint;
        public BulkBindRequest(ulong peerId, ulong generation, BulkTicket ticket, ushort edge,
            ushort horizontalRadius, ushort verticalRadius, string fingerprint)
        {
            PeerId = peerId; Generation = generation; Ticket = ticket; Edge = edge;
            HorizontalRadius = horizontalRadius; VerticalRadius = verticalRadius; Fingerprint = fingerprint;
        }
    }
    public static class BulkBindingFrames
    {
        public static byte[] EncodeRequest(BulkBindRequest request)
        {
            if (request.PeerId == 0 || request.Generation == 0 || request.Ticket.Equals(default) || request.Edge == 0 || !ValidFingerprint(request.Fingerprint))
                throw new ArgumentException("Invalid companion binding request.");
            if (request.HorizontalRadius > ChunkInterest.MaximumRadius || request.VerticalRadius > ChunkInterest.MaximumRadius)
                throw new ArgumentException("Binding request exceeds the interest radius limit.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            Header(writer, 1); writer.Write(request.PeerId); writer.Write(request.Generation);
            writer.Write(request.Ticket.High); writer.Write(request.Ticket.Low); writer.Write(request.Edge);
            writer.Write(request.HorizontalRadius); writer.Write(request.VerticalRadius);
            for (int i = 0; i < 64; i += 2) writer.Write((byte)((Hex(request.Fingerprint[i]) << 4) | Hex(request.Fingerprint[i + 1])));
            return stream.ToArray();
        }
        public static BulkBindRequest DecodeRequest(byte[] packet)
        {
            using var reader = Open(packet, 1, 74);
            ulong peer = reader.ReadUInt64(), generation = reader.ReadUInt64();
            var ticket = new BulkTicket(reader.ReadUInt64(), reader.ReadUInt64());
            ushort edge = reader.ReadUInt16();
            ushort horizontal = reader.ReadUInt16(), vertical = reader.ReadUInt16();
            var hash = new StringBuilder(64);
            for (int i = 0; i < 32; i++) hash.Append(reader.ReadByte().ToString("x2"));
            if (peer == 0 || generation == 0 || ticket.Equals(default) || edge == 0) throw new FormatException("Invalid companion credentials.");
            //a peer chooses its own view distance, so this is validated rather than trusted.
            if (horizontal > ChunkInterest.MaximumRadius || vertical > ChunkInterest.MaximumRadius)
                throw new FormatException("Binding request exceeds the interest radius limit.");
            return new BulkBindRequest(peer, generation, ticket, edge, horizontal, vertical, hash.ToString());
        }
        public static byte[] EncodeAccepted(ulong peerId, ulong generation)
        {
            if (peerId == 0 || generation == 0) throw new ArgumentException("Invalid binding identity.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            Header(writer, 2); writer.Write(peerId); writer.Write(generation); return stream.ToArray();
        }
        public static void DecodeAccepted(byte[] packet, out ulong peerId, out ulong generation)
        {
            using var reader = Open(packet, 2, 20); peerId = reader.ReadUInt64(); generation = reader.ReadUInt64();
            if (peerId == 0 || generation == 0) throw new FormatException("Invalid binding identity.");
        }
        internal static bool ValidFingerprint(string hash)
        { if (hash == null || hash.Length != 64) return false; foreach (char c in hash) if (Hex(c) < 0) return false; return true; }
        private static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
        private static void Header(BinaryWriter writer, byte kind) { writer.Write((ushort)0x4244); writer.Write((byte)1); writer.Write(kind); }
        private static BinaryReader Open(byte[] packet, byte kind, int length)
        {
            if (packet == null || packet.Length != length || packet[0] != 0x44 || packet[1] != 0x42 || packet[2] != 1 || packet[3] != kind)
                throw new FormatException("Invalid companion binding frame.");
            var stream = new MemoryStream(packet, false); stream.Position = 4; return new BinaryReader(stream);
        }
    }
}
