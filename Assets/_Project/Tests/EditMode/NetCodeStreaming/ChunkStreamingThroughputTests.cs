using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;

namespace DigBlocks.Networking.NetCode.Tests
{
    /// <summary>
    /// Guards the transfer stage against regressing to one chunk in flight at a time. The pipeline is paced
    /// to real time so snapshot-encode latency counts honestly instead of being outrun by a tight loop.
    /// </summary>
    public sealed class ChunkStreamingThroughputTests
    {
        private const double TickSeconds = 1.0 / 60.0;

        //layered terrain, so palette-packed snapshots are representative rather than degenerate.
        private sealed class LayeredSource : IAuthoritativeChunkSource
        {
            public CellEdit[] LoadOrGenerate(ChunkAddress address)
            {
                if (address.Position.y != 0) return Array.Empty<CellEdit>();
                var edits = new List<CellEdit>(ChunkLayout.Volume);
                for (int y = 0; y < ChunkLayout.Edge; y++)
                for (int z = 0; z < ChunkLayout.Edge; z++)
                for (int x = 0; x < ChunkLayout.Edge; x++)
                {
                    int height = 12 + ((x * 7 + z * 13 + address.Position.x * 3 + address.Position.z * 5) & 7);
                    uint solid = y == 0 ? 1u : y < height - 4 ? 2u : y < height - 1 ? 3u : y < height ? 4u : 0u;
                    if (solid != 0) edits.Add(new CellEdit(ChunkLayout.Index(new int3(x, y, z)), solid, 0));
                }
                return edits.ToArray();
            }
        }

        private static BlockRegistry Registry()
        {
            StateDefinition Define(string key, BlockAttributes attributes) =>
                new StateDefinition(key, "digblocks:static", "digblocks:cube", Array.Empty<string>(), attributes);
            return new BlockRegistry(
                new[]
                {
                    Define("digblocks:air", BlockAttributes.Air), Define("digblocks:bedrock", BlockAttributes.Default),
                    Define("digblocks:stone", BlockAttributes.Default), Define("digblocks:dirt", BlockAttributes.Default),
                    Define("digblocks:grass", BlockAttributes.Default), Define("digblocks:sand", BlockAttributes.Default)
                },
                new[] { Define("digblocks:empty", BlockAttributes.Air), Define("digblocks:water", BlockAttributes.Air.WithFlags(BlockFlags.Replaceable)) });
        }

        [Test]
        public void DefaultNeighbourhoodConvergesWithinTickBudget() =>
            Measure("h=2 v=1", new ChunkStreamingOptions(2, 1), 200);

        [Test]
        public void LargeNeighbourhoodConvergesWithinTickBudget() =>
            Measure("h=3 v=2", new ChunkStreamingOptions(3, 2), 500);

        //one chunk per transfer with no overlap is what the old stop-and-wait scheduler did; the budget
        //below is far tighter than that behaviour could ever reach.
        private static void Measure(string label, ChunkStreamingOptions options, int tickBudget)
        {
            using var serverWorld = new World("Throughput server");
            using var clientWorld = new World("Throughput client");
            var registry = Registry();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512);
            replica.EnableReplicas();

            var toClient = new Queue<byte[]>();
            var toServer = new Queue<byte[]>();
            using var server = new ChunkStreamingServer(store, options, (_, packet) => { toClient.Enqueue(packet); return true; },
                _ => Assert.Fail($"{label}: server reported a streaming failure"), new LayeredSource());
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { toServer.Enqueue(packet); return true; },
                () => Assert.Fail($"{label}: client reported a streaming failure"), 0);

            int expected = new ChunkInterest(1, new ChunkAddress(options.WorldId, default), options.HorizontalRadius, options.VerticalRadius).Count;
            Assert.That(server.Add(1, 0), Is.True);

            long wireBytes = 0;
            var declared = new List<ChunkAddress>();
            int starts = 0, tick = 0;
            var clock = Stopwatch.StartNew();
            for (; tick < tickBudget && replica.Count < expected; tick++)
            {
                while (clock.Elapsed.TotalSeconds < tick * TickSeconds) Thread.Sleep(1);
                double now = clock.Elapsed.TotalSeconds;
                while (toServer.Count > 0) server.Receive(1, toServer.Dequeue(), now);
                long before = server.SentBytes;
                server.Tick(now);
                wireBytes += server.SentBytes - before;
                while (toClient.Count > 0)
                {
                    var packet = toClient.Dequeue();
                    if (ChunkTransferFrames.ReadKind(packet) == ChunkFrameKind.Start)
                    { starts++; declared.Add(ChunkTransferFrames.DecodeStart(packet).Address); }
                    client.Receive(packet, now);
                }
                client.Tick(now);
            }

            Debug.Log($"[streaming] {label}: {expected} chunks in {tick} ticks ({tick * TickSeconds:F2} s at 60 Hz), " +
                $"{starts} transfers, {wireBytes} wire bytes, budget {tickBudget}.");
            Assert.That(replica.Count, Is.EqualTo(expected),
                $"{label}: only {replica.Count} of {expected} chunks converged within {tickBudget} ticks.");
            //a retried transfer re-declares under a fresh identity, so extra starts mean the pipeline stalled.
            Assert.That(starts, Is.EqualTo(expected), $"{label}: {starts - expected} transfers had to be retried.");
            AssertRadialWithinWindow(declared, new ChunkAddress(options.WorldId, default), options.PeerWindow, label);

            server.Dispose();
            client.Dispose();
        }

        //Chunks are still chosen closest-first; pipelining only lets the in-flight window finish out of order.
        //Nothing may be declared nearer than something declared a whole window earlier.
        private static void AssertRadialWithinWindow(List<ChunkAddress> declared, ChunkAddress anchor, int window, string label)
        {
            var distances = new List<long>(declared.Count);
            foreach (var address in declared)
            {
                long x = (long)address.Position.x - anchor.Position.x;
                long y = (long)address.Position.y - anchor.Position.y;
                long z = (long)address.Position.z - anchor.Position.z;
                distances.Add(x * x + y * y + z * z);
            }
            long settled = -1;
            for (int i = 0; i < distances.Count; i++)
            {
                if (i >= window) settled = Math.Max(settled, distances[i - window]);
                Assert.That(distances[i], Is.GreaterThanOrEqualTo(settled),
                    $"{label}: {declared[i]} was declared out of radial order by more than the {window}-transfer window.");
            }
        }
    }
}
