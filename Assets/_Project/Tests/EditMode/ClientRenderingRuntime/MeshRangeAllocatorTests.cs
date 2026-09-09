using NUnit.Framework;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class MeshRangeAllocatorTests
    {
        [Test]
        public void RetiredGeometryCannotBeReusedUntilEveryFrameReleasesIt()
        {
            var allocator = new MeshRangeAllocator(100);
            var old = allocator.Allocate(80);
            allocator.Retain(old); allocator.Retain(old);
            allocator.Retire(old);
            Assert.That(allocator.Allocate(30), Is.Null);
            allocator.Release(old);
            Assert.That(allocator.Allocate(30), Is.Null);
            allocator.Release(old);
            var next = allocator.Allocate(100);
            Assert.That(next.Start, Is.Zero); Assert.That(next.Count, Is.EqualTo(100));
        }

        [Test]
        public void OutOfOrderFreeCoalescesBothNeighbors()
        {
            var allocator = new MeshRangeAllocator(100);
            var a = allocator.Allocate(20); var b = allocator.Allocate(30); var c = allocator.Allocate(50);
            allocator.Retire(a); allocator.Retire(c);
            Assert.That(allocator.Allocate(60), Is.Null);
            allocator.Retire(b);
            Assert.That(allocator.Allocated, Is.Zero);
            Assert.That(allocator.Allocate(100).Start, Is.Zero);
        }
    }
}
