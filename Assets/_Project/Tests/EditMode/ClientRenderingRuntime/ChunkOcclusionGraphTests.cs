using DigBlocks.Voxels;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class ChunkOcclusionGraphTests
    {
        //the authored view distance, so the box these tests traverse is the one that ships.
        private static readonly ChunkSlotGrid Grid = new(12, 4);
        private ChunkOcclusionGraph graph;

        [SetUp]
        public void SetUp() => graph = new ChunkOcclusionGraph(Grid);

        private void Add(int x, int y = 0, int z = 0, ulong connectivity = ChunkFaceConnectivity.ValidBits, bool renderable = true) =>
            graph.SetNode(new int3(x, y, z), new ChunkFaceConnectivity(connectivity), renderable);

        private static int Slot(int x, int y = 0, int z = 0) => Grid.SlotOf(new int3(x, y, z));

        private static ulong Connect(BlockFace incoming, BlockFace outgoing) =>
            1ul << ((int)incoming * ChunkFaceConnectivity.FaceCount + (int)outgoing);

        [Test]
        public void OpenChainIsVisibleFromEndToEnd()
        {
            Add(0); Add(1); Add(2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(1)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(2)), Is.True);
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(3));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void FullyOpaqueChunkStopsTraversalAfterRemainingVisibleItself()
        {
            Add(0); Add(1, connectivity: 0); Add(2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(1)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(2)), Is.False);
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.GraphCulledCount, Is.EqualTo(1));
        }

        [Test]
        public void AlternateRouteReachesChunkBehindBarrier()
        {
            Add(0, 0, 0);
            Add(1, 0, 0, 0);
            Add(0, 0, 1); Add(1, 0, 1); Add(2, 0, 1); Add(2, 0, 0);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(2, 0, 0)), Is.True);
        }

        [Test]
        public void CyclesTerminateAndVisitEachRenderableNode()
        {
            Add(0, 0, 0); Add(1, 0, 0); Add(1, 0, 1); Add(0, 0, 1);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(4));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void NewIncomingFaceRequeuesPreviouslyProcessedNode()
        {
            Add(0, 0, 0);
            ulong central = Connect(BlockFace.East, BlockFace.North);
            Add(0, 0, 1, central);
            Add(1, 0, 0); Add(1, 0, 1);
            Add(0, 0, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(0, 0, 2)), Is.True);
        }

        [Test]
        public void CameraChunkUsesMathematicalFloorAcrossZero()
        {
            Add(-1); Add(0, connectivity: 0);
            graph.SetReady(true);

            graph.Cull(new float3(-0.01f, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(-1)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);

            graph.Cull(new float3(0.01f, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(-1)), Is.False);
        }

        //the slot of a chunk's neighbour is a fixed permutation, but the chunk sitting in that slot may
        //be the one that wrapped onto it from the opposite face of the box rather than a true neighbour.
        //Traversal must not step across that seam, or the far side of the world lights up as visible.
        [Test]
        public void TraversalDoesNotStepAcrossTheWrapSeamToADistantChunk()
        {
            Add(0);
            Add(Grid.Width - 1);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);
            Assert.That(graph.IsCameraVisible(Slot(Grid.Width - 1)), Is.False,
                "A chunk a whole box-width away is not a neighbour.");
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(1));
            Assert.That(graph.GraphCulledCount, Is.EqualTo(1));
        }

        [TestCase(false, true, TestName = "DisabledCullingFailsOpen")]
        [TestCase(true, false, TestName = "NotReadyCullingFailsOpen")]
        public void UnavailableCullingMakesEveryRenderableChunkVisible(bool enabled, bool ready)
        {
            Add(0, connectivity: 0); Add(2);
            graph.SetReady(ready);

            graph.Cull(new float3(1, 1, 1), enabled);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(graph.RenderableCount));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void OriginOutsideGraphFailsOpen()
        {
            Add(0, connectivity: 0); Add(2);
            graph.SetReady(true);

            graph.Cull(new float3(ChunkLayout.Edge * 20, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        //two chunks a whole box-width apart cannot both be resident. That means the streamed volume
        //outgrew the grid, so the graph refuses the second rather than overwriting the first, and stops
        //culling against a world it knows is wrong.
        [Test]
        public void AChunkThatWouldAliasAnotherIsRejectedAndMakesTheGraphFailOpen()
        {
            Add(0, connectivity: 0);
            Assert.That(graph.SetNode(new int3(Grid.Width, 0, 0), new ChunkFaceConnectivity(0), true), Is.False);
            Assert.That(graph.ResidentCount, Is.EqualTo(1), "The resident chunk keeps its slot.");
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(graph.RenderableCount));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void RepublishingTheSameChunkUpdatesItWithoutDoubleCounting()
        {
            Add(0, renderable: false);
            Assert.That(graph.ResidentCount, Is.EqualTo(1));
            Assert.That(graph.RenderableCount, Is.Zero);

            Add(0);

            Assert.That(graph.ResidentCount, Is.EqualTo(1));
            Assert.That(graph.RenderableCount, Is.EqualTo(1));
        }

        [Test]
        public void RemovingAChunkFreesItsSlotForTheChunkThatWrapsOntoIt()
        {
            Add(0); Add(1);
            graph.RemoveNode(new int3(0, 0, 0));

            Assert.That(graph.ResidentCount, Is.EqualTo(1));
            Assert.That(graph.SetNode(new int3(Grid.Width, 0, 0), new ChunkFaceConnectivity(ChunkFaceConnectivity.ValidBits), true), Is.True);
            Assert.That(graph.ResidentCount, Is.EqualTo(2));
        }

        //a removal that names a chunk which is not the one in that slot must leave the resident alone.
        [Test]
        public void RemovingAChunkThatIsNotResidentLeavesTheSlotHolderInPlace()
        {
            Add(0);
            graph.RemoveNode(new int3(Grid.Width, 0, 0));

            Assert.That(graph.ResidentCount, Is.EqualTo(1));
            Assert.That(graph.RenderableCount, Is.EqualTo(1));
        }

        [Test]
        public void EmptyNodesRemainInGraphButNotRenderableCounters()
        {
            Add(0, renderable: false); Add(1); Add(2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.ResidentCount, Is.EqualTo(3));
            Assert.That(graph.RenderableCount, Is.EqualTo(2));
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.IsCameraVisible(Slot(0)), Is.True);
        }

        [Test]
        public void ClearRemovesNodesAndFailsOpenWithoutAnOrigin()
        {
            Add(0); Add(1);

            graph.Clear();
            graph.SetReady(true);
            graph.Cull(float3.zero, true);

            Assert.That(graph.ResidentCount, Is.Zero);
            Assert.That(graph.CameraVisibleCount, Is.Zero);
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void RepeatedTraversalDoesNotAllocateManagedMemory()
        {
            Add(0); Add(1); Add(2);
            graph.SetReady(true);
            graph.Cull(new float3(1, 1, 1), true);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) graph.Cull(new float3(1, 1, 1), true);
            long after = System.GC.GetAllocatedBytesForCurrentThread();

            Assert.That(after, Is.EqualTo(before));
        }
    }
}
