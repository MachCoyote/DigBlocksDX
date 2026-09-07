using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using TransportStatus = Unity.Networking.Transport.Error.StatusCode;

namespace DigBlocks.Networking.NetCode
{
    public enum BulkEventType : byte { Connected, Data, Disconnected }

    public readonly struct BulkDriverEvent
    {
        public BulkDriverEvent(BulkEventType type, NetworkConnection connection, byte[] payload = null)
        { Type = type; Connection = connection; Payload = payload; }
        public BulkEventType Type { get; }
        public NetworkConnection Connection { get; }
        public byte[] Payload { get; }
    }

    public sealed class BulkDriver : IDisposable
    {
        public const int MaxPayloadBytes = 1024;
        private const int MaxQueuedMessages = 256;
        private const int MaxMessagesPerConnection = 64;
        private const int MaxQueuedEvents = 256;
        private readonly int maxConnections;
        private readonly bool server;
        private readonly Dictionary<NetworkConnection, PendingConnection> connections = new();
        private readonly Queue<BulkDriverEvent> events = new();
        private readonly Queue<NetworkConnection> disconnected = new();
        private readonly List<NetworkConnection> closing = new();
        private readonly List<NetworkConnection> sendOrder = new();
        private NetworkDriver driver;
        private NetworkPipeline pipeline;
        private JobHandle updateJob;
        private int queuedMessages;
        private int nextSender;
        private bool disposed;

        private sealed class PendingConnection
        {
            public bool Ready;
            public int PayloadCapacity;
            public readonly Queue<byte[]> Messages = new();
        }

        public BulkDriver(bool ipc, bool server, NetworkEndpoint endpoint, int maxConnections = 64)
        {
            if (maxConnections < 1 || maxConnections > 1024) throw new ArgumentOutOfRangeException(nameof(maxConnections));
            this.maxConnections = maxConnections;
            this.server = server;
            var settings = new NetworkSettings(Allocator.Temp);
            try
            {
                settings.WithReliableStageParameters(windowSize: 64);
                settings.WithNetworkConfigParameters(sendQueueCapacity: 256, receiveQueueCapacity: 256);
                driver = ipc ? NetworkDriver.Create(new IPCNetworkInterface(), settings)
                    : NetworkDriver.Create(new UDPNetworkInterface(), settings);
            }
            finally { settings.Dispose(); }
            try
            {
                pipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
                if (driver.Bind(endpoint) != 0 || (server && driver.Listen() != 0))
                    throw new InvalidOperationException($"Could not bind bulk transport to {endpoint}.");
                LocalPort = driver.GetLocalEndpoint().Port;
            }
            catch { driver.Dispose(); throw; }
        }

        public ushort LocalPort { get; }

        public NetworkConnection Connect(NetworkEndpoint endpoint)
        {
            ThrowIfDisposed();
            if (server) throw new InvalidOperationException("A bulk server accepts connections rather than initiating them.");
            if (connections.Count + disconnected.Count >= maxConnections) throw new InvalidOperationException("Bulk connection capacity is exhausted; consume disconnect events before reconnecting.");
            updateJob.Complete();
            var connection = driver.Connect(endpoint);
            if (!connection.IsCreated) throw new InvalidOperationException("Bulk connection could not be created.");
            connections.Add(connection, new PendingConnection());
            return connection;
        }

