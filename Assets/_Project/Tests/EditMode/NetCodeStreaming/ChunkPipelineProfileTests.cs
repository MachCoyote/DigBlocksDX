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
    /// Profiling harness. Not a regression guard: these tests measure and report, they do not assert
    /// throughput. Run by name; they are explicit so a normal suite run skips them.
    /// </summary>
    [Explicit("Profiling harness: run by name, not as part of the suite.")]
    public sealed class ChunkPipelineProfileTests
    {
        private const double TickSeconds = 1.0 / 60.0;

        private sealed class LayeredSource : IAuthoritativeChunkSource
        {
            public int Calls; public double Seconds;
            public void Generate(ChunkAddress address, uint[] solids, uint[] fluids)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    if (address.Position.y != 0) return;
                    for (int y = 0; y < ChunkLayout.Edge; y++)
                    for (int z = 0; z < ChunkLayout.Edge; z++)
                    for (int x = 0; x < ChunkLayout.Edge; x++)
                    {
                        int height = 12 + ((x * 7 + z * 13 + address.Position.x * 3 + address.Position.z * 5) & 7);
                        uint solid = y == 0 ? 1u : y < height - 4 ? 2u : y < height - 1 ? 3u : y < height ? 4u : 0u;
                        if (solid != 0) solids[ChunkLayout.Index(new int3(x, y, z))] = solid;
                    }
                }
                finally { System.Threading.Interlocked.Increment(ref Calls); Seconds += clock.Elapsed.TotalSeconds; }
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

        //---------------------------------------------------------------- stage costs

        [Test]
        public void StageCosts()
        {
            var registry = Registry();
            using var world = new World("Stage costs");
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512);
            var source = new LayeredSource();

            //authoritative load of one representative chunk: generate plus the per-cell Apply path.
            var address = new ChunkAddress(1, new int3(0, 0, 0));
            var lease = store.Acquire(address);
            var genClock = Stopwatch.StartNew();
            store.EnsureLoaded(lease, source);
            genClock.Stop();

            const int iterations = 200;
            byte[] payload = store.EncodeSnapshot(lease);
            double encode = Time(iterations, () => store.EncodeSnapshot(lease));

            var solids = new PackedChannelData();
            var fluids = new PackedChannelData();
            double decode = Time(iterations, () => ChunkWireCodec.DecodeSnapshotInto(payload, registry.MaxSolidStateId,
                registry.MaxFluidStateId, solids, fluids, out _, out _, out _));

            double fromPacked = Time(iterations, () =>
            { var data = ChunkData.FromPacked(address, 1, 1, solids, fluids); data.Dispose(); });

            using var replicaWorld = new World("Stage costs replica");
            var replica = replicaWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512);
            replica.EnableReplicas();
            replica.SetReplicaInterest(new ChunkInterest(1, new ChunkAddress(1, default), 3, 2));
            ulong revision = 1;
            double publish = Time(iterations, () => replica.PublishReplica(1, address, 1, ++revision, solids, fluids));

            Debug.Log($"[profile:stage] payload={payload.Length}B solidBits={solids.BitsPerEntry} " +
                $"solidPalette={solids.PaletteCount} fluidStorage={fluids.Storage}");
            Debug.Log($"[profile:stage] worldgen_generate_and_apply={genClock.Elapsed.TotalMilliseconds:F2}ms server_main_thread");
            Debug.Log($"[profile:stage] EncodeSnapshot={encode:F3}ms server_main_thread");
            Debug.Log($"[profile:stage] DecodeSnapshotInto={decode:F3}ms client_main_thread");
            Debug.Log($"[profile:stage] PublishReplica={publish:F3}ms client_main_thread");
            Debug.Log($"[profile:stage] FromPacked={fromPacked:F3}ms");
            Debug.Log($"[profile:stage] client_per_chunk_total={decode + publish:F3}ms");

            lease.Dispose();
        }

        private static double Time(int iterations, Action action)
        {
            action();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            return clock.Elapsed.TotalMilliseconds / iterations;
        }

        //---------------------------------------------------------------- pipeline accounting

        //the authored shape and budgets, so this measures what actually ships.
        [Test]
        public void PipelineTickAccounting() => Debug.Log(Run("authored h=12 v=2",
            new ChunkStreamingOptions(12, 2, globalBytesPerTick: 2097152, peerBytesPerTick: 1048576,
                maxPayloads: 128, peerWindow: 64, appliesPerTick: 16), 1800, trace: true));

        [Test]
        public void ParameterSweep()
        {
            var lines = new List<string>();
            foreach (int window in new[] { 16, 64 })
            foreach (int applies in new[] { 2, 8, 16, 32 })
                lines.Add(Run($"window {window,2} applies {applies,2}",
                    new ChunkStreamingOptions(12, 2, globalBytesPerTick: 2097152, peerBytesPerTick: 1048576,
                        maxPayloads: 256, peerWindow: window, appliesPerTick: applies), 1800, trace: false));
            Debug.Log("[profile:sweep]\n" + string.Join("\n", lines));
        }

        [Test]
        public void UnboundedBudgets()
        {
            var options = new ChunkStreamingOptions(12, 2, globalBytesPerTick: 8388608, peerBytesPerTick: 8388608,
                maxPayloads: 256, peerWindow: 1024, appliesPerTick: 64);
            Debug.Log(Run("unbounded", options, 900, trace: true));
        }

        private static string Run(string label, ChunkStreamingOptions options, int tickBudget, bool trace)
        {
            using var serverWorld = new World($"Profile server {label}");
            using var clientWorld = new World($"Profile client {label}");
            var registry = Registry();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 8192);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 8192);
            replica.EnableReplicas();

            var toClient = new Queue<byte[]>();
            var toServer = new Queue<byte[]>();
            var source = new LayeredSource();
            using var server = new ChunkStreamingServer(store, options, (_, packet) => { toClient.Enqueue(packet); return true; },
                _ => Assert.Fail($"{label}: server failure"), source);
            using var client = new ChunkStreamingClient(replica, registry, options, packet => { toServer.Enqueue(packet); return true; },
                () => Assert.Fail($"{label}: client failure"), 0);

            int expected = new ChunkInterest(1, new ChunkAddress(options.WorldId, default), options.HorizontalRadius, options.VerticalRadius).Count;
            Assert.That(server.Add(1, 0), Is.True);

            double serverSeconds = 0, clientSeconds = 0, receiveSeconds = 0;
            double worstServer = 0, worstClient = 0;
            int idleTicks = 0, windowFullTicks = 0;
            int tick = 0, previousCount = 0;
            var samples = new List<string>();
            var clock = Stopwatch.StartNew();
            var stage = new Stopwatch();

            for (; tick < tickBudget && replica.Count < expected; tick++)
            {
                while (clock.Elapsed.TotalSeconds < tick * TickSeconds) Thread.Sleep(1);
                double now = clock.Elapsed.TotalSeconds;
                while (toServer.Count > 0) server.Receive(1, toServer.Dequeue(), now);

                stage.Restart(); server.Tick(now); stage.Stop();
                serverSeconds += stage.Elapsed.TotalSeconds; worstServer = Math.Max(worstServer, stage.Elapsed.TotalMilliseconds);
                double serverMs = stage.Elapsed.TotalMilliseconds;

                int inFlight = server.PayloadCount;
                if (inFlight >= options.PeerWindow) windowFullTicks++;

                stage.Restart();
                while (toClient.Count > 0) client.Receive(toClient.Dequeue(), now);
                stage.Stop(); receiveSeconds += stage.Elapsed.TotalSeconds;

                stage.Restart(); client.Tick(now); stage.Stop();
                clientSeconds += stage.Elapsed.TotalSeconds; worstClient = Math.Max(worstClient, stage.Elapsed.TotalMilliseconds);

                int applied = replica.Count - previousCount;
                previousCount = replica.Count;
                if (applied == 0) idleTicks++;
                if (trace && (tick < 16 || tick % 10 == 0))
                    samples.Add($"    t{tick,3} applied {applied,2} total {replica.Count,4} inflight {inFlight,3} " +
                        $"srv {serverMs,6:F2} cli {stage.Elapsed.TotalMilliseconds,6:F2}");
            }

            string header = $"  {label,-24} {replica.Count,4}/{expected} in {tick,4} ticks ({tick * TickSeconds:F2} s) " +
                $"| {(double)replica.Count / Math.Max(1, tick),6:F2} chunks/tick | idle {idleTicks,3} " +
                $"| window full {windowFullTicks,3} " +
                $"| srv {serverSeconds * 1000 / Math.Max(1, tick),6:F2} ms/t (max {worstServer:F1}) " +
                $"| recv {receiveSeconds * 1000 / Math.Max(1, tick),5:F2} " +
                $"| cli {clientSeconds * 1000 / Math.Max(1, tick),6:F2} ms/t (max {worstClient:F1}) " +
                $"| gen {source.Seconds * 1000:F0} ms over {source.Calls} calls";
            server.Dispose(); client.Dispose();
            return trace ? "[profile:pipeline]\n" + header + "\n" + string.Join("\n", samples) : header;
        }
    }
}
