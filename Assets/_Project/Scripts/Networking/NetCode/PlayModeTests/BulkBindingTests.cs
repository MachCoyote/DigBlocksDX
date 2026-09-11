using System;
using DigBlocks.ChunkProtocol;
using NUnit.Framework;
namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class BulkBindingTests
    {
        private static BulkBindRequest Request() => new BulkBindRequest(7, 9, new BulkTicket(13, 17), 32, 12, 4, new string('a', 64));
        [Test]
        public void BindingFramesRoundTripTicketGenerationLayoutViewDistanceAndFingerprint()
        {
            byte[] packet = BulkBindingFrames.EncodeRequest(Request());
            Assert.That(packet.Length, Is.EqualTo(74));
            var result = BulkBindingFrames.DecodeRequest(packet);
            Assert.That(result.PeerId, Is.EqualTo(7)); Assert.That(result.Generation, Is.EqualTo(9));
            Assert.That(result.Ticket, Is.EqualTo(new BulkTicket(13, 17)));
            Assert.That(result.Edge, Is.EqualTo(32)); Assert.That(result.Fingerprint, Is.EqualTo(new string('a', 64)));
            Assert.That(result.HorizontalRadius, Is.EqualTo(12)); Assert.That(result.VerticalRadius, Is.EqualTo(4));
            packet = BulkBindingFrames.EncodeAccepted(7, 9);
            BulkBindingFrames.DecodeAccepted(packet, out ulong peer, out ulong generation);
            Assert.That(peer, Is.EqualTo(7)); Assert.That(generation, Is.EqualTo(9));
        }
        [Test]
        public void BindingFramesRejectMalformedLengthsHeadersAndWrongDirections()
        {
            byte[] valid = BulkBindingFrames.EncodeRequest(Request());
            for (int length = 0; length < valid.Length; length++)
                Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(new byte[length]));
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(null));
            var extra = new byte[valid.Length + 1]; Array.Copy(valid, extra, valid.Length);
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(extra));
            for (int index = 0; index < 4; index++)
            {
                var corrupted = (byte[])valid.Clone(); corrupted[index] = 255;
                Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(corrupted));
            }
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(BulkBindingFrames.EncodeAccepted(7, 9)));
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeAccepted(valid, out _, out _));
            var zeroPeer = (byte[])valid.Clone(); Array.Clear(zeroPeer, 4, 8);
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(zeroPeer));
        }
        //a peer chooses its own view distance, so an absurd one has to be rejected rather than sized for.
        [Test]
        public void BindingFramesRejectAViewDistanceBeyondTheInterestRadiusLimit()
        {
            const ushort tooFar = (ushort)(ChunkInterest.MaximumRadius + 1);
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(
                new BulkBindRequest(7, 9, new BulkTicket(13, 17), 32, tooFar, 4, new string('a', 64))));
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(
                new BulkBindRequest(7, 9, new BulkTicket(13, 17), 32, 12, tooFar, new string('a', 64))));
            byte[] valid = BulkBindingFrames.EncodeRequest(Request());
            //the radii sit immediately after the 2-byte edge, which follows peer, generation and ticket.
            var overRadius = (byte[])valid.Clone();
            overRadius[38] = (byte)(tooFar & 255); overRadius[39] = (byte)(tooFar >> 8);
            Assert.Throws<FormatException>(() => BulkBindingFrames.DecodeRequest(overRadius));
        }
        [Test]
        public void BindingFramesRejectEmptyCredentialsAndNoncanonicalFingerprint()
        {
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(new BulkBindRequest(0, 1, new BulkTicket(1, 2), 32, 12, 4, new string('a', 64))));
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(new BulkBindRequest(1, 0, new BulkTicket(1, 2), 32, 12, 4, new string('a', 64))));
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(new BulkBindRequest(1, 1, default, 32, 12, 4, new string('a', 64))));
            foreach (string hash in new[] { null, "abc", new string('G', 64), new string('A', 64) })
                Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeRequest(new BulkBindRequest(1, 1, new BulkTicket(1, 2), 32, 12, 4, hash)));
            Assert.Throws<ArgumentException>(() => BulkBindingFrames.EncodeAccepted(1, 0));
        }
    }
}
