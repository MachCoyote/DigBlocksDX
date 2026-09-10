using System.Collections.Generic;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class TerrainFixtureChunkSourceTests
    {
        [Test]
        public void PreservesTheAuthoredFixtureAndBoundsPerlinSampleTerrain()
        {
            var content = BlockContentProvider.Load();
            var source = new TerrainFixtureChunkSource(content.Registry);
            uint stone = content.Registry.LookupSolid("digblocks:stone");
            uint grass = content.Registry.LookupSolid("digblocks:grass_block");
            uint test = content.Registry.LookupSolid("digblocks:testblock");

            var center = Cells(source, new ChunkAddress(1, int3.zero));
            Assert.That(center[ChunkLayout.Index(new int3(8, 21, 8))], Is.EqualTo(stone), "The existing wall remains unchanged.");
            Assert.That(center.ContainsKey(ChunkLayout.Index(new int3(8, 22, 8))), Is.False);
            Assert.That(center[ChunkLayout.Index(new int3(0, 13, 0))], Is.EqualTo(grass));
            var marker = Cells(source, new ChunkAddress(1, new int3(-1, 0, -1)));
            Assert.That(marker[ChunkLayout.Index(new int3(20, 9, 20))], Is.EqualTo(test));

            Assert.That(Cells(source, new ChunkAddress(1, new int3(7, 0, 0))), Is.Empty);
            Assert.That(Cells(source, new ChunkAddress(1, new int3(2, 1, 0))), Is.Empty);
        }

        [Test]
        public void PerlinColumnsUseTheRequestedLayerOrderAndZeroToFourExtraStoneLayers()
        {
            var content = BlockContentProvider.Load();
            var source = new TerrainFixtureChunkSource(content.Registry);
            uint bedrock = content.Registry.LookupSolid("digblocks:bedrock");
            uint stone = content.Registry.LookupSolid("digblocks:stone");
            uint dirt = content.Registry.LookupSolid("digblocks:dirt");
            uint grass = content.Registry.LookupSolid("digblocks:grass_block");
            var cells = Cells(source, new ChunkAddress(1, new int3(2, 0, 0)));
            var tops = new HashSet<int>();

            for (int z = 0; z < ChunkLayout.Edge; z++)
            for (int x = 0; x < ChunkLayout.Edge; x++)
            {
                int top = 0;
                while (cells.ContainsKey(ChunkLayout.Index(new int3(x, top + 1, z)))) top++;
                tops.Add(top);
                Assert.That(top, Is.InRange(3, 7));
                Assert.That(cells[ChunkLayout.Index(new int3(x, 0, z))], Is.EqualTo(bedrock));
                for (int y = 1; y < top - 1; y++)
                    Assert.That(cells[ChunkLayout.Index(new int3(x, y, z))], Is.EqualTo(stone));
                Assert.That(cells[ChunkLayout.Index(new int3(x, top - 1, z))], Is.EqualTo(dirt));
                Assert.That(cells[ChunkLayout.Index(new int3(x, top, z))], Is.EqualTo(grass));
            }
            Assert.That(tops.Count, Is.GreaterThan(1), "The sample should visibly demonstrate height noise.");
        }

        //the source fills a whole chunk of cells, so the assertions below read the placed ones back out.
        private static Dictionary<int, uint> Cells(TerrainFixtureChunkSource source, ChunkAddress address)
        {
            var solids = new uint[ChunkLayout.Volume];
            var fluids = new uint[ChunkLayout.Volume];
            source.Generate(address, solids, fluids);
            var result = new Dictionary<int, uint>();
            for (int i = 0; i < solids.Length; i++) if (solids[i] != 0) result.Add(i, solids[i]);
            foreach (uint fluid in fluids) Assert.That(fluid, Is.Zero, "The fixture places no fluid.");
            return result;
        }
    }
}
