using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Runtime;
using Unity.Mathematics;

namespace DigBlocks.Bootstrap
{
    //bounded visualization content for exercising ordinary authoritative streaming. This remains
    //sample data rather than the world-generation subsystem.
    public sealed class TerrainFixtureChunkSource : IAuthoritativeChunkSource
    {
        public const int NoiseChunkRadius = 6;
        private readonly uint worldId;
        private readonly uint bedrock, stone, dirt, grass, test;

        public TerrainFixtureChunkSource(BlockRegistry registry, uint worldId = 1)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            this.worldId = worldId;
            bedrock = registry.LookupSolid("digblocks:bedrock");
            stone = registry.LookupSolid("digblocks:stone");
            dirt = registry.LookupSolid("digblocks:dirt");
            grass = registry.LookupSolid("digblocks:grass_block");
            test = registry.LookupSolid("digblocks:testblock");
        }

        public CellEdit[] LoadOrGenerate(ChunkAddress address)
        {
            if (address.World != worldId || address.Position.y != 0 ||
                address.Position.x < -NoiseChunkRadius || address.Position.x > NoiseChunkRadius ||
                address.Position.z < -NoiseChunkRadius || address.Position.z > NoiseChunkRadius)
                return Array.Empty<CellEdit>();

            return address.Position.x >= -1 && address.Position.x <= 1 && address.Position.z >= -1 && address.Position.z <= 1
                ? ExistingFixture(address.Position.x, address.Position.z)
                : PerlinTerrain(address.Position.x, address.Position.z);
        }

        private CellEdit[] ExistingFixture(int cx, int cz)
        {
            var edits = new List<CellEdit>(ChunkLayout.Edge * ChunkLayout.Edge * 16);
            for (int z = 0; z < ChunkLayout.Edge; z++)
            for (int x = 0; x < ChunkLayout.Edge; x++)
            {
                int wx = cx * ChunkLayout.Edge + x, wz = cz * ChunkLayout.Edge + z;
                int height = 8 + ((wx + 32) / 16 % 3) * 2 + ((wz + 32) / 32 % 2) * 2;
                bool wall = wx >= 8 && wx < 28 && wz >= 8 && wz < 12;
                if (wall) height = 22;
                bool marker = wx >= -12 && wx < -6 && wz >= -12 && wz < -6;
                for (int y = 0; y < height; y++)
                {
                    uint block = y == 0 ? bedrock : wall ? stone : y == height - 1 ? (marker ? test : grass) : y >= height - 3 ? dirt : stone;
                    edits.Add(new CellEdit(ChunkLayout.Index(new int3(x, y, z)), block, 0));
                }
            }
            return edits.ToArray();
        }

        private CellEdit[] PerlinTerrain(int cx, int cz)
        {
            var edits = new List<CellEdit>(ChunkLayout.Edge * ChunkLayout.Edge * 6);
            for (int z = 0; z < ChunkLayout.Edge; z++)
            for (int x = 0; x < ChunkLayout.Edge; x++)
            {
                int wx = cx * ChunkLayout.Edge + x, wz = cz * ChunkLayout.Edge + z;
                float sample = noise.cnoise(new float2(wx, wz) * 0.035f + new float2(19.37f, -7.11f));
                int extraStone = math.clamp((int)math.floor((sample * 0.5f + 0.5f) * 5f), 0, 4);
                int dirtY = 2 + extraStone;
                int grassY = dirtY + 1;
                for (int y = 0; y <= grassY; y++)
                {
                    uint block = y == 0 ? bedrock : y < dirtY ? stone : y == dirtY ? dirt : grass;
                    edits.Add(new CellEdit(ChunkLayout.Index(new int3(x, y, z)), block, 0));
                }
            }
            return edits.ToArray();
        }
    }
}
