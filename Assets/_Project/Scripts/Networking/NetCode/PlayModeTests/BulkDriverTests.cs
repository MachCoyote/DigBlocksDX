using System;
using System.Collections;
using NUnit.Framework;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class BulkDriverTests
    {
        [Test]
        public void TicketIsBoundExpiresAndCanOnlyBeConsumedOnce()
        {
            var tickets = new BulkTickets(2);
            var ticket = tickets.Issue(4, 9, 1, 5);
            Assert.That(ticket.Equals(default), Is.False);
            Assert.That(tickets.TryConsume(ticket, 5, 2, out _), Is.False);
            Assert.That(tickets.TryConsume(ticket, 4, 2, out ulong generation), Is.True);
            Assert.That(generation, Is.EqualTo(9));
            Assert.That(tickets.TryConsume(ticket, 4, 2, out _), Is.False);
            var expired = tickets.Issue(4, 10, 1, 5);
            Assert.That(tickets.TryConsume(expired, 4, 6, out _), Is.False);
            var revoked = tickets.Issue(4, 11, 6);
            tickets.Revoke(4);
            Assert.That(tickets.TryConsume(revoked, 4, 7, out _), Is.False);
        }

        [Test]
        public void TicketCapacityIsBoundedAndExpiredSlotsAreReclaimed()
        {
            var tickets = new BulkTickets(1);
            tickets.Issue(1, 1, 0, 1);
            Assert.Throws<InvalidOperationException>(() => tickets.Issue(2, 2, 0, 1));
            var next = tickets.Issue(2, 2, 1, 1);
            Assert.That(tickets.TryConsume(next, 2, 1.5, out _), Is.True);
        }

        [Test]
        public void TicketsRejectInvalidInputsAndReplacingTicketInvalidatesOldSecret()
        {
            var tickets = new BulkTickets(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => tickets.Issue(0, 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => tickets.Issue(1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => tickets.Issue(1, 1, double.NaN));
            var old = tickets.Issue(1, 3, 0);
            var fresh = tickets.Issue(1, 4, 1);
            Assert.That(tickets.TryConsume(old, 1, 1, out _), Is.False);
            Assert.That(tickets.TryConsume(new BulkTicket(1, 2), 1, 1, out _), Is.False);
            Assert.That(tickets.TryConsume(fresh, 1, 1, out ulong generation), Is.True);
            Assert.That(generation, Is.EqualTo(4));
            var cleared = tickets.Issue(1, 5, 2);
            tickets.Clear();
            Assert.That(tickets.TryConsume(cleared, 1, 2, out _), Is.False);
        }

        [UnityTest] public IEnumerator IpcReliableQueueRoundTripsOwnedPayloads() => RoundTrip(true);
        [UnityTest] public IEnumerator UdpReliableQueueRoundTripsOwnedPayloads() => RoundTrip(false);

        [UnityTest]
        public IEnumerator UnreliablePayloadIsRejectedBeforeDelivery()
        {
            using var server = new BulkDriver(true, true, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var raw = NetworkDriver.Create(new IPCNetworkInterface());
            try
            {
                Assert.That(raw.Bind(NetworkEndpoint.LoopbackIpv4.WithPort(0)), Is.Zero);
                var connection = raw.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(server.LocalPort));
                bool ready = false;
                double deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!ready && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    raw.ScheduleUpdate().Complete(); server.Update();
                    while (server.TryPopEvent(out _)) { }
                    NetworkEvent.Type type;
                    while ((type = raw.PopEvent(out _, out _)) != NetworkEvent.Type.Empty)
                        ready |= type == NetworkEvent.Type.Connect;
                    yield return null;
                }
                Assert.That(ready, Is.True);
                Assert.That(raw.BeginSend(connection, out var writer), Is.Zero);
                writer.WriteByte(42);
                Assert.That(raw.EndSend(writer), Is.EqualTo(1));
                bool rejected = false;
                deadline = Time.realtimeSinceStartupAsDouble + 5;
                while (!rejected && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    raw.ScheduleUpdate().Complete(); server.Update();
                    while (raw.PopEvent(out _, out _) != NetworkEvent.Type.Empty) { }
                    while (server.TryPopEvent(out var item))
                    {
                        Assert.That(item.Type, Is.Not.EqualTo(BulkEventType.Data));
                        rejected |= item.Type == BulkEventType.Disconnected;
                    }
                    yield return null;
                }
                Assert.That(rejected, Is.True);
            }
            finally { raw.Dispose(); }
        }

        private static IEnumerator RoundTrip(bool ipc)
        {
            using var server = new BulkDriver(ipc, true, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            using var client = new BulkDriver(ipc, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            Assert.That(server.LocalPort, Is.Not.Zero);
            var connection = client.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(server.LocalPort));
            bool connected = false;
            double deadline = Time.realtimeSinceStartupAsDouble + 5;
            while (!connected && Time.realtimeSinceStartupAsDouble < deadline)
            {
                server.Update(); client.Update();
                while (server.TryPopEvent(out _)) { }
                while (client.TryPopEvent(out var item)) connected |= item.Type == BulkEventType.Connected;
                yield return null;
            }
            Assert.That(connected, Is.True);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.TrySend(connection, new byte[BulkDriver.MaxPayloadBytes + 1]));
            //push past the queue depth rather than a fixed number, so raising the depth does not turn
            //this into an assertion that the queue is unbounded.
            int attempts = BulkDriver.MaxMessagesPerConnection * 2;
            int queued = 0;
            for (; queued < attempts; queued++)
            {
                byte[] payload = { (byte)queued, (byte)(queued >> 8), 77 };
                if (!client.TrySend(connection, payload)) break;
                payload[2] = 0;
            }
            Assert.That(queued, Is.GreaterThan(0).And.LessThan(attempts), "The application send queue must be bounded.");
            int received = 0;
            deadline = Time.realtimeSinceStartupAsDouble + 10;
            while (received < queued && Time.realtimeSinceStartupAsDouble < deadline)
            {
                client.Update(); server.Update();
                while (client.TryPopEvent(out _)) { }
                while (server.TryPopEvent(out var item))
                {
                    if (item.Type != BulkEventType.Data) continue;
                    Assert.That(item.Payload, Is.EqualTo(new byte[] { (byte)received, (byte)(received >> 8), 77 }));
                    received++;
                }
                yield return null;
            }
            Assert.That(received, Is.EqualTo(queued));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DisposeReleasesListener(bool ipc)
        {
            var server = new BulkDriver(ipc, true, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            ushort port = server.LocalPort;
            Assert.That(port, Is.Not.Zero);
            server.Dispose(); server.Dispose();
            using var replacement = new BulkDriver(ipc, true, NetworkEndpoint.LoopbackIpv4.WithPort(port));
            Assert.That(replacement.LocalPort, Is.EqualTo(port));
        }
    }
}