        //acceptance means copied into our bounded queue, not delivered or applied by the remote peer.
        public bool TrySend(NetworkConnection connection, byte[] payload)
        {
            ThrowIfDisposed();
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0 || payload.Length > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payload));
            if (!connections.TryGetValue(connection, out var pending) || !pending.Ready)
                throw new InvalidOperationException("The bulk connection is not ready.");
            if (payload.Length > pending.PayloadCapacity) throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the negotiated reliable-pipeline capacity.");
            if (queuedMessages >= MaxQueuedMessages || pending.Messages.Count >= MaxMessagesPerConnection) return false;
            pending.Messages.Enqueue((byte[])payload.Clone());
            queuedMessages++;
            return true;
        }

        public void Update()
        {
            ThrowIfDisposed();
            //do not stall the frame waiting on the previous transport update.
            if (!updateJob.IsCompleted) return;
            updateJob.Complete();
            while (events.Count < MaxQueuedEvents && disconnected.Count > 0)
                events.Enqueue(new BulkDriverEvent(BulkEventType.Disconnected, disconnected.Dequeue()));
            if (server)
            {
                int accepts = 0;
                while (events.Count < MaxQueuedEvents && accepts < MaxQueuedEvents)
                {
                    var connection = driver.Accept();
                    if (!connection.IsCreated) break;
                    accepts++;
                    if (connections.Count + disconnected.Count >= maxConnections) { driver.Disconnect(connection); continue; }
                    connections.Add(connection, new PendingConnection { Ready = true, PayloadCapacity = driver.GetMaxSupportedPayloadSize(connection, pipeline) });
                    events.Enqueue(new BulkDriverEvent(BulkEventType.Connected, connection));
                }
                if (accepts == MaxQueuedEvents || events.Count == MaxQueuedEvents) return;
            }

            int reads = 0;
            while (events.Count < MaxQueuedEvents && reads < MaxQueuedEvents)
            {
                var type = driver.PopEvent(out var connection, out var reader, out var receivedPipeline);
                if (type == NetworkEvent.Type.Empty) break;
                reads++;
                if (!connections.TryGetValue(connection, out var pending)) continue;
                if (type == NetworkEvent.Type.Connect)
                {
                    pending.Ready = true;
                    pending.PayloadCapacity = driver.GetMaxSupportedPayloadSize(connection, pipeline);
                    events.Enqueue(new BulkDriverEvent(BulkEventType.Connected, connection));
                }
                else if (type == NetworkEvent.Type.Disconnect) RemoveConnection(connection, true);
                else if (type == NetworkEvent.Type.Data)
                {
                    if (receivedPipeline != pipeline || reader.Length == 0 || reader.Length > MaxPayloadBytes)
                    { driver.Disconnect(connection); RemoveConnection(connection, true); continue; }
                    var bytes = new byte[reader.Length];
                    reader.ReadBytes(bytes.AsSpan());
                    events.Enqueue(new BulkDriverEvent(BulkEventType.Data, connection, bytes));
                }
            }
            //UTP clears unread events on the next update. Drain them over bounded calls first.
            if (events.Count == MaxQueuedEvents || reads == MaxQueuedEvents) return;

            closing.Clear();
            sendOrder.Clear();
            foreach (var connection in connections.Keys) sendOrder.Add(connection);
            int sent = 0;
            int examined = 0;
            int start = sendOrder.Count == 0 ? 0 : nextSender % sendOrder.Count;
            for (; examined < sendOrder.Count && sent < MaxQueuedMessages; examined++)
            {
                var connection = sendOrder[(start + examined) % sendOrder.Count];
                var messages = connections[connection].Messages;
                int perConnection = 0;
                while (messages.Count > 0 && perConnection < 16 && sent < MaxQueuedMessages)
                {
                    byte[] bytes = messages.Peek();
                    int result = driver.BeginSend(pipeline, connection, out var writer, bytes.Length);
                    if (result == (int)TransportStatus.NetworkSendQueueFull) break;
                    if (result != 0) { closing.Add(connection); break; }
                    writer.WriteBytes(bytes.AsSpan());
                    result = driver.EndSend(writer);
                    if (result == (int)TransportStatus.NetworkSendQueueFull) break;
                    if (result < 0) { closing.Add(connection); break; }
                    messages.Dequeue(); queuedMessages--; perConnection++; sent++;
                }
            }
            //continue after the last serviced peer when the global send budget is exhausted.
            nextSender = sendOrder.Count == 0 ? 0 : (start + examined) % sendOrder.Count;
            foreach (var connection in closing)
            { driver.Disconnect(connection); RemoveConnection(connection, true); }
            updateJob = driver.ScheduleUpdate();
        }

        public bool TryPopEvent(out BulkDriverEvent item)
        {
            ThrowIfDisposed();
            if (events.Count > 0) { item = events.Dequeue(); return true; }
            if (disconnected.Count > 0) { item = new BulkDriverEvent(BulkEventType.Disconnected, disconnected.Dequeue()); return true; }
            item = default; return false;
        }

        public void Disconnect(NetworkConnection connection)
        {
            ThrowIfDisposed();
            updateJob.Complete();
            driver.Disconnect(connection);
            RemoveConnection(connection, true);
        }

        private void RemoveConnection(NetworkConnection connection, bool notify)
        {
            if (!connections.TryGetValue(connection, out var pending)) return;
            queuedMessages -= pending.Messages.Count;
            connections.Remove(connection);
            if (notify) disconnected.Enqueue(connection);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            updateJob.Complete();
            if (driver.IsCreated)
            {
                try
                {
                    foreach (var connection in connections.Keys) driver.Disconnect(connection);
                    //UTP requires one completed update to send disconnect notifications before disposal.
                    driver.ScheduleUpdate().Complete();
                }
                finally { driver.Dispose(); }
            }
            connections.Clear(); events.Clear(); disconnected.Clear(); closing.Clear(); sendOrder.Clear(); queuedMessages = 0;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(BulkDriver));
        }
    }
}
