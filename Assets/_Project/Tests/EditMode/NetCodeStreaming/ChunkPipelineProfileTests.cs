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
    /// Temporary profiling harness. Not a regression guard: these tests measure and report, they do not
    /// assert throughput. Delete once the findings are acted on.
    /// </summary>
    [Explicit("Profiling harness: run by name, not as part of the suite.")]
    public sealed class ChunkPipelineProfileTests
    {
        private const double TickSeconds = 1.0 / 60.0;

        private sealed class LayeredSource : IAuthoritativeChunkSource
        {
            public int Calls; public double Seconds;
            public CellEdit[] LoadOrGenerate(ChunkAddress address)
            {
                var clock = Stopwatch.StartNew();
                try
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
                finally { Calls++; Seconds += clock.Elapsed.TotalSeconds; }
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
            var store = world.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512, 16);
            var source = new LayeredSource();

            //authoritative load of one representative chunk: generate plus the per-cell Apply path.
            var address = new ChunkAddress(1, new int3(0, 0, 0));
            var lease = store.Acquire(address);
            var genClock = Stopwatch.StartNew();
            store.EnsureLoaded(lease, source);
            genClock.Stop();

            //the same content as flat channels, which is what the encoder and the client both see.
            var solids = new uint[ChunkLayout.Volume];
            var fluids = new uint[ChunkLayout.Volume];
            foreach (var edit in source.LoadOrGenerate(address)) solids[edit.Index] = edit.Solid;
            var image = ChunkImage.FromOwnedChannels(address, 1, 1, solids, fluids);

            const int iterations = 40;
            byte[] payload = ChunkWireCodec.EncodeSnapshot(image);
            double encode = Time(iterations, () => ChunkWireCodec.EncodeSnapshot(image));

            var destSolids = new uint[ChunkLayout.Volume];
            var destFluids = new uint[ChunkLayout.Volume];
            double decode = Time(iterations, () => ChunkWireCodec.DecodeSnapshotInto(payload, registry.MaxSolidStateId,
                registry.MaxFluidStateId, destSolids, destFluids, out _, out _, out _));

            double fromValues = Time(iterations, () => { var channel = PaletteChannel.FromValues(destSolids); channel.Dispose(); });
            double fromChannels = Time(iterations, () =>
            { var data = ChunkData.FromChannels(lease.Address, 1, 1, destSolids, destFluids); data.Dispose(); });

            double validate = Time(iterations, () =>
            {
                for (int i = 0; i < ChunkLayout.Volume; i++)
                { var solid = registry.GetSolid(destSolids[i]); registry.GetFluid(destFluids[i]); if (solid.PermitsFluid) { } }
            });

            //publish into a replica store, revision-stepped so each call takes the full build path.
            using var replicaWorld = new World("Stage costs replica");
            var replica = replicaWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 512, 16);
            replica.EnableReplicas();
            replica.SetReplicaInterest(new ChunkInterest(1, new ChunkAddress(1, default), 3, 2));
            ulong revision = 1;
            double publish = Time(iterations, () =>
                replica.PublishReplica(1, address, 1, ++revision, destSolids, destFluids));

            int solidPalette = Distinct(destSolids), fluidPalette = Distinct(destFluids);
            Debug.Log($"[profile:stage] payload={payload.Length}B solidPalette={solidPalette} fluidPalette={fluidPalette}");
            Debug.Log($"[profile:stage] worldgen_generate_and_apply={genClock.Elapsed.TotalMilliseconds:F2}ms server_main_thread");
            Debug.Log($"[profile:stage] EncodeSnapshot={encode:F3}ms server_worker");
            Debug.Log($"[profile:stage] DecodeSnapshotInto={decode:F3}ms client_main_thread");
            Debug.Log($"[profile:stage] PublishReplica={publish:F3}ms client_main_thread");
            Debug.Log($"[profile:stage] validation_loop={validate:F3}ms");
            Debug.Log($"[profile:stage] FromChannels={fromChannels:F3}ms FromValues_solids_only={fromValues:F3}ms");
            Debug.Log($"[profile:stage] client_per_chunk_total={decode + publish:F3}ms");

            lease.Dispose();
        }

        private static int Distinct(uint[] values)
        {
            var seen = new HashSet<uint>();
            foreach (uint value in values) seen.Add(value);
            return seen.Count;
        }

        private static double Time(int iterations, Action action)
        {
            action();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            return clock.Elapsed.TotalMilliseconds / iterations;
        }

        //---------------------------------------------------------------- pipeline accounting

        [Test]
        public void PipelineTickAccounting()
        {
            var report = Run("h=3 v=2", new ChunkStreamingOptions(3, 2), 900, trace: true);
            Debug.Log(report);
        }

        [Test]
        public void ParameterSweep()
        {
            var lines = new List<string>();
            foreach (int window in new[] { 8, 16, 32, 64 })
            foreach (int applies in new[] { 2, 4, 8, 16 })
            {
                var options = new ChunkStreamingOptions(3, 2, maxPayloads: 256, peerWindow: window,
                    snapshotWorkers: 32, appliesPerTick: applies);
                lines.Add(Run($"window {window,2} applies {applies,2}", options, 900, trace: false));
            }
            Debug.Log("[profile:sweep]\n" + string.Join("\n", lines));
        }

        [Test]
        public void UnboundedBudgets()
        {
            //everything opened up: shows the ceiling the current stage costs allow at 60 Hz.
            var options = new ChunkStreamingOptions(3, 2, globalBytesPerTick: 8388608, peerBytesPerTick: 8388608,
                maxPayloads: 256, peerWindow: 64, snapshotWorkers: 64, appliesPerTick: 64);
            Debug.Log(Run("unbounded", options, 900, trace: true));
        }

        private static string Run(string label, ChunkStreamingOptions options, int tickBudget, bool trace)
        {
            using var serverWorld = new World($"Profile server {label}");
            using var clientWorld = new World($"Profile client {label}");
            var registry = Registry();
            var store = serverWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 1024, options.SnapshotWorkers);
            var replica = clientWorld.GetOrCreateSystemManaged<ChunkWorldSystem>().Configure(registry, 1024, options.SnapshotWorkers);
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
            int idleTicks = 0, encoderPeggedTicks = 0, windowFullTicks = 0;
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

                int inFlight = server.PayloadCount, pending = store.PendingSnapshots;
                if (pending >= options.SnapshotWorkers) encoderPeggedTicks++;
                if (inFlight >= options.PeerWindow) windowFullTicks++;

                stage.Restart();
                while (toClient.Count > 0) client.Receive(toClient.Dequeue(), now);
                stage.Stop(); receiveSeconds += stage.Elapsed.TotalSeconds;

                stage.Restart(); client.Tick(now); stage.Stop();
                clientSeconds += stage.Elapsed.TotalSeconds; worstClient = Math.Max(worstClient, stage.Elapsed.TotalMilliseconds);

                int applied = replica.Count - previousCount;
                previousCount = replica.Count;
                if (applied == 0) idleTicks++;
                if (trace && (tick < 20 || tick % 10 == 0))
                    samples.Add($"    t{tick,3} applied {applied,2} total {replica.Count,3} inflight {inFlight,2} " +
                        $"encodes {pending,2} ready {store.ReadySnapshots,2} srv {stage.Elapsed.TotalMilliseconds,5:F2}");
            }

            double elapsed = clock.Elapsed.TotalSeconds;
            string header = $"  {label,-24} {replica.Count,3}/{expected} in {tick,4} ticks ({tick * TickSeconds:F2} s) " +
                $"| {(double)replica.Count / Math.Max(1, tick),5:F2} chunks/tick | idle {idleTicks,3} " +
                $"| encoder pegged {encoderPeggedTicks,3} | window full {windowFullTicks,3} " +
                $"| srv {serverSeconds * 1000 / Math.Max(1, tick),5:F2} ms/t (max {worstServer:F1}) " +
                $"| recv {receiveSeconds * 1000 / Math.Max(1, tick),5:F2} " +
                $"| cli {clientSeconds * 1000 / Math.Max(1, tick),5:F2} ms/t (max {worstClient:F1}) " +
                $"| gen {source.Seconds * 1000:F0} ms over {source.Calls} calls";
            server.Dispose(); client.Dispose();
            return trace ? "[profile:pipeline]\n" + header + "\n" + string.Join("\n", samples) : header;
        }
    }
}
