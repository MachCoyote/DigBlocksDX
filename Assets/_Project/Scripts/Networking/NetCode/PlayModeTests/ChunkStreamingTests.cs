using System;
using System.Collections;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class ChunkStreamingTests
    {
        private static readonly ChunkAddress Address = new(1, default);
        private static ChunkImage Image(ChunkAddress address, ulong revision = 1) =>
            new(address, 1, revision, new uint[ChunkLayout.Volume], new uint[ChunkLayout.Volume]);

        private static void Deliver(ChunkStreamingClient client, TransferStart start, byte[] payload, double now)
        {
            client.Receive(ChunkTransferFrames.EncodeStart(start), now);
            for (int offset = 0; offset < payload.Length; offset += ChunkTransferFrames.MaxSliceBytes)
            {
                int length = Math.Min(ChunkTransferFrames.MaxSliceBytes, payload.Length - offset);
                client.Receive(ChunkTransferFrames.EncodeSlice(start.TransferId, offset, payload, offset, length), now);
            }
        }

        [Test]
        public void ClientPublishesBeforeAckAndOldEpochCannotRecreateEvictedReplica()
        {
            using var world = new World("Streaming client epochs");
            var registry = BlockRegistry.CreateDummy();
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); store.EnableReplicas();
            int acknowledgements = 0;
            using var client = new ChunkStreamingClient(store, registry, new ChunkStreamingOptions(), packet =>
            {
                Assert.That(store.DataReady, Is.True, "An applied ACK must follow publication.");
                Assert.That(ChunkTransferFrames.ReadKind(packet), Is.EqualTo(ChunkFrameKind.Acknowledgement));
                acknowledgements++; return true;
            }, () => Assert.Fail("Unexpected failure"), 0);
            client.Receive(ChunkTransferFrames.EncodeInterest(new ChunkInterest(1, Address, 0, 0)), 0);
            byte[] payload = ChunkWireCodec.EncodeSnapshot(Image(Address));
            var start = new TransferStart(1, 1, Address, 1, 1, payload.Length, false);
            Deliver(client, start, payload, 0); Assert.That(acknowledgements, Is.Zero);
            client.Tick(0); Assert.That(acknowledgements, Is.EqualTo(1));
            client.Receive(ChunkTransferFrames.EncodeInterest(new ChunkInterest(2, new ChunkAddress(1, new int3(5)), 0, 0)), 1);
            Deliver(client, start, payload, 1);
            Assert.That(store.Count, Is.Zero); Assert.That(store.DataReady, Is.False);
            client.Receive(ChunkTransferFrames.EncodeInterest(new ChunkInterest(3, Address, 0, 0)), 1);
            Deliver(client, new TransferStart(2, 1, Address, 1, 1, payload.Length, false), payload, 1);
            Assert.That(store.Count, Is.Zero);
            Deliver(client, new TransferStart(3, 3, Address, 1, 1, payload.Length, false), payload, 1);
            client.Tick(1); Assert.That(acknowledgements, Is.EqualTo(2));
            client.Dispose(); Assert.That(store.Count, Is.Zero); Assert.That(store.DataReady, Is.False);
        }

        [Test]
        public void ClientRejectsPayloadDeclarationMismatchAndRequestsSnapshotForMissingDeltaBaseline()
        {
            using var world = new World("Streaming validation");
            var registry = BlockRegistry.CreateDummy();
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); store.EnableReplicas();
            var replies = new List<byte[]>();
            using var client = new ChunkStreamingClient(store, registry, new ChunkStreamingOptions(), packet => { replies.Add(packet); return true; }, () => Assert.Fail(), 0);
            client.Receive(ChunkTransferFrames.EncodeInterest(new ChunkInterest(1, Address, 0, 0)), 0);
            byte[] bad = ChunkWireCodec.EncodeSnapshot(Image(new ChunkAddress(2, default)));
            Assert.Throws<FormatException>(() => Deliver(client, new TransferStart(1, 1, Address, 1, 1, bad.Length, false), bad, 0));
            Assert.That(store.Count, Is.Zero); Assert.That(replies, Is.Empty);
            byte[] delta = ChunkWireCodec.EncodeDelta(new ChunkDelta(Address, 1, 1, 2, new[] { new ChunkCellUpdate(0, 1, 0) }));
            Deliver(client, new TransferStart(2, 1, Address, 1, 2, delta.Length, true), delta, 0);
            client.Tick(0);
            Assert.That(replies.Count, Is.EqualTo(1)); Assert.That(ChunkTransferFrames.DecodeResync(replies[0]), Is.EqualTo(2));
            Assert.That(store.Count, Is.Zero);
            byte[] snapshot = ChunkWireCodec.EncodeSnapshot(Image(Address, 2));
            Deliver(client, new TransferStart(3, 1, Address, 1, 2, snapshot.Length, false), snapshot, 1);
            client.Tick(1); Assert.That(ChunkTransferFrames.ReadKind(replies[1]), Is.EqualTo(ChunkFrameKind.Acknowledgement));
            Assert.That(store.DataReady, Is.True);
        }

        [UnityTest]
        public IEnumerator LostAppliedAckRetriesSnapshotAndLateAckCannotAdvanceTheReplacement()
        {
            using var world = new World("ACK recovery source");
            using var replicaWorld = new World("ACK recovery replica");
            var registry = BlockRegistry.CreateDummy();
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry);
            var replica = replicaWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); replica.EnableReplicas();
            var inbound = new Queue<byte[]>(); var replies = new Queue<byte[]>();
            var options = new ChunkStreamingOptions(0, 0, progressTimeout: 1);
            using var server = new ChunkStreamingServer(store, options, (_, packet) => { inbound.Enqueue(packet); return true; }, _ => Assert.Fail("Unexpected server failure"));
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { replies.Enqueue(packet); return true; }, () => Assert.Fail("Unexpected client failure"), 0);
            server.Add(1, 0);
            for (int tick = 0; tick < 100 && replies.Count == 0; tick++)
            {
                server.Tick(0);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), 0);
                client.Tick(0); yield return null;
            }
            Assert.That(replies.Count, Is.EqualTo(1));
            var lostAck = replies.Dequeue();
            Assert.That(replica.DataReady, Is.True); Assert.That(server.AppliedAcknowledgements, Is.Zero);
            using var lease = store.Acquire(Address);
            lease.Apply(new[] { new CellEdit(0, 1, 0) });
            server.Tick(2);
            server.Receive(1, lostAck, 2);
            Assert.That(server.AppliedAcknowledgements, Is.Zero, "Timed-out ACK cannot establish the replacement baseline.");
            for (int tick = 0; tick < 100 && server.AppliedAcknowledgements == 0; tick++)
            {
                server.Tick(2);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), 2);
                client.Tick(2);
                while (replies.Count != 0) server.Receive(1, replies.Dequeue(), 2);
                yield return null;
            }
            Assert.That(server.SentSnapshots, Is.EqualTo(2)); Assert.That(server.SentDeltas, Is.Zero);
            Assert.That(server.AppliedAcknowledgements, Is.EqualTo(1));
            Assert.That(replica.TryReadReplica(Address, out var image), Is.True);
            Assert.That(image.Revision, Is.EqualTo(lease.Revision)); Assert.That(image.SolidAt(0), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BlockedPeerDoesNotPreventOtherPeersAndTimeoutsReleaseItsResidency()
        {
            using var world = new World("Streaming fairness");
            using var clientWorld = new World("Healthy replica");
            var registry = BlockRegistry.CreateDummy();
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); replica.EnableReplicas();
            var inbound = new Queue<byte[]>(); var replies = new Queue<byte[]>(); var failed = new List<ulong>();
            var options = new ChunkStreamingOptions(0, 0, 2048, 1024, 2, 1);
            using var server = new ChunkStreamingServer(store, options, (peer, packet) =>
            { if (peer == 1) return false; inbound.Enqueue(packet); return true; }, peer => failed.Add(peer));
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { replies.Enqueue(packet); return true; }, () => Assert.Fail("Healthy client failed"), 0);
            Assert.That(server.Add(1, 0), Is.True); Assert.That(server.Add(2, 0), Is.True);
            Assert.That(server.SetInterest(1, new ChunkAddress(2, default), 0, 0, 0), Is.True);
            for (int tick = 0; tick < 100 && server.AppliedAcknowledgements == 0; tick++)
            {
                double now = tick * 0.005;
                server.Tick(now);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), now);
                client.Tick(now); while (replies.Count != 0) server.Receive(2, replies.Dequeue(), now);
                Assert.That(server.PayloadCount, Is.LessThanOrEqualTo(2)); yield return null;
            }
            Assert.That(replica.DataReady, Is.True); Assert.That(server.AppliedAcknowledgements, Is.EqualTo(1));
            Assert.That(store.Count, Is.EqualTo(2));
            server.Tick(2); server.Tick(4); server.Tick(6);
            Assert.That(failed.Contains(1ul), Is.True); Assert.That(failed.Contains(2ul), Is.False);
            server.Remove(1); Assert.That(store.Count, Is.EqualTo(1));
        }
    }
}
