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
            //applying is budgeted per tick now, so a payload that disagrees with its declaration is
            //rejected when the client gets round to decoding it rather than as the last slice lands.
            Deliver(client, new TransferStart(1, 1, Address, 1, 1, bad.Length, false), bad, 0);
            Assert.Throws<FormatException>(() => client.Tick(0));
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
        public void ResyncRetriesAreCountedPerChunkInsteadOfAcrossIndependentChunks()
        {
            using var world = new World("Streaming resync accounting");
            var registry = BlockRegistry.CreateDummy();
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); store.EnableReplicas();
            var interest = new ChunkInterest(1, Address, 1, 0);
            var replies = new List<byte[]>();
            bool failed = false;
            using var client = new ChunkStreamingClient(store, registry,
                new ChunkStreamingOptions(1, 0, appliesPerTick: 8),
                packet => { replies.Add(packet); return true; }, () => failed = true, 0);
            client.Receive(ChunkTransferFrames.EncodeInterest(interest), 0);
            var addresses = interest.Addresses();
            for (int i = 0; i < 3; i++)
            {
                var delta = new ChunkDelta(addresses[i], 1, 1, 2,
                    new[] { new ChunkCellUpdate(0, 1, 0) });
                byte[] payload = ChunkWireCodec.EncodeDelta(delta);
                Deliver(client, new TransferStart((ulong)i + 1, 1, addresses[i], 1, 2,
                    payload.Length, true), payload, 0);
            }

            client.Tick(0);
            Assert.That(failed, Is.False, "Independent chunks must not consume one shared retry allowance.");
            Assert.That(replies.Count, Is.EqualTo(3));
            foreach (var reply in replies)
                Assert.That(ChunkTransferFrames.ReadKind(reply), Is.EqualTo(ChunkFrameKind.Resync));

            for (ulong id = 4; id <= 5; id++)
            {
                var delta = new ChunkDelta(addresses[0], 1, 1, 2,
                    new[] { new ChunkCellUpdate(0, 1, 0) });
                byte[] payload = ChunkWireCodec.EncodeDelta(delta);
                Deliver(client, new TransferStart(id, 1, addresses[0], 1, 2,
                    payload.Length, true), payload, 1);
            }
            client.Tick(1);
            Assert.That(failed, Is.True, "Repeated failure of the same chunk must remain bounded.");
            Assert.That(replies.Count, Is.EqualTo(4), "The terminal attempt must fail instead of queuing another resync.");
        }

        [Test]
        public void ServerResyncRetriesAreCountedPerChunkInsteadOfAcrossIndependentChunks()
        {
            using var world = new World("Server resync accounting");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy());
            var packets = new List<byte[]>();
            bool failed = false;
            using var server = new ChunkStreamingServer(store,
                new ChunkStreamingOptions(1, 0, peerWindow: 8),
                (_, packet) => { packets.Add(packet); return true; }, _ => failed = true);
            Assert.That(server.Add(1, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0), Is.True);
            server.Tick(0);

            var starts = new List<TransferStart>();
            foreach (var packet in packets)
                if (ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Start)
                    starts.Add(ChunkTransferFrames.DecodeStart(packet));
            Assert.That(starts.Count, Is.GreaterThanOrEqualTo(3));
            for (int i = 0; i < 3; i++)
                server.Receive(1, ChunkTransferFrames.EncodeResync(starts[i].TransferId), 0);

            Assert.That(failed, Is.False, "Independent chunks must not consume one shared server retry allowance.");

            var retriedAddress = starts[0].Address;
            for (int attempt = 2; attempt <= 3; attempt++)
            {
                packets.Clear();
                server.Tick(attempt);
                TransferStart replacement = default;
                bool found = false;
                foreach (var packet in packets)
                {
                    if (ChunkTransferFrames.ReadKind(packet) != ChunkFrameKind.Start) continue;
                    var candidate = ChunkTransferFrames.DecodeStart(packet);
                    if (!candidate.Address.Equals(retriedAddress)) continue;
                    replacement = candidate; found = true; break;
                }
                Assert.That(found, Is.True, "The server must replace a resynced transfer.");
                server.Receive(1, ChunkTransferFrames.EncodeResync(replacement.TransferId), attempt);
                Assert.That(failed, Is.EqualTo(attempt > 2),
                    "Only the third failure of the same chunk may exhaust its server retry allowance.");
            }
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
            Assert.That(server.Add(1, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0), Is.True);
            var requested = new ChunkAddress(1, new int3(-3, 2, 5));
            Assert.That(client.RequestInterest(requested), Is.True);
            client.Tick(0);
            Assert.That(clientPackets.Count, Is.EqualTo(1));
            Assert.That(ChunkTransferFrames.ReadKind(clientPackets[0]), Is.EqualTo(ChunkFrameKind.InterestRequest));
            server.Receive(1, clientPackets[0], 0);
            Assert.That(store.Count, Is.EqualTo(ChunkInterest.CountFor(options.HorizontalRadius, options.VerticalRadius)));
            server.Tick(0);
            var declaration = ChunkTransferFrames.DecodeInterest(serverPackets.Find(packet => ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Interest));
            Assert.That(declaration.Anchor, Is.EqualTo(requested));
            Assert.That(declaration.HorizontalRadius, Is.EqualTo(1));
            Assert.That(declaration.VerticalRadius, Is.Zero);
            Assert.That(client.RequestInterest(new ChunkAddress(2, default)), Is.False);
            Assert.Throws<ArgumentException>(() => server.Receive(1,
                ChunkTransferFrames.EncodeInterestRequest(new ChunkAddress(2, default)), 0));
        }

        //view distance is negotiated rather than dictated: a client that asks for less is not forced up
        //to the server's distance, and one that asks for more is held to what the server serves. The
        //client half matters because the renderer sizes its chunk slots from the client's own setting.
        [Test]
        public void ServedViewDistanceIsTheSmallerOfTheClientAndTheServerConfiguration()
        {
            using var world = new World("Negotiated view distance");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(BlockRegistry.CreateDummy(), 2048);
            var packets = new Dictionary<ulong, List<byte[]>>();
            var options = new ChunkStreamingOptions(2, 1);
            using var server = new ChunkStreamingServer(store, options, (id, packet) =>
            {
                if (!packets.TryGetValue(id, out var list)) packets.Add(id, list = new List<byte[]>());
                list.Add(packet); return true;
            }, _ => Assert.Fail());

            Assert.That(server.Add(1, 1, 0, 0), Is.True, "A client below the server's distance.");
            Assert.That(server.Add(2, 4, 2, 0), Is.True, "A client above the server's distance.");
            server.Tick(0);

            var modest = Declaration(packets, 1);
            Assert.That(modest.HorizontalRadius, Is.EqualTo(1)); Assert.That(modest.VerticalRadius, Is.Zero);
            var greedy = Declaration(packets, 2);
            Assert.That(greedy.HorizontalRadius, Is.EqualTo(2)); Assert.That(greedy.VerticalRadius, Is.EqualTo(1));

            //an anchor move re-derives the interest, so the ceiling has to survive it rather than only
            //applying to the interest a peer is admitted with.
            server.Receive(1, ChunkTransferFrames.EncodeInterestRequest(new ChunkAddress(1, new int3(4, 0, 4))), 0);
            server.Tick(0);
            var moved = Declaration(packets, 1);
            Assert.That(moved.Anchor.Position, Is.EqualTo(new int3(4, 0, 4)));
            Assert.That(moved.HorizontalRadius, Is.EqualTo(1)); Assert.That(moved.VerticalRadius, Is.Zero);

            //server-side code may direct a peer's interest past the server's own default distance, but
            //never past what the client said it can hold: the client's renderer has exactly that many
            //chunk slots, so overshooting it would alias two live chunks onto one.
            Assert.That(server.SetInterest(1, new ChunkAddress(1, new int3(8, 0, 8)), 5, 3, 0), Is.True);
            server.Tick(0);
            var directed = Declaration(packets, 1);
            Assert.That(directed.HorizontalRadius, Is.EqualTo(1), "Held to the client's ceiling.");
            Assert.That(directed.VerticalRadius, Is.Zero);
            Assert.That(server.SetInterest(2, new ChunkAddress(1, new int3(8, 0, 8)), 4, 2, 0), Is.True);
            server.Tick(0);
            var roomier = Declaration(packets, 2);
            Assert.That(roomier.HorizontalRadius, Is.EqualTo(4), "This client can hold more than the server's default.");
            Assert.That(roomier.VerticalRadius, Is.EqualTo(2));
        }

        private static ChunkInterest Declaration(Dictionary<ulong, List<byte[]>> packets, ulong peer) =>
            ChunkTransferFrames.DecodeInterest(packets[peer].FindLast(
                packet => ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Interest));

        [UnityTest]
        public IEnumerator MovingOneChunkRetainsOverlappingReplicasAndStreamsOnlyTheNewOnes()
        {
            using var serverWorld = new World("Moving source");
            using var clientWorld = new World("Moving replica");
            var registry = BlockRegistry.CreateDummy();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry); replica.EnableReplicas();
            var inbound = new Queue<byte[]>(); var replies = new Queue<byte[]>(); var starts = new List<ChunkAddress>();
            var options = new ChunkStreamingOptions(1, 0);
            //derived from the interest shape rather than pinned, so the counts stay true if it changes.
            var movedAnchor = new ChunkAddress(1, new int3(1, 0, 0));
            var before = new ChunkInterest(1, Address, options.HorizontalRadius, options.VerticalRadius);
            var after1 = new ChunkInterest(2, movedAnchor, options.HorizontalRadius, options.VerticalRadius);
            int resident = before.Count, arrived = 0;
            foreach (var address in after1.Addresses()) if (!before.Contains(address)) arrived++;
            Assert.That(arrived, Is.GreaterThan(0).And.LessThan(resident), "the move must both retain and add chunks.");
            using var server = new ChunkStreamingServer(store, options, (_, packet) =>
            {
                if (ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Start) starts.Add(ChunkTransferFrames.DecodeStart(packet).Address);
                inbound.Enqueue(packet); return true;
            }, _ => Assert.Fail());
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { replies.Enqueue(packet); return true; }, () => Assert.Fail(), 0);
            Assert.That(server.Add(1, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0), Is.True);
            for (int tick = 0; tick < 500 && server.AppliedAcknowledgements < resident; tick++)
            {
                server.Tick(tick * 0.01);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), tick * 0.01);
                client.Tick(tick * 0.01);
                while (replies.Count != 0) server.Receive(1, replies.Dequeue(), tick * 0.01);
                yield return null;
            }
            Assert.That(server.AppliedAcknowledgements, Is.EqualTo(resident));
            AssertRadial(starts, Address, options.PeerWindow);
            var retainedAddress = new ChunkAddress(1, new int3(0, 0, 0));
            Assert.That(replica.TryGetReplicaStamp(retainedAddress, out var retained), Is.True);

            starts.Clear();
            Assert.That(client.RequestInterest(movedAnchor), Is.True);
            client.Tick(6);
            while (replies.Count != 0) server.Receive(1, replies.Dequeue(), 6);
            for (int tick = 0; tick < 500 && server.AppliedAcknowledgements < resident + arrived; tick++)
            {
                double now = 6 + tick * 0.01;
                server.Tick(now);
                while (inbound.Count != 0) client.Receive(inbound.Dequeue(), now);
                client.Tick(now);
                while (replies.Count != 0) server.Receive(1, replies.Dequeue(), now);
                yield return null;
            }

            Assert.That(server.AppliedAcknowledgements, Is.EqualTo(resident + arrived));
            Assert.That(server.SentSnapshots, Is.EqualTo(resident + arrived));
            Assert.That(store.Count, Is.EqualTo(resident)); Assert.That(replica.Count, Is.EqualTo(resident));
            Assert.That(replica.DataReady, Is.True);
            Assert.That(replica.TryGetReplicaStamp(retainedAddress, out var after), Is.True);
            Assert.That(after.Incarnation, Is.EqualTo(retained.Incarnation));
            Assert.That(replica.TryGetReplicaStamp(new ChunkAddress(1, new int3(-1, 0, 0)), out _), Is.False);
            Assert.That(starts.Count, Is.EqualTo(arrived));
            AssertRadial(starts, movedAnchor, options.PeerWindow);
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
            server.Add(1, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0);
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
            Assert.That(server.Add(1, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0), Is.True); Assert.That(server.Add(2, ChunkInterest.MaximumRadius, ChunkInterest.MaximumRadius, 0), Is.True);
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
