using System;
using DigBlocks.ChunkProtocol;
using UnityEngine;

namespace DigBlocks.Networking.NetCode
{
    [CreateAssetMenu(menuName = "DigBlocks/Chunk streaming settings")]
    public sealed class ChunkStreamingSettings : ScriptableObject
    {
        [Min(1)] public int WorldId = 1;
        [Min(0)] public int HorizontalRenderDistanceChunks = 2;
        [Min(0)] public int VerticalRenderDistanceChunks = 1;

        [Header("Transfer budgets")]
        [Tooltip("Bytes the server may send across all peers in one tick.")]
        [Min(BulkDriver.MaxPayloadBytes)] public int GlobalBytesPerTick = 262144;
        [Tooltip("Bytes the server may send to one peer in one tick.")]
        [Min(BulkDriver.MaxPayloadBytes)] public int PeerBytesPerTick = 131072;
        [Tooltip("Chunks outstanding to one peer at once, counting those already sent and waiting to be "
            + "applied. Most hold no payload, so this can be deep; it is what stops acknowledgement "
            + "latency from capping the load rate.")]
        [Range(1, 1024)] public int PeerWindow = 8;
        [Tooltip("Encoded chunk payloads held across all peers. Bounds the transfer stage's memory.")]
        [Range(2, 256)] public int MaxBufferedPayloads = 64;
        [Tooltip("Chunks the client decodes and stores per tick. Bounds how much of a frame an arriving "
            + "window can take; raising it lifts the peak load rate but makes each frame lumpier.")]
        [Range(1, 64)] public int AppliesPerTick = 2;

        public ChunkStreamingOptions CreateOptions()
        {
            if (WorldId < 1) throw new InvalidOperationException("Chunk streaming requires a positive world ID.");
            return new ChunkStreamingOptions(HorizontalRenderDistanceChunks, VerticalRenderDistanceChunks,
                GlobalBytesPerTick, PeerBytesPerTick, MaxBufferedPayloads, worldId: (uint)WorldId,
                peerWindow: PeerWindow, appliesPerTick: AppliesPerTick);
        }

        private void OnValidate()
        {
            WorldId = Math.Max(1, WorldId);
            HorizontalRenderDistanceChunks = Math.Clamp(HorizontalRenderDistanceChunks, 0, ChunkInterest.MaximumRadius);
            VerticalRenderDistanceChunks = Math.Clamp(VerticalRenderDistanceChunks, 0, ChunkInterest.MaximumRadius);
            GlobalBytesPerTick = Math.Clamp(GlobalBytesPerTick, BulkDriver.MaxPayloadBytes, 8388608);
            PeerBytesPerTick = Math.Clamp(PeerBytesPerTick, BulkDriver.MaxPayloadBytes, GlobalBytesPerTick);
            MaxBufferedPayloads = Math.Clamp(MaxBufferedPayloads, 2, 256);
            PeerWindow = Math.Clamp(PeerWindow, 1, 1024);
            AppliesPerTick = Math.Clamp(AppliesPerTick, 1, 64);
            if (ChunkInterest.CountFor(HorizontalRenderDistanceChunks, VerticalRenderDistanceChunks) > ChunkInterest.MaximumChunks)
                Debug.LogWarning($"Chunk streaming distance exceeds the {ChunkInterest.MaximumChunks}-chunk interest limit.", this);
        }
    }
}
