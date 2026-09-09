using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Server.Runtime;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class ChunkStreamingScaleTests
    {
        [UnityTest] public IEnumerator ThirtyTwoUdpPeersApplyBoundedStreaming() => Run(32, false);
        [UnityTest] public IEnumerator FourUdpPeersConvergeWithDelayJitterAndPacketLoss() => Run(4, true);

        private static IEnumerator Run(int peerCount, bool impaired) => UniTask.ToCoroutine(async () =>
        {
            var hosts = new List<GameHost>(); var logger = new NullLogger();
            var options = new ChunkStreamingOptions(progressTimeout: 30);
            if (impaired) options.Simulation = new SimulatorUtility.Parameters
            {
                MaxPacketCount = 256, MaxPacketSize = 1400, Mode = ApplyMode.SentPacketsOnly,
                PacketDelayMs = 50, PacketJitterMs = 10, PacketDropInterval = 10, RandomSeed = 7
            };
            GameHost Host(params IGameService[] services) { var host = new GameHost(services, logger); hosts.Add(host); return host; }
            async UniTask Until(Func<bool> condition)
            {
                double deadline = Time.realtimeSinceStartupAsDouble + 45;
                while (!condition() && Time.realtimeSinceStartupAsDouble < deadline) await UniTask.Yield();
                Assert.That(condition(), Is.True, "Streaming scale condition exceeded 45 seconds.");
            }
            try
            {
                var runtime = new ServerRuntime(NetCodeWorldFactory.CreateServerWorld);
                var session = new NetCodeSession(NetworkSessionRole.Server, new NetworkSessionOptions("127.0.0.1", 0, 1, capacity: peerCount, bindAddress: "127.0.0.1"), null, () => runtime.World, logger);
                var server = new ChunkCompanionService(session, 0, bindingTimeoutSeconds: 30, streamingOptions: options);
                await Host(runtime, session, server).StartAsync(CancellationToken.None);
                var clients = new ChunkCompanionService[peerCount]; var sessions = new NetCodeSession[peerCount];
                var replicas = new ResidentChunkStore[peerCount];
                for (int i = 0; i < peerCount; i++)
                {
                    var clientRuntime = new ClientRuntime(NetCodeWorldFactory.CreateClientWorld);
                    ulong identity = (ulong)(200 + i);
                    var clientSession = new NetCodeSession(NetworkSessionRole.Client, new NetworkSessionOptions("127.0.0.1", session.ListeningPort, 1), () => clientRuntime.World, null, logger, () => identity);
                    clients[i] = new ChunkCompanionService(clientSession, bindingTimeoutSeconds: 30, streamingOptions: options); sessions[i] = clientSession;
                    await Host(clientRuntime, clientSession, clients[i]).StartAsync(CancellationToken.None);
                    replicas[i] = clientSession.ClientWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
                }
                await Until(() => { foreach (var client in clients) if (!client.ClientDataReady) return false; return server.AppliedChunkAcknowledgements >= peerCount * 9; });
                var source = session.ServerWorld.GetExistingSystemManaged<ChunkWorldSystem>().Store;
                Assert.That(source.Count, Is.EqualTo(9), "Overlapping peers share authoritative residents.");
                using var lease = source.Acquire(new ChunkAddress(1, default));
                var edits = new CellEdit[ChunkLayout.Volume];
                for (int i = 0; i < edits.Length; i++) edits[i] = new CellEdit(i, (uint)(i % 3 == 0 ? 1 : 0), (uint)(i % 3 == 1 ? 1 : 0));
                lease.Apply(edits);
                long beforeBytes = server.SentChunkBytes, beforeAck = server.AppliedChunkAcknowledgements;
                double began = Time.realtimeSinceStartupAsDouble;
                var appliedAt = new double[peerCount]; int observedPayloads = 0, observedCaptures = 0;
                long managedPeak = GC.GetTotalMemory(false), unityPeak = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                for (int i = 0; i < peerCount; i++) Assert.That(server.SetServerInterest(sessions[i].LocalPeerId, lease.Address, 0, 0), Is.True);
                await Until(() =>
                {
                    observedPayloads = Math.Max(observedPayloads, server.PendingChunkPayloads);
                    observedCaptures = Math.Max(observedCaptures, source.PendingSnapshots);
                    managedPeak = Math.Max(managedPeak, GC.GetTotalMemory(false));
                    unityPeak = Math.Max(unityPeak, UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong());
                    bool complete = true;
                    for (int i = 0; i < peerCount; i++)
                    {
                        if (replicas[i].InterestEpoch == 2 && clients[i].ClientDataReady)
                        { if (appliedAt[i] == 0) appliedAt[i] = Time.realtimeSinceStartupAsDouble - began; }
                        else complete = false;
                    }
                    return complete && server.AppliedChunkAcknowledgements >= beforeAck + peerCount;
                });
                double fastest = double.MaxValue, slowest = 0;
                foreach (double elapsed in appliedAt) { fastest = Math.Min(fastest, elapsed); slowest = Math.Max(slowest, elapsed); }
                foreach (var replica in replicas)
                {
                    Assert.That(replica.TryReadReplica(lease.Address, out var image), Is.True);
                    Assert.That(image.Revision, Is.EqualTo(lease.Revision));
                    for (int i = 0; i < ChunkLayout.Volume; i++)
                    { Assert.That(image.SolidAt(i), Is.EqualTo(lease.SolidAt(i))); Assert.That(image.FluidAt(i), Is.EqualTo(lease.FluidAt(i))); }
                }
                Assert.That(observedPayloads, Is.LessThanOrEqualTo(options.MaxPayloads)); Assert.That(observedCaptures, Is.LessThanOrEqualTo(options.SnapshotWorkers));
                Assert.That(source.Count, Is.EqualTo(1));
                TestContext.WriteLine(FormattableString.Invariant($"STREAMING_METRICS peers={peerCount} impaired={impaired} bytes={server.SentChunkBytes - beforeBytes} first_apply_s={fastest:F3} last_apply_s={slowest:F3} max_ack_s={server.MaxAppliedAckSeconds:F3} peak_encoded_bytes={server.PeakEncodedPayloadBytes} payload_slots={observedPayloads} capture_slots={observedCaptures} process_managed_peak={managedPeak} unity_allocated_peak={unityPeak}"));
            }
            finally { for (int i = hosts.Count - 1; i >= 0; i--) await hosts[i].StopAsync(CancellationToken.None); }
        });
        private sealed class NullLogger : IGameLogger
        { public IGameLogger CreateFor(string sourceName) => this; public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null) { } }
    }
}
