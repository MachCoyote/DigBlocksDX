using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.ChunkProtocol.Tests
{
    public sealed class ChunkInterestTests
    {
        [Test]
        public void EnumeratesWorldQualifiedNegativeNeighborhoodWithIndependentVerticalRadius()
        {
            var anchor = new ChunkAddress(7, new int3(-9, -3, 2));
            var interest = new ChunkInterest(4, anchor, 2, 1);
            var addresses = interest.Addresses();
            Assert.That(addresses.Length, Is.EqualTo(75));
            Assert.That(addresses[0], Is.EqualTo(anchor));
            Assert.That(new HashSet<ChunkAddress>(addresses).Count, Is.EqualTo(75));
            foreach (var address in addresses) Assert.That(interest.Contains(address), Is.True);
            Assert.That(interest.Contains(new ChunkAddress(8, anchor.Position)), Is.False);
            Assert.That(interest.Contains(new ChunkAddress(7, anchor.Position + new int3(0, 2, 0))), Is.False);
            Assert.That(interest.Contains(new ChunkAddress(7, anchor.Position + new int3(3, 0, 0))), Is.False);
            addresses[0] = default;
            Assert.That(interest.Addresses()[0], Is.EqualTo(anchor));
        }

        [Test]
        public void RejectsUnboundedAndOverflowingNeighborhoodsBeforeEnumeration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(0, default, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, default, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, default, 0, int.MaxValue));
            Assert.Throws<ArgumentException>(() => new ChunkInterest(1, default, 4, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, new ChunkAddress(1, new int3(int.MaxValue, 0, 0)), 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, new ChunkAddress(1, new int3(0, int.MinValue, 0)), 0, 1));
            var edge = new ChunkInterest(1, new ChunkAddress(1, new int3(int.MinValue)), 0, 0);
            Assert.That(edge.Count, Is.EqualTo(1));
            Assert.That(edge.Contains(new ChunkAddress(1, new int3(int.MaxValue))), Is.False);
        }
    }
}
