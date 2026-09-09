using DigBlocks.Voxels.Appearance;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Meshing.Tests
{
    public sealed class ChunkVisibilityTests
    {
        private NativeArray<uint> voxels;
        private NativeArray<BlockAttributes> attributes;
        private NativeArray<byte> visited;
        private NativeArray<int> queue;
        private NativeReference<ulong> result;

        [SetUp]
        public void SetUp()
        {
            voxels = new NativeArray<uint>(GreedyMesherJob.PaddedVolume, Allocator.Persistent);
            attributes = new NativeArray<BlockAttributes>(4, Allocator.Persistent);
            attributes[0] = BlockAttributes.Air;
            attributes[1] = BlockAttributes.Default.WithFlags(BlockFlags.FullCube);
            attributes[2] = BlockAttributes.Default.WithFlags(BlockFlags.Opaque);
            attributes[3] = BlockAttributes.Default;
            visited = new NativeArray<byte>(ChunkLayout.Volume, Allocator.Persistent);
            queue = new NativeArray<int>(ChunkLayout.Volume, Allocator.Persistent);
            result = new NativeReference<ulong>(Allocator.Persistent);
        }

        [TearDown]
        public void TearDown()
        {
            result.Dispose();
            queue.Dispose();
            visited.Dispose();
            attributes.Dispose();
            voxels.Dispose();
        }

        private ChunkFaceConnectivity Build()
        {
            new ChunkVisibilityJob
            {
                Voxels = voxels,
                Attributes = attributes.AsReadOnly(),
                Visited = visited,
                Queue = queue,
                Result = result
            }.Schedule().Complete();
            return new ChunkFaceConnectivity(result.Value);
        }

        private void FillCenter(uint state)
        {
            for (int y = 0; y < ChunkLayout.Edge; y++)
            for (int z = 0; z < ChunkLayout.Edge; z++)
            for (int x = 0; x < ChunkLayout.Edge; x++)
                voxels[GreedyMesherJob.Index(new int3(x, y, z))] = state;
        }

        [Test]
        public void EmptyChunkConnectsEveryFacePair()
        {
            var connectivity = Build();

            foreach (BlockFace incoming in System.Enum.GetValues(typeof(BlockFace)))
            foreach (BlockFace outgoing in System.Enum.GetValues(typeof(BlockFace)))
                Assert.That(connectivity.Connects(incoming, outgoing), Is.True, $"{incoming} to {outgoing}");
        }

        [Test]
        public void FullyOpaqueChunkConnectsNoFaces()
        {
            FillCenter(3);
            var connectivity = Build();

            Assert.That(connectivity.Bits, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SealedPlaneDisconnectsItsOpposingFaces(int axis)
        {
            for (int y = 0; y < ChunkLayout.Edge; y++)
            for (int z = 0; z < ChunkLayout.Edge; z++)
            for (int x = 0; x < ChunkLayout.Edge; x++)
                if (new int3(x, y, z)[axis] == ChunkLayout.Edge / 2)
                    voxels[GreedyMesherJob.Index(new int3(x, y, z))] = 3;

            var connectivity = Build();
            BlockFace negative = axis == 0 ? BlockFace.West : axis == 1 ? BlockFace.Down : BlockFace.South;
            BlockFace positive = ChunkFaceConnectivity.Opposite(negative);

            Assert.That(connectivity.Connects(negative, positive), Is.False);
            Assert.That(connectivity.Connects(positive, negative), Is.False);
        }

        [Test]
        public void OpeningInSealedPlaneReconnectsBothSides()
        {
            for (int y = 0; y < ChunkLayout.Edge; y++)
            for (int z = 0; z < ChunkLayout.Edge; z++)
                voxels[GreedyMesherJob.Index(new int3(ChunkLayout.Edge / 2, y, z))] = 3;
            voxels[GreedyMesherJob.Index(new int3(ChunkLayout.Edge / 2, 15, 15))] = 0;

            Assert.That(Build().Connects(BlockFace.West, BlockFace.East), Is.True);
        }

        [Test]
        public void DisconnectedBoundaryComponentsDoNotConnectToEachOther()
        {
            FillCenter(3);
            voxels[GreedyMesherJob.Index(new int3(0, 15, 15))] = 0;
            voxels[GreedyMesherJob.Index(new int3(31, 15, 15))] = 0;

            var connectivity = Build();

            Assert.That(connectivity.Connects(BlockFace.West, BlockFace.West), Is.True);
            Assert.That(connectivity.Connects(BlockFace.East, BlockFace.East), Is.True);
            Assert.That(connectivity.Connects(BlockFace.West, BlockFace.East), Is.False);
        }

        [Test]
        public void BoundaryDetectionCoversAllSixFaces()
        {
            FillCenter(3);
            var cells = new[]
            {
                new int3(15, 0, 15), new int3(15, 31, 15), new int3(15, 15, 31),
                new int3(15, 15, 0), new int3(0, 15, 15), new int3(31, 15, 15)
            };
            for (int i = 0; i < cells.Length; i++)
                voxels[GreedyMesherJob.Index(cells[i])] = 0;

            var connectivity = Build();

            foreach (BlockFace face in System.Enum.GetValues(typeof(BlockFace)))
                Assert.That(connectivity.Connects(face, face), Is.True, face.ToString());
        }

        [TestCase(0, true, TestName = "AirRemainsPassable")]
        [TestCase(1, true, TestName = "NonOpaqueFullCubeRemainsPassable")]
        [TestCase(2, true, TestName = "OpaquePartialBlockRemainsPassable")]
        [TestCase(3, false, TestName = "OpaqueFullCubeBlocksVisibility")]
        public void OnlyOpaqueFullCubesOcclude(int state, bool expectedConnected)
        {
            FillCenter((uint)state);

            Assert.That(Build().Connects(BlockFace.West, BlockFace.East), Is.EqualTo(expectedConnected));
        }

        [Test]
        public void OppositeFacesMatchTheMeshingDirectionPairs()
        {
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.Down), Is.EqualTo(BlockFace.Up));
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.Up), Is.EqualTo(BlockFace.Down));
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.North), Is.EqualTo(BlockFace.South));
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.South), Is.EqualTo(BlockFace.North));
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.West), Is.EqualTo(BlockFace.East));
            Assert.That(ChunkFaceConnectivity.Opposite(BlockFace.East), Is.EqualTo(BlockFace.West));
        }
    }
}
