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

        public ChunkStreamingOptions CreateOptions()
        {
            if (WorldId < 1) throw new InvalidOperationException("Chunk streaming requires a positive world ID.");
            return new ChunkStreamingOptions(HorizontalRenderDistanceChunks, VerticalRenderDistanceChunks, worldId: (uint)WorldId);
        }

        private void OnValidate()
        {
            WorldId = Math.Max(1, WorldId);
            HorizontalRenderDistanceChunks = Math.Max(0, HorizontalRenderDistanceChunks);
            VerticalRenderDistanceChunks = Math.Max(0, VerticalRenderDistanceChunks);
            long width = 2L * HorizontalRenderDistanceChunks + 1;
            if (width * width * (2L * VerticalRenderDistanceChunks + 1) > ChunkInterest.MaximumChunks)
                Debug.LogWarning($"Chunk streaming distance exceeds the {ChunkInterest.MaximumChunks}-chunk development limit.", this);
        }
    }
}
