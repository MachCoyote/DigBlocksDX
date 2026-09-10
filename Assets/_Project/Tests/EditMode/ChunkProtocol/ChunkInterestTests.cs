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
            //a horizontal radius of 2 admits 13 columns, not the 25 a square of the same reach would.
            Assert.That(addresses.Length, Is.EqualTo(39));
            Assert.That(interest.Count, Is.EqualTo(39));
            Assert.That(addresses[0], Is.EqualTo(anchor));
            Assert.That(new HashSet<ChunkAddress>(addresses).Count, Is.EqualTo(39));
            foreach (var address in addresses) Assert.That(interest.Contains(address), Is.True);
            Assert.That(interest.Contains(new ChunkAddress(8, anchor.Position)), Is.False);
            Assert.That(interest.Contains(new ChunkAddress(7, anchor.Position + new int3(0, 2, 0))), Is.False);
            Assert.That(interest.Contains(new ChunkAddress(7, anchor.Position + new int3(3, 0, 0))), Is.False);
            addresses[0] = default;
            Assert.That(interest.Addresses()[0], Is.EqualTo(anchor));
        }

        [Test]
        public void ExcludesColumnsOutsideTheHorizontalRadiusAtEveryHeight()
        {
            var anchor = new ChunkAddress(1, new int3(4, -2, 11));
            var interest = new ChunkInterest(1, anchor, 5, 2);
            //a corner of the enclosing square is further away than the radius the interest claims.
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(5, 0, 5))), Is.False);
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(5, 0, 0))), Is.True);
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(3, 2, 4))), Is.True);
            //the cylinder does not taper: the same column is admitted at every height it reaches.
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(5, 2, 0))), Is.True);
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(5, -2, 0))), Is.True);
            Assert.That(interest.Contains(new ChunkAddress(1, anchor.Position + new int3(5, 3, 0))), Is.False);

            //enumeration and containment must describe exactly the same set.
            var enumerated = new HashSet<ChunkAddress>(interest.Addresses());
            Assert.That(enumerated.Count, Is.EqualTo(interest.Count));
            for (int y = -3; y <= 3; y++)
            for (int z = -6; z <= 6; z++)
            for (int x = -6; x <= 6; x++)
            {
                var address = new ChunkAddress(1, anchor.Position + new int3(x, y, z));
                Assert.That(enumerated.Contains(address), Is.EqualTo(interest.Contains(address)), $"{address} disagreed.");
            }
        }

        [Test]
        public void CountMatchesEnumerationWithoutEnumerating()
        {
            for (int horizontal = 0; horizontal <= 12; horizontal++)
            for (int vertical = 0; vertical <= 2; vertical++)
                Assert.That(ChunkInterest.CountFor(horizontal, vertical),
                    Is.EqualTo(new ChunkInterest(1, new ChunkAddress(1, default), horizontal, vertical).Addresses().Length),
                    $"h={horizontal} v={vertical}");
            //the authored comparison target, stated so a shape change cannot silently move it.
            Assert.That(ChunkInterest.CountFor(12, 2), Is.EqualTo(2205));
        }

        [Test]
        public void RejectsUnboundedAndOverflowingNeighborhoodsBeforeEnumeration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(0, default, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, default, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, default, 0, int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, default, ChunkInterest.MaximumRadius + 1, 0));
            //inside the radius bound but past the chunk ceiling, which is the case that has to be
            //rejected before anything allocates per chunk.
            Assert.Throws<ArgumentException>(() => new ChunkInterest(1, default, 200, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, new ChunkAddress(1, new int3(int.MaxValue, 0, 0)), 1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkInterest(1, new ChunkAddress(1, new int3(0, int.MinValue, 0)), 0, 1));
            var edge = new ChunkInterest(1, new ChunkAddress(1, new int3(int.MinValue)), 0, 0);
            Assert.That(edge.Count, Is.EqualTo(1));
            Assert.That(edge.Contains(new ChunkAddress(1, new int3(int.MaxValue))), Is.False);
        }

        [Test]
        public void EnumeratesChunksRadiallyFromTheAnchor()
        {
            var anchor = new ChunkAddress(1, new int3(-4, 7, 12));
            var addresses = new ChunkInterest(1, anchor, 2, 1).Addresses();
            long previousDistance = -1;
            foreach (var address in addresses)
            {
                long dx = (long)address.Position.x - anchor.Position.x;
                long dy = (long)address.Position.y - anchor.Position.y;
                long dz = (long)address.Position.z - anchor.Position.z;
                long distance = dx * dx + dy * dy + dz * dz;
                Assert.That(distance, Is.GreaterThanOrEqualTo(previousDistance),
                    $"{address} was scheduled after a farther chunk.");
                previousDistance = distance;
            }
        }
    }
}
