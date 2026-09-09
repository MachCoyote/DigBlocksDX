using System;
using System.Collections;
using System.Collections.Generic;
using DigBlocks.ChunkProtocol;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    public sealed class ChunkTransferIntegrationTests
    {
        [UnityTest] public IEnumerator IpcSnapshotThenInFlightEditDeltaPublishesBeforeAck() => RoundTrip(true);
        [UnityTest] public IEnumerator UdpSnapshotThenInFlightEditDeltaPublishesBeforeAck() => RoundTrip(false);

        private static IEnumerator RoundTrip(bool ipc)
        {
            using var server = new BulkDriver(ipc, true, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            using var client = new BulkDriver(ipc, false, NetworkEndpoint.LoopbackIpv4.WithPort(0));
            var clientConnection = client.Connect(NetworkEndpoint.LoopbackIpv4.WithPort(server.LocalPort));
            NetworkConnection serverConnection = default;
            bool clientReady = false;
            double deadline = Time.realtimeSinceStartupAsDouble + 10;
            while ((!clientReady || !serverConnection.IsCreated) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                server.Update(); client.Update();
                while (server.TryPopEvent(out var item))
                    if (item.Type == BulkEventType.Connected) serverConnection = item.Connection;
                while (client.TryPopEvent(out var item)) clientReady |= item.Type == BulkEventType.Connected;
                yield return null;
            }
            Assert.That(clientReady && serverConnection.IsCreated, Is.True);

            var address = new ChunkAddress(7, new int3(-3, 4, -8));
            var solids = new uint[ChunkLayout.Volume]; var fluids = new uint[ChunkLayout.Volume];
            for (int i = 0; i < solids.Length; i++) { solids[i] = (uint)i; fluids[i] = (uint)(i % 4); }
            using var authoritative = ChunkData.FromChannels(address, 9, 1, solids, fluids);
            byte[] snapshot;
            using (var capture = authoritative.Capture())
                snapshot = ChunkWireCodec.EncodeSnapshot(new ChunkImage(capture.Address, capture.Incarnation,
                    capture.Revision, capture.CopySolids(), capture.CopyFluids()));
            var frames = Frames(new TransferStart(1, 1, address, 9, 1, snapshot.Length, false), snapshot);
            Assert.That(frames.Count, Is.GreaterThan(16), "Exercise reassembly with a genuinely multi-slice snapshot.");
            authoritative.Apply(new[] { new CellEdit(19, 4, 0), new CellEdit(31, 8, 2) }, (uint)solids.Length - 1, 3);
            var delta = new ChunkDelta(address, 9, 1, authoritative.Revision,
                new[] { new ChunkCellUpdate(19, 4, 0), new ChunkCellUpdate(31, 8, 2) });
            var assembler = new ChunkTransferReassembler();
            ChunkData replica = null;
            int sent = 0, acknowledgements = 0;
            deadline = Time.realtimeSinceStartupAsDouble + 20;
            try
            {
                while (acknowledgements < 2 && Time.realtimeSinceStartupAsDouble < deadline)
                {
                    while (sent < frames.Count)
                    {
                        //a full application queue is covered directly by BulkDriverTests; here it only paces the send.
                        if (!server.TrySend(serverConnection, frames[sent])) break;
                        sent++;
                    }
                    server.Update(); client.Update();
                    while (client.TryPopEvent(out var item))
                    {
                        Assert.That(item.Type, Is.Not.EqualTo(BulkEventType.Disconnected));
                        if (item.Type != BulkEventType.Data) continue;
                        if (ChunkTransferFrames.ReadKind(item.Payload) == ChunkFrameKind.Start)
                        { Assert.That(assembler.Begin(ChunkTransferFrames.DecodeStart(item.Payload)), Is.True); continue; }
                        ChunkTransferFrames.DecodeSlice(item.Payload, out ulong id, out int offset, out byte[] bytes);
                        if (!assembler.AddSlice(id, offset, bytes, out var start, out var complete)) continue;
                        Assert.That(start.Address, Is.EqualTo(address)); Assert.That(start.SubscriptionGeneration, Is.EqualTo(1));
                        if (!start.IsDelta)
                        {
                            var image = ChunkWireCodec.DecodeSnapshot(complete, (uint)solids.Length - 1, 3);
                            Assert.That(image.Revision, Is.EqualTo(start.Revision));
                            replica = ChunkData.FromChannels(image.Address, image.Incarnation, image.Revision, image.CopySolids(), image.CopyFluids());
                            Assert.That(replica.SolidAt(19), Is.EqualTo(19), "The captured baseline must survive later server edits.");
                        }
                        else
                        {
                            var update = ChunkWireCodec.DecodeDelta(complete, (uint)solids.Length - 1, 3);
                            Assert.That(update.ResultRevision, Is.EqualTo(start.Revision));
                            var edits = new CellEdit[update.Updates.Count];
                            for (int i = 0; i < edits.Length; i++)
                                edits[i] = new CellEdit(update.Updates[i].Index, update.Updates[i].Solid, update.Updates[i].Fluid);
                            replica.ApplyReplica(edits, update.BaseRevision, update.ResultRevision, (uint)solids.Length - 1, 3);
                        }
                        Assert.That(replica.Revision, Is.EqualTo(start.Revision));
                        Assert.That(client.TrySend(clientConnection, ChunkTransferFrames.EncodeAcknowledgement(start.TransferId, replica.Revision)), Is.True);
                    }
                    while (server.TryPopEvent(out var item))
                    {
                        Assert.That(item.Type, Is.Not.EqualTo(BulkEventType.Disconnected));
                        if (item.Type != BulkEventType.Data) continue;
                        ChunkTransferFrames.DecodeAcknowledgement(item.Payload, out ulong id, out ulong revision);
                        Assert.That(id, Is.EqualTo((ulong)acknowledgements + 1));
                        Assert.That(revision, Is.EqualTo(replica.Revision));
                        acknowledgements++;
                        if (acknowledgements != 1) continue;
                        byte[] encoded = ChunkWireCodec.EncodeDelta(delta);
                        frames = Frames(new TransferStart(2, 1, address, 9, delta.ResultRevision, encoded.Length, true), encoded);
                        sent = 0;
                    }
                    yield return null;
                }
                Assert.That(acknowledgements, Is.EqualTo(2));
                Assert.That(assembler.Count, Is.Zero); Assert.That(assembler.BufferedBytes, Is.Zero);
                Assert.That(replica.Revision, Is.EqualTo(authoritative.Revision));
                for (int i = 0; i < solids.Length; i++)
                {
                    Assert.That(replica.SolidAt(i), Is.EqualTo(authoritative.SolidAt(i)));
                    Assert.That(replica.FluidAt(i), Is.EqualTo(authoritative.FluidAt(i)));
                }
            }
            finally { replica?.Dispose(); }
        }

        private static List<byte[]> Frames(TransferStart start, byte[] bytes)
        {
            var frames = new List<byte[]> { ChunkTransferFrames.EncodeStart(start) };
            for (int offset = 0; offset < bytes.Length; offset += ChunkTransferFrames.MaxSliceBytes)
                frames.Add(ChunkTransferFrames.EncodeSlice(start.TransferId, offset, bytes, offset,
                    Math.Min(ChunkTransferFrames.MaxSliceBytes, bytes.Length - offset)));
            return frames;
        }
    }
}
