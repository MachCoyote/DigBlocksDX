using System.IO;
using DigBlocks.Voxels.Content;
using NUnit.Framework;
using UnityEngine;

namespace DigBlocks.Voxels.Generation.Tests
{
    /// <summary>
    /// The demo world type is a worked reference, not content anyone plays, so it is not pinned to a
    /// fingerprint or held to a shape. It is registered alongside the shipped world types though, so
    /// this checks the one thing that matters: that it still builds and still produces a world.
    /// </summary>
    public sealed class DemoGeneratorTests
    {
        private static IBlockResolver Blocks() => new RegistryBlockResolver(
            BlockContentLoader.LoadFromDirectory(
                Path.Combine(Application.streamingAssetsPath, BlockContentLoader.DefaultContentFolder)).Registry);

        [Test]
        public void TheDemoWorldBuildsAndGenerates()
        {
            var definition = new DemoGenerator();
            using (var generator = definition.Build(new GenSeed(2024UL), Blocks()))
            {
                //one chunk from each layer's band of the world, including the one a kilometre up.
                int[] chunkYs = { -8, 1, 2, 12, 31 };
                int nonEmpty = 0;
                foreach (int chunkY in chunkYs)
                {
                    var solids = new uint[ChunkLayout.Volume];
                    var fluids = new uint[ChunkLayout.Volume];
                    generator.Generate(0, chunkY, 0, solids, fluids);
                    foreach (uint state in solids) if (state != 0u) { nonEmpty++; break; }
                }
                Assert.That(nonEmpty, Is.GreaterThan(3), "Most of the demo world's layers should have produced something.");
            }
        }

        [Test]
        public void TheDemoWorldIsRegistered()
        {
            BuiltInGenerators.RegisterAll();
            Assert.That(TerrainGeneratorRegistry.Resolve(DemoGenerator.Key).DisplayName, Is.EqualTo("Demo"));
        }

        [Test]
        public void TheDemoWorldRespectsItsOwnSettings()
        {
            var definition = new DemoGenerator();
            var withoutIslands = GeneratorSettings.Defaults(definition.Schema).With(DemoGenerator.SkyIslands, false);
            using (var generator = definition.Build(new GeneratorBuildContext(new GenSeed(2024UL), withoutIslands, Blocks())))
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                //chunk 31 covers world Y 992 to 1023, which is where the islands would be.
                generator.Generate(0, 31, 0, solids, fluids);
                Assert.That(solids, Is.All.EqualTo(0u), "Turning the islands off should leave that height empty.");
            }
        }
    }
}
