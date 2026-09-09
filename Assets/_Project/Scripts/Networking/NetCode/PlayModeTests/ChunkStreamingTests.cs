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

        [Test]
        public void ClientAnchorRequestUsesServerOwnedDistancesAndRejectsAnotherWorld()
        {
            using var serverWorld = new World("Moving interest source");
            using var clientWorld = new World("Moving interest requester");
            var registry = BlockRegistry.CreateDummy();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); replica.EnableReplicas();
            var serverPackets = new List<byte[]>(); var clientPackets = new List<byte[]>();
            var options = new ChunkStreamingOptions(1, 0);
            using var server = new ChunkStreamingServer(store, options, (_, packet) => { serverPackets.Add(packet); return true; }, _ => Assert.Fail());
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { clientPackets.Add(packet); return true; }, () => Assert.Fail(), 0);
            Assert.That(server.Add(1, 0), Is.True);
            var requested = new ChunkAddress(1, new int3(-3, 2, 5));
            Assert.That(client.RequestInterest(requested), Is.True);
            client.Tick(0);
            Assert.That(clientPackets.Count, Is.EqualTo(1));
            Assert.That(ChunkTransferFrames.ReadKind(clientPackets[0]), Is.EqualTo(ChunkFrameKind.InterestRequest));
            server.Receive(1, clientPackets[0], 0);
            Assert.That(store.Count, Is.EqualTo(9));
            server.Tick(0);
            var declaration = ChunkTransferFrames.DecodeInterest(serverPackets.Find(packet => ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Interest));
            Assert.That(declaration.Anchor, Is.EqualTo(requested));
            Assert.That(declaration.HorizontalRadius, Is.EqualTo(1));
            Assert.That(declaration.VerticalRadius, Is.Zero);
            Assert.That(client.RequestInterest(new ChunkAddress(2, default)), Is.False);
            Assert.Throws<ArgumentException>(() => server.Receive(1,
                ChunkTransferFrames.EncodeInterestRequest(new ChunkAddress(2, default)), 0));
        }

        [UnityTest]
        public IEnumerator MovingOneChunkRetainsSixReplicasAndStreamsOnlyThreeNewSnapshots()
        {
            using var serverWorld = new World("Moving source");
            using var clientWorld = new World("Moving replica");
            var registry = BlockRegistry.CreateDummy();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); replica.EnableReplicas();
            var inbound = new Queue<byte[]>(); var replies = new Queue<byte[]>(); var starts = new List<ChunkAddress>();
            var options = new ChunkStreamingOptions(1, 0);
            using var server = new ChunkStreamingServer(store, options, (_, packet) =>
            {
                if (ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Start) starts.Add(ChunkTransferFrames.DecodeStart(packet).Address);
                inbound.Enqueue(packet); return true;
            }, _ => Assert.Fail());
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { replies.Enqueue(packet); return true; }, () => Assert.Fail(), 0);
            Assert.That(server.Add(1, 0), Is.True);
            for (int tick = 0; tick < 500 && server.AppliedAcknowledgements < 9; tick++)
            {
                server.Tick(tick * 0.01);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), tick * 0.01);
                client.Tick(tick * 0.01);
                while (replies.Count != 0) server.Receive(1, replies.Dequeue(), tick * 0.01);
                yield return null;
            }
            Assert.That(server.AppliedAcknowledgements, Is.EqualTo(9));
            AssertRadial(starts, Address, options.PeerWindow);
            var retainedAddress = new ChunkAddress(1, new int3(0, 0, 0));
            Assert.That(replica.TryGetReplicaStamp(retainedAddress, out var retained), Is.True);

            starts.Clear();
            Assert.That(client.RequestInterest(new ChunkAddress(1, new int3(1, 0, 0))), Is.True);
            client.Tick(6);
            while (replies.Count != 0) server.Receive(1, replies.Dequeue(), 6);
            for (int tick = 0; tick < 500 && server.AppliedAcknowledgements < 12; tick++)
            {
                double now = 6 + tick * 0.01;
                server.Tick(now);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), now);
                client.Tick(now);
                while (replies.Count != 0) server.Receive(1, replies.Dequeue(), now);
                yield return null;
            }

            Assert.That(server.AppliedAcknowledgements, Is.EqualTo(12));
            Assert.That(server.SentSnapshots, Is.EqualTo(12));
            Assert.That(store.Count, Is.EqualTo(9)); Assert.That(replica.Count, Is.EqualTo(9));
            Assert.That(replica.DataReady, Is.True);
            Assert.That(replica.TryGetReplicaStamp(retainedAddress, out var after), Is.True);
            Assert.That(after.Incarnation, Is.EqualTo(retained.Incarnation));
            Assert.That(replica.TryGetReplicaStamp(new ChunkAddress(1, new int3(-1, 0, 0)), out _), Is.False);
            Assert.That(starts.Count, Is.EqualTo(3));
            AssertRadial(starts, new ChunkAddress(1, new int3(1, 0, 0)), options.PeerWindow);
        }

        //Selection is still strictly closest-first, but transfers are pipelined, so the window's chunks are
        //declared as their payloads finish encoding. Order is therefore radial to within the in-flight window:
        //no chunk may be declared nearer than one declared a whole window earlier.
        private static void AssertRadial(IReadOnlyList<ChunkAddress> addresses, ChunkAddress anchor, int window)
        {
            var distances = new List<long>(addresses.Count);
            foreach (var address in addresses)
            {
                long x = (long)address.Position.x - anchor.Position.x;
                long y = (long)address.Position.y - anchor.Position.y;
                long z = (long)address.Position.z - anchor.Position.z;
                distances.Add(x * x + y * y + z * z);
            }
            for (int i = 0; i < distances.Count; i++)
            {
                long settled = -1;
                for (int j = 0; j <= i - window; j++) settled = Math.Max(settled, distances[j]);
                Assert.That(distances[i], Is.GreaterThanOrEqualTo(settled),
                    $"Chunk {addresses[i]} was declared out of radial order by more than the {window}-transfer window.");
            }
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
            var options = new ChunkStreamingOptions(0, 0, 2800, 1400, 2, 1);
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
