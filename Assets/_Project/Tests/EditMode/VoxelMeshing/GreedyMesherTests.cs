using System.Collections.Generic;
using System;
using System.Runtime.InteropServices;
using DigBlocks.Voxels.Appearance;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing.Tests
{
    public sealed class GreedyMesherTests
    {
        private NativeArray<uint> voxels;
        private NativeArray<BlockAttributes> attributes;
        private BlockAppearanceTable appearance;
        private NativeArray<ulong> mask;
        private NativeList<PackedQuad> output;

        [SetUp]
        public void SetUp()
        {
            var content = BlockContentCompiler.Compile(new[] { new RenderMaterialDefinition("test:opaque", BlockRenderLayer.Opaque, 4) },
                null, new[] {
                    new BlockDefinition { Key = "digblocks:air", Invisible = true, Attributes = new BlockAttributeOverrides { Opaque = false, FullCube = false } },
                    new BlockDefinition { Key = "digblocks:empty", Channel = BlockChannel.Fluid, Invisible = true },
                    new BlockDefinition { Key = "test:stone", Appearance = new BlockAppearanceOverrides { MaterialKey = "test:opaque", Texture = 1 } }
                });
            voxels = new NativeArray<uint>(GreedyMesherJob.PaddedVolume, Allocator.Persistent);
            attributes = new NativeArray<BlockAttributes>(2, Allocator.Persistent);
            attributes[0] = BlockAttributes.Air; attributes[1] = BlockAttributes.Default;
            appearance = BlockAppearanceTable.Create(content, Allocator.Persistent);
            mask = new NativeArray<ulong>(1024, Allocator.Persistent);
            output = new NativeList<PackedQuad>(GreedyMesherJob.MaximumQuads, Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        { output.Dispose(); mask.Dispose(); appearance.Dispose(); attributes.Dispose(); voxels.Dispose(); }

        private void Build() => new GreedyMesherJob { Voxels = voxels, Attributes = attributes.AsReadOnly(), Appearance = appearance.AsReadOnly(),
            Mask = mask, Output = output, Slot = 7, Seed = 123, ChunkPosition = new int3(-1, 0, 2) }.Schedule().Complete();

        [Test]
        public void UniformFullChunkIsSixGreedyQuads()
        {
            for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++)
                voxels[GreedyMesherJob.Index(new int3(x, y, z))] = 1;
            Build();
            Assert.That(output.Length, Is.EqualTo(6));
            var directions = new HashSet<BlockFace>();
            foreach (var quad in output)
            {
                directions.Add(quad.Face);
                Assert.That(quad.Width, Is.EqualTo(32)); Assert.That(quad.Height, Is.EqualTo(32));
                Assert.That(quad.Texture, Is.EqualTo(1)); Assert.That(quad.ChunkSlot, Is.EqualTo(7));
            }
            Assert.That(directions.Count, Is.EqualTo(6));
        }

        [TestCase("same", 6)]
        [TestCase("texture", 10)]
        [TestCase("rotation", 10)]
        [TestCase("tint", 10)]
        [TestCase("material", 10)]
        public void MergeIdentityUsesRenderPropertiesInsteadOfStateId(string difference, int expected)
        {
            var first = new BlockAppearanceOverrides { MaterialKey = "test:a", Texture = 1 };
            var second = first.Clone();
            if (difference == "texture") second.Texture = 2;
            if (difference == "rotation") second.Rotation = 1;
            if (difference == "tint") second.TintKey = "test:tint";
            if (difference == "material") second.MaterialKey = "test:b";
            var content = BlockContentCompiler.Compile(new[] {
                new RenderMaterialDefinition("test:a", BlockRenderLayer.Opaque, 4),
                new RenderMaterialDefinition("test:b", BlockRenderLayer.Opaque, 4) }, null, new[] {
                new BlockDefinition { Key = "digblocks:air", Invisible = true, Attributes = new BlockAttributeOverrides { Opaque = false, FullCube = false } },
                new BlockDefinition { Key = "digblocks:empty", Channel = BlockChannel.Fluid, Invisible = true },
                new BlockDefinition { Key = "test:first", Appearance = first },
                new BlockDefinition { Key = "test:second", Appearance = second } });
            appearance.Dispose(); appearance = BlockAppearanceTable.Create(content, Allocator.Persistent);
            attributes.Dispose(); attributes = new NativeArray<BlockAttributes>(3, Allocator.Persistent);
            attributes[0] = BlockAttributes.Air; attributes[1] = attributes[2] = BlockAttributes.Default;
            voxels[GreedyMesherJob.Index(int3.zero)] = content.Registry.LookupSolid("test:first");
            voxels[GreedyMesherJob.Index(new int3(1, 0, 0))] = content.Registry.LookupSolid("test:second");
            Build();
            Assert.That(output.Length, Is.EqualTo(expected));
        }

        [Test]
        public void EmptyAndFullySurroundedChunksEmitNothing()
        {
            Build(); Assert.That(output.Length, Is.Zero);
            for (int i = 0; i < voxels.Length; i++) voxels[i] = 1;
            Build(); Assert.That(output.Length, Is.Zero);
        }

        [TestCase(17)] [TestCase(812)] [TestCase(139)]
        public void GreedyCoverageMatchesExposedFaceOracleWithoutOverlap(int seed)
        {
            var random = new System.Random(seed);
            for (int y = 0; y < 8; y++) for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++)
                voxels[GreedyMesherJob.Index(new int3(x, y, z))] = (uint)random.Next(2);
            Build();
            var expected = new HashSet<(int3, BlockFace)>();
            var actual = new HashSet<(int3, BlockFace)>();
            for (int y = 0; y < 8; y++) for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++)
            {
                var cell = new int3(x, y, z);
                if (voxels[GreedyMesherJob.Index(cell)] == 0) continue;
                foreach (BlockFace face in Enum.GetValues(typeof(BlockFace)))
                {
                    FaceBasis.Get(face, out var n, out _, out _);
                    if (voxels[GreedyMesherJob.Index(cell + n)] == 0) expected.Add((cell, face));
                }
            }
            foreach (var quad in output)
            {
                FaceBasis.Get(quad.Face, out var n, out var u, out var v);
                var start = quad.Anchor - FaceBasis.AnchorOffset(n, u, v);
                for (int y = 0; y < quad.Height; y++) for (int x = 0; x < quad.Width; x++)
                    Assert.That(actual.Add((start + x * u + y * v, quad.Face)), Is.True, "Overlapping greedy rectangles");
            }
            Assert.That(actual.SetEquals(expected), Is.True, "Missing or extra exposed faces");
        }

        [Test]
        public void OneNeighborSlabRemovesOnlyTheSharedBoundary()
        {
            for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++) for (int x = 0; x <= 32; x++)
                voxels[GreedyMesherJob.Index(new int3(x, y, z))] = 1;
            Build();
            Assert.That(output.Length, Is.EqualTo(5));
            foreach (var quad in output) Assert.That(quad.Face, Is.Not.EqualTo(BlockFace.East));
        }

        [Test]
        public void PackedExtremesRoundTripInTwelveBytes()
        {
            var quad = new PackedQuad(new int3(32), 32, 32, BlockFace.East, ushort.MaxValue, byte.MaxValue, 3, 0xffffff, 255);
            Assert.That(Marshal.SizeOf<PackedQuad>(), Is.EqualTo(12));
            Assert.That(quad.Anchor, Is.EqualTo(new int3(32)));
            Assert.That(quad.Width, Is.EqualTo(32)); Assert.That(quad.Height, Is.EqualTo(32));
            Assert.That(quad.Face, Is.EqualTo(BlockFace.East)); Assert.That(quad.Texture, Is.EqualTo(65535));
            Assert.That(quad.Tint, Is.EqualTo(255)); Assert.That(quad.Rotation, Is.EqualTo(3));
            Assert.That(quad.Material, Is.EqualTo(255)); Assert.That(quad.ChunkSlot, Is.EqualTo(0xffffff));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackedQuad(new int3(33), 1, 1, BlockFace.Up, 0, 0, 0, 0, 0));
        }

        [Test]
        public void FragmentedChunkFitsCapacityAndRecordsRepresentativeJobCosts()
        {
            using var visibilityVisited = new NativeArray<byte>(ChunkLayout.Volume, Allocator.Persistent);
            using var visibilityQueue = new NativeArray<int>(ChunkLayout.Volume, Allocator.Persistent);
            using var visibilityResult = new NativeReference<ulong>(Allocator.Persistent);
            JobHandle Visibility() => new ChunkVisibilityJob
            {
                Voxels = voxels, Attributes = attributes.AsReadOnly(), Visited = visibilityVisited,
                Queue = visibilityQueue, Result = visibilityResult
            }.Schedule();
            JobHandle Combined()
            {
                var mesh = new GreedyMesherJob { Voxels = voxels, Attributes = attributes.AsReadOnly(), Appearance = appearance.AsReadOnly(),
                    Mask = mask, Output = output, Slot = 7, Seed = 123, ChunkPosition = new int3(-1, 0, 2) }.Schedule();
                return JobHandle.CombineDependencies(mesh, Visibility());
            }

            var report = new System.Text.StringBuilder("Burst schedule + completion, warmed editor, 32-cube input, 10 samples per layout. Not GPU/frame timings.\n");
            for (int layout = 0; layout < 4; layout++)
            {
                for (int i = 0; i < voxels.Length; i++) voxels[i] = 0;
                var random = new System.Random(73);
                for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++)
                    voxels[GreedyMesherJob.Index(new int3(x, y, z))] = layout == 0 ? 1u :
                        layout == 1 ? (x == 16 ? 1u : 0u) : layout == 2 ? (uint)((x + y + z) & 1) : (uint)random.Next(2);
                Build(); Visibility().Complete(); Combined().Complete();
                if (layout == 2) Assert.That(output.Length, Is.EqualTo(ChunkLayout.Volume / 2 * 6));

                var meshTimer = System.Diagnostics.Stopwatch.StartNew();
                for (int sample = 0; sample < 10; sample++) Build();
                meshTimer.Stop();
                var visibilityTimer = System.Diagnostics.Stopwatch.StartNew();
                for (int sample = 0; sample < 10; sample++) Visibility().Complete();
                visibilityTimer.Stop();
                var combinedTimer = System.Diagnostics.Stopwatch.StartNew();
                for (int sample = 0; sample < 10; sample++) Combined().Complete();
                combinedTimer.Stop();

                report.AppendLine($"{new[] { "uniform", "plane", "checkerboard", "random" }[layout]}: " +
                    $"mesh {meshTimer.Elapsed.TotalMilliseconds / 10:F3} ms, visibility {visibilityTimer.Elapsed.TotalMilliseconds / 10:F3} ms, " +
                    $"combined {combinedTimer.Elapsed.TotalMilliseconds / 10:F3} ms, {output.Length} quads, {output.Length * PackedQuad.Stride} bytes");
            }
            System.IO.Directory.CreateDirectory(".utmp");
            System.IO.File.WriteAllText(".utmp/terrain-mesher-benchmark.txt", report.ToString());
        }

        [Test]
        public void RandomRotationIsStableAcrossEquivalentNegativeWorldCoordinates()
        {
            var appearance = new BlockFaceAppearance(1, 2, 0, true);
            var seen = new HashSet<byte>();
            for (int x = 0; x < 32; x++)
            {
                byte a = GreedyMesherJob.ResolveRotation(appearance, new int3(-1,0,0), new int3(x,0,0), BlockFace.Up, 54);
                byte b = GreedyMesherJob.ResolveRotation(appearance, int3.zero, new int3(x-32,0,0), BlockFace.Up, 54);
                Assert.That(a, Is.EqualTo(b)); seen.Add(a);
            }
            Assert.That(seen.Count, Is.EqualTo(4));
            Assert.That(GreedyMesherJob.ResolveRotation(new BlockFaceAppearance(1, 2, 0), new int3(-10), new int3(3), BlockFace.North, 54), Is.EqualTo(2));
        }

        [Test]
        public void SideBasesRemainUprightAndTriangleWindingMatchesNormal()
        {
            foreach (BlockFace face in Enum.GetValues(typeof(BlockFace)))
            {
                FaceBasis.Get(face, out var n, out var u, out var v);
                Assert.That(math.cross((float3)v, (float3)u), Is.EqualTo((float3)n));
                if (face != BlockFace.Up && face != BlockFace.Down) Assert.That(v, Is.EqualTo(new int3(0,1,0)));
            }
        }
    }
}
