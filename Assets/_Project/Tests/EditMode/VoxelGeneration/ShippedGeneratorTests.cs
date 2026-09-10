using System.Collections.Generic;
using System.IO;
using DigBlocks.Voxels.Content;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace DigBlocks.Voxels.Generation.Tests
{
    /// <summary>
    /// Guards the world types the game ships, generating against the real block registry. These
    /// assertions are the contract a generator edit has to update deliberately.
    /// </summary>
    public sealed class ShippedGeneratorTests
    {
        private const int Edge = ChunkLayout.Edge;
        private static BlockRegistry registry;
        private static IBlockResolver blocks;

        [OneTimeSetUp]
        public void LoadContent()
        {
            registry = BlockContentLoader.LoadFromDirectory(
                Path.Combine(Application.streamingAssetsPath, BlockContentLoader.DefaultContentFolder)).Registry;
            blocks = new RegistryBlockResolver(registry);
        }

        private static uint Solid(string key) => registry.LookupSolid(key);
        private static uint Fluid(string key) => registry.LookupFluid(key);
        private static int Index(int x, int y, int z) => ChunkLayout.Index(new int3(x, y, z));

        private static TerrainGenerator Classic(GeneratorSettings settings = null)
        {
            var definition = new ClassicGenerator();
            return definition.Build(new GeneratorBuildContext(new GenSeed(1234UL),
                settings ?? GeneratorSettings.Defaults(definition.Schema), blocks));
        }

        [Test]
        public void BuiltInWorldTypesAreRegistered()
        {
            BuiltInGenerators.RegisterAll();
            Assert.That(TerrainGeneratorRegistry.Resolve(ClassicGenerator.Key).DisplayName, Is.EqualTo("Classic"));
            Assert.That(TerrainGeneratorRegistry.Resolve(FlatGenerator.Key).DisplayName, Is.EqualTo("Flat"));
            Assert.That(BuiltInGenerators.Default.Id, Is.EqualTo(ClassicGenerator.Key));
            //a world saved against a world type this build no longer has must still open.
            Assert.That(BuiltInGenerators.ResolveOrDefault("digblocks:vanished").Id, Is.EqualTo(ClassicGenerator.Key));
        }

        [Test]
        public void TheClassicWorldProducesBothLandAndSea()
        {
            using (var generator = Classic())
            {
                int land = 0, sea = 0;
                //a wide sweep, because a world that is all one or all the other would still pass a
                //single-chunk check.
                for (int chunkZ = -6; chunkZ <= 6; chunkZ += 3)
                for (int chunkX = -6; chunkX <= 6; chunkX += 3)
                //sea level sits at 62, so the water surface is in chunk 1 while most land tops out in
                //chunk 2. Sampling only one of them would miss whichever it is not.
                for (int chunkY = 1; chunkY <= 2; chunkY++)
                {
                    var solids = new uint[ChunkLayout.Volume];
                    var fluids = new uint[ChunkLayout.Volume];
                    generator.Generate(chunkX, chunkY, chunkZ, solids, fluids);
                    for (int index = 0; index < ChunkLayout.Volume; index++)
                    {
                        if (fluids[index] == Fluid("digblocks:water")) sea++;
                        if (solids[index] == Solid("digblocks:grass_block")) land++;
                    }
                }
                Assert.That(land, Is.GreaterThan(0), "The classic world produced no grass anywhere.");
                Assert.That(sea, Is.GreaterThan(0), "The classic world produced no water anywhere.");
            }
        }

        [Test]
        public void TheClassicWorldFloorsInBedrock()
        {
            using (var generator = Classic())
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                //chunk -2 covers world Y -64 to -33, so the layer's floor sits at its base.
                generator.Generate(0, -2, 0, solids, fluids);

                uint bedrock = Solid("digblocks:bedrock");
                for (int z = 0; z < Edge; z++)
                for (int x = 0; x < Edge; x++)
                    Assert.That(solids[Index(x, 0, z)], Is.EqualTo(bedrock), $"World Y -64 at ({x}, {z}) is not bedrock.");

                //the floor is uneven, so a few courses up it must be a mix rather than a flat slab.
                int bedrockAbove = 0;
                for (int z = 0; z < Edge; z++)
                for (int x = 0; x < Edge; x++)
                    if (solids[Index(x, 2, z)] == bedrock) bedrockAbove++;
                Assert.That(bedrockAbove, Is.InRange(1, Edge * Edge - 1), "The bedrock floor is flat, so its roughness is not reaching the fill.");
            }
        }

        [Test]
        public void NothingIsGeneratedBelowTheLayer()
        {
            using (var generator = Classic())
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                //chunk -3 covers world Y -96 to -65, entirely below the overworld's bottom bound.
                generator.Generate(0, -3, 0, solids, fluids);
                Assert.That(solids, Is.All.EqualTo(0u));
                Assert.That(fluids, Is.All.EqualTo(0u));
            }
        }

        [Test]
        public void DryGroundIsCappedWithGrassAndShoresWithSand()
        {
            using (var generator = Classic())
            {
                uint grass = Solid("digblocks:grass_block");
                uint sand = Solid("digblocks:sand");
                uint water = Fluid("digblocks:water");
                int seaLevel = GeneratorSettings.Defaults(new ClassicGenerator().Schema).Integer(ClassicGenerator.SeaLevel);

                int grassColumns = 0, sandColumns = 0;
                //whole chunks rather than a handful of columns, because a coastline is a thin thing to
                //land on by sampling.
                foreach (var chunk in new[] { new int2(0, 0), new int2(-5, 4), new int2(9, -7) })
                {
                    var stack = ReadStack(generator, chunk.x, chunk.y, chunkLow: 0, chunkHigh: 3);
                    for (int z = 0; z < Edge; z++)
                    for (int x = 0; x < Edge; x++)
                    {
                        int surface = TopSolidInStack(stack, x, z);
                        if (surface < 0) continue;

                        if (surface > seaLevel)
                        {
                            Assert.That(stack.solids[surface * Edge * Edge + x + Edge * z], Is.EqualTo(grass),
                                $"Dry ground at Y {surface} is not grass.");
                            grassColumns++;
                        }
                        else
                        {
                            Assert.That(stack.solids[surface * Edge * Edge + x + Edge * z], Is.EqualTo(sand),
                                $"Ground at the water line at Y {surface} is not sand.");
                            //ground standing exactly at sea level occupies the topmost water cell, so
                            //it is dry sand: the beach, one block proud of the water beside it.
                            if (surface < seaLevel)
                                Assert.That(stack.fluids[(surface + 1) * Edge * Edge + x + Edge * z], Is.EqualTo(water),
                                    $"The cell above submerged ground at Y {surface} holds no water.");
                            sandColumns++;
                        }
                    }
                }

                Assert.That(grassColumns, Is.GreaterThan(0), "No sampled column stood above the water line.");
                Assert.That(sandColumns, Is.GreaterThan(0), "No sampled column met the water, so the shore rule went untested.");
            }
        }

        [Test]
        public void AmplificationWidensTheRangeOfHeights()
        {
            var schema = new ClassicGenerator().Schema;
            int Spread(float amplification)
            {
                using (var generator = Classic(GeneratorSettings.Defaults(schema).With(ClassicGenerator.Amplification, amplification)))
                {
                    int low = int.MaxValue, high = int.MinValue;
                    for (int chunkX = -3; chunkX <= 3; chunkX++)
                    {
                        var column = ReadColumnStack(generator, chunkX, 0, 4, 4, chunkLow: -1, chunkHigh: 5);
                        int surface = TopSolid(column);
                        if (surface == int.MinValue) continue;
                        low = math.min(low, surface);
                        high = math.max(high, surface);
                    }
                    return high - low;
                }
            }

            Assert.That(Spread(3f), Is.GreaterThan(Spread(1f)), "Amplifying the world did not make its heights differ more.");
        }

        /// <summary>
        /// The cross-platform reproducibility guard. If a seed no longer produces this world, either a
        /// generator changed on purpose, in which case update the constant, or something in the noise
        /// path has drifted, in which case every existing world has silently changed.
        /// </summary>
        [Test]
        public void TheClassicWorldMatchesItsRecordedFingerprint()
        {
            using (var generator = Classic())
                Assert.That(Fingerprint(generator), Is.EqualTo(1326956985995353685UL));
        }

        [Test]
        public void TheFlatWorldMatchesItsRecordedFingerprint()
        {
            var definition = new FlatGenerator();
            using (var generator = definition.Build(new GenSeed(1234UL), blocks))
                Assert.That(Fingerprint(generator), Is.EqualTo(12012289552818127653UL));
        }

        [Test]
        public void TheFlatWorldIsLevel()
        {
            var definition = new FlatGenerator();
            using (var generator = definition.Build(new GenSeed(7UL), blocks))
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                generator.Generate(5, 0, -9, solids, fluids);

                for (int z = 0; z < Edge; z++)
                for (int x = 0; x < Edge; x++)
                {
                    Assert.That(solids[Index(x, 5, z)], Is.EqualTo(0u));
                    Assert.That(solids[Index(x, 4, z)], Is.EqualTo(Solid("digblocks:grass_block")));
                    Assert.That(solids[Index(x, 3, z)], Is.EqualTo(Solid("digblocks:dirt")));
                    Assert.That(solids[Index(x, 2, z)], Is.EqualTo(Solid("digblocks:dirt")));
                    Assert.That(solids[Index(x, 1, z)], Is.EqualTo(Solid("digblocks:stone")));
                    Assert.That(solids[Index(x, 0, z)], Is.EqualTo(Solid("digblocks:bedrock")));
                }
                Assert.That(fluids, Is.All.EqualTo(0u), "The flat world has no sea.");
            }
        }

        [Test]
        public void TheFlatWorldCanDropItsBedrock()
        {
            var definition = new FlatGenerator();
            var settings = GeneratorSettings.Defaults(definition.Schema).With(FlatGenerator.Bedrock, false);
            using (var generator = definition.Build(new GeneratorBuildContext(new GenSeed(7UL), settings, blocks)))
            {
                var solids = new uint[ChunkLayout.Volume];
                var fluids = new uint[ChunkLayout.Volume];
                generator.Generate(0, 0, 0, solids, fluids);
                Assert.That(solids[Index(0, 1, 0)], Is.EqualTo(Solid("digblocks:stone")));
                Assert.That(solids[Index(0, 0, 0)], Is.EqualTo(0u), "Without a bedrock course the layer starts one block higher.");
            }
        }

        [Test]
        public void AGeneratorNamingAnUnknownBlockFailsWithTheKey()
        {
            var definition = new BrokenGenerator();
            var exception = Assert.Throws<GenerationContentException>(() => definition.Build(new GenSeed(1UL), blocks));
            Assert.That(exception.Message, Does.Contain("digblocks:unobtainium"));
        }

        //a stable hash over a spread of chunks, including ones above, below and beside the terrain.
        private static ulong Fingerprint(TerrainGenerator generator)
        {
            ulong hash = 14695981039346656037UL;
            var solids = new uint[ChunkLayout.Volume];
            var fluids = new uint[ChunkLayout.Volume];
            var addresses = new[]
            {
                new int3(0, 0, 0), new int3(0, 1, 0), new int3(0, 2, 0), new int3(0, -2, 0),
                new int3(3, 2, -5), new int3(-7, 1, 11), new int3(40, 2, -40)
            };

            foreach (var address in addresses)
            {
                System.Array.Clear(solids, 0, solids.Length);
                System.Array.Clear(fluids, 0, fluids.Length);
                generator.Generate(address.x, address.y, address.z, solids, fluids);
                for (int index = 0; index < ChunkLayout.Volume; index++)
                {
                    hash = (hash ^ solids[index]) * 1099511628211UL;
                    hash = (hash ^ fluids[index]) * 1099511628211UL;
                }
            }
            return hash;
        }

        //a vertical run of chunks flattened into one array indexed by world Y from chunkLow * 32.
        private static (uint[] solids, uint[] fluids) ReadStack(TerrainGenerator generator,
            int chunkX, int chunkZ, int chunkLow, int chunkHigh)
        {
            int slices = (chunkHigh - chunkLow + 1) * Edge;
            var solids = new uint[slices * Edge * Edge];
            var fluids = new uint[solids.Length];
            var chunkSolids = new uint[ChunkLayout.Volume];
            var chunkFluids = new uint[ChunkLayout.Volume];

            for (int chunkY = chunkLow; chunkY <= chunkHigh; chunkY++)
            {
                System.Array.Clear(chunkSolids, 0, chunkSolids.Length);
                System.Array.Clear(chunkFluids, 0, chunkFluids.Length);
                generator.Generate(chunkX, chunkY, chunkZ, chunkSolids, chunkFluids);
                int offset = (chunkY - chunkLow) * ChunkLayout.Volume;
                System.Array.Copy(chunkSolids, 0, solids, offset, ChunkLayout.Volume);
                System.Array.Copy(chunkFluids, 0, fluids, offset, ChunkLayout.Volume);
            }
            return (solids, fluids);
        }

        private static int TopSolidInStack((uint[] solids, uint[] fluids) stack, int x, int z)
        {
            int slices = stack.solids.Length / (Edge * Edge);
            for (int y = slices - 1; y >= 0; y--)
                if (stack.solids[y * Edge * Edge + x + Edge * z] != 0u) return y;
            return -1;
        }

        private static (uint[] solids, uint[] fluids, int minY) ReadColumnStack(TerrainGenerator generator,
            int chunkX, int chunkZ, int localX, int localZ, int chunkLow, int chunkHigh)
        {
            int minY = chunkLow * Edge;
            var solids = new uint[(chunkHigh - chunkLow + 1) * Edge];
            var fluids = new uint[solids.Length];
            var chunkSolids = new uint[ChunkLayout.Volume];
            var chunkFluids = new uint[ChunkLayout.Volume];

            for (int chunkY = chunkLow; chunkY <= chunkHigh; chunkY++)
            {
                System.Array.Clear(chunkSolids, 0, chunkSolids.Length);
                System.Array.Clear(chunkFluids, 0, chunkFluids.Length);
                generator.Generate(chunkX, chunkY, chunkZ, chunkSolids, chunkFluids);
                for (int localY = 0; localY < Edge; localY++)
                {
                    int offset = (chunkY - chunkLow) * Edge + localY;
                    solids[offset] = chunkSolids[Index(localX, localY, localZ)];
                    fluids[offset] = chunkFluids[Index(localX, localY, localZ)];
                }
            }
            return (solids, fluids, minY);
        }

        //returns a world Y, offset so the caller can index the stack directly.
        private static int TopSolid((uint[] solids, uint[] fluids, int minY) column)
        {
            for (int index = column.solids.Length - 1; index >= 0; index--)
                if (column.solids[index] != 0u) return index;
            return int.MinValue;
        }

        private sealed class BrokenGenerator : TerrainGeneratorDefinition
        {
            public override string Id => "digblocks.tests:broken";
            public override string DisplayName => "Broken";
            protected override IReadOnlyList<WorldLayer> CreateLayers(GeneratorBuildContext context)
                => new[] { WorldLayer.Named("bad").Bounds(0, 31)
                    .Band("ground", BandDirection.Up, BandSurface.At(4), ColumnRecipe.Solid("digblocks:unobtainium")).Build() };
        }
    }
}
