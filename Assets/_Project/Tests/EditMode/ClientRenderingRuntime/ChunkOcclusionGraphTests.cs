using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class ChunkOcclusionGraphTests
    {
        private ChunkOcclusionGraph graph;

        [SetUp]
        public void SetUp() => graph = new ChunkOcclusionGraph(32);

        private void Add(int slot, int x, int y = 0, int z = 0, ulong connectivity = ChunkFaceConnectivity.ValidBits, bool renderable = true) =>
            graph.SetNode(slot, new int3(x, y, z), new ChunkFaceConnectivity(connectivity), renderable);

        private static ulong Connect(BlockFace incoming, BlockFace outgoing) =>
            1ul << ((int)incoming * ChunkFaceConnectivity.FaceCount + (int)outgoing);

        [Test]
        public void OpenChainIsVisibleFromEndToEnd()
        {
            Add(0, 0); Add(1, 1); Add(2, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(0), Is.True);
            Assert.That(graph.IsCameraVisible(1), Is.True);
            Assert.That(graph.IsCameraVisible(2), Is.True);
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(3));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void FullyOpaqueChunkStopsTraversalAfterRemainingVisibleItself()
        {
            Add(0, 0); Add(1, 1, connectivity: 0); Add(2, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(0), Is.True);
            Assert.That(graph.IsCameraVisible(1), Is.True);
            Assert.That(graph.IsCameraVisible(2), Is.False);
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.GraphCulledCount, Is.EqualTo(1));
        }

        [Test]
        public void AlternateRouteReachesChunkBehindBarrier()
        {
            Add(0, 0, 0, 0);
            Add(1, 1, 0, 0, 0);
            Add(2, 0, 0, 1); Add(3, 1, 0, 1); Add(4, 2, 0, 1); Add(5, 2, 0, 0);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(5), Is.True);
        }

        [Test]
        public void CyclesTerminateAndVisitEachRenderableNode()
        {
            Add(0, 0, 0, 0); Add(1, 1, 0, 0); Add(2, 1, 0, 1); Add(3, 0, 0, 1);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(4));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void NewIncomingFaceRequeuesPreviouslyProcessedNode()
        {
            Add(0, 0, 0, 0);
            ulong central = Connect(BlockFace.East, BlockFace.North);
            Add(1, 0, 0, 1, central);
            Add(2, 1, 0, 0); Add(3, 1, 0, 1);
            Add(4, 0, 0, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.IsCameraVisible(4), Is.True);
        }

        [Test]
        public void CameraChunkUsesMathematicalFloorAcrossZero()
        {
            Add(0, -1); Add(1, 0, connectivity: 0);
            graph.SetReady(true);

            graph.Cull(new float3(-0.01f, 1, 1), true);

            Assert.That(graph.IsCameraVisible(0), Is.True);
            Assert.That(graph.IsCameraVisible(1), Is.True);

            graph.Cull(new float3(0.01f, 1, 1), true);

            Assert.That(graph.IsCameraVisible(1), Is.True);
            Assert.That(graph.IsCameraVisible(0), Is.False);
        }

        [TestCase(false, true, TestName = "DisabledCullingFailsOpen")]
        [TestCase(true, false, TestName = "NotReadyCullingFailsOpen")]
        public void UnavailableCullingMakesEveryRenderableChunkVisible(bool enabled, bool ready)
        {
            Add(0, 0, connectivity: 0); Add(1, 2);
            graph.SetReady(ready);

            graph.Cull(new float3(1, 1, 1), enabled);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(graph.RenderableCount));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void OriginOutsideGraphFailsOpen()
        {
            Add(0, 0, connectivity: 0); Add(1, 2);
            graph.SetReady(true);

            graph.Cull(new float3(32 * 20, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void DuplicatePositionsMakeInconsistentGraphFailOpen()
        {
            Add(0, 0, connectivity: 0); Add(1, 0); Add(2, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.CameraVisibleCount, Is.EqualTo(3));
            Assert.That(graph.GraphCulledCount, Is.Zero);
        }

        [Test]
        public void EmptyNodesRemainInGraphButNotRenderableCounters()
        {
            Add(0, 0, renderable: false); Add(1, 1); Add(2, 2);
            graph.SetReady(true);

            graph.Cull(new float3(1, 1, 1), true);

            Assert.That(graph.ResidentCount, Is.EqualTo(3));
            Assert.That(graph.RenderableCount, Is.EqualTo(2));
            Assert.That(graph.CameraVisibleCount, Is.EqualTo(2));
            Assert.That(graph.IsCameraVisible(0), Is.True);
        }

        [Test]
        public void ClearRemovesNodesAndFailsOpenWithoutAnOrigin()
        {
            Add(0, 0); Add(1, 1);

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
            Add(0, 0); Add(1, 1); Add(2, 2);
            graph.SetReady(true);
            graph.Cull(new float3(1, 1, 1), true);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) graph.Cull(new float3(1, 1, 1), true);
            long after = System.GC.GetAllocatedBytesForCurrentThread();

            Assert.That(after, Is.EqualTo(before));
        }
    }
}
