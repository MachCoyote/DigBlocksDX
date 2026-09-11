using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Tests
{
    public sealed class ChunkSlotGridTests
    {
        [Test]
        public void CapacityIsTheBoxThatCircumscribesTheStreamedCylinder()
        {
            var grid = new ChunkSlotGrid(12, 4);
            Assert.That(grid.Width, Is.EqualTo(25)); Assert.That(grid.Height, Is.EqualTo(9));
            Assert.That(grid.Capacity, Is.EqualTo(25 * 25 * 9));
            //the cylinder this box holds is smaller than the box; the corner slots stay empty and that
            //is the price of addressing by position rather than allocating.
            Assert.That(grid.Capacity, Is.GreaterThan(441 * 9));
        }

        //the whole point of the scheme: no two chunks that can be resident together share a slot, and
        //nothing has to know where the volume is centred for that to hold.
        [Test]
        public void EveryChunkInAnyPlacementOfTheVolumeGetsItsOwnSlot()
        {
            var grid = new ChunkSlotGrid(3, 2);
            foreach (var centre in new[] { new int3(0, 0, 0), new int3(1, 0, 0), new int3(-40, 17, 96), new int3(-1, -1, -1) })
            {
                var taken = new Dictionary<int, int3>();
                for (int y = -2; y <= 2; y++)
                for (int z = -3; z <= 3; z++)
                for (int x = -3; x <= 3; x++)
                {
                    if (x * x + z * z > 9) continue;
                    var position = centre + new int3(x, y, z);
                    int slot = grid.SlotOf(position);
                    Assert.That(slot, Is.InRange(0, grid.Capacity - 1));
                    Assert.That(taken.ContainsKey(slot), Is.False,
                        $"{position} collided with {(taken.TryGetValue(slot, out var other) ? other.ToString() : "?")} at centre {centre}.");
                    taken.Add(slot, position);
                }
            }
        }

        //moving the anchor by one must hand the arriving chunk exactly the slot the departing one left,
        //which is what makes an eviction and its replacement a single overwrite.
        [Test]
        public void SteppingOneChunkReusesTheDepartingSlotForTheArrivingChunk()
        {
            var grid = new ChunkSlotGrid(5, 1);
            var departing = new int3(-5, 0, 0);
            var arriving = new int3(6, 0, 0);
            Assert.That(grid.SlotOf(arriving), Is.EqualTo(grid.SlotOf(departing)));
            Assert.That(grid.SlotOf(new int3(0, -1, 0)), Is.EqualTo(grid.SlotOf(new int3(0, 2, 0))));
        }

        //a plain remainder would fold -1 onto the same slot as 0 and alias two live chunks together.
        [Test]
        public void NegativeCoordinatesWrapByFloorRatherThanTowardZero()
        {
            var grid = new ChunkSlotGrid(1, 1);
            Assert.That(grid.SlotOf(new int3(-1, 0, 0)), Is.Not.EqualTo(grid.SlotOf(new int3(0, 0, 0))));
            Assert.That(grid.SlotOf(new int3(-1, 0, 0)), Is.EqualTo(grid.SlotOf(new int3(2, 0, 0))));
            Assert.That(grid.SlotOf(new int3(0, -1, 0)), Is.EqualTo(grid.SlotOf(new int3(0, 2, 0))));
            Assert.That(grid.SlotOf(new int3(0, 0, -3)), Is.EqualTo(grid.SlotOf(new int3(0, 0, 0))));
        }

        [Test]
        public void SlotsStayInRangeAcrossTheAddressExtremes()
        {
            var grid = new ChunkSlotGrid(7, 3);
            foreach (var position in new[]
            {
                new int3(int.MinValue, int.MinValue, int.MinValue),
                new int3(int.MaxValue, int.MaxValue, int.MaxValue),
                new int3(int.MinValue, 0, int.MaxValue)
            })
                Assert.That(grid.SlotOf(position), Is.InRange(0, grid.Capacity - 1));
        }

        [Test]
        public void ARadiusOfZeroIsASingleSlotAndEveryChunkLandsOnIt()
        {
            var grid = new ChunkSlotGrid(0, 0);
            Assert.That(grid.Capacity, Is.EqualTo(1));
            Assert.That(grid.SlotOf(new int3(9, -4, 130)), Is.Zero);
        }

        [Test]
        public void NegativeAndOversizedRadiiAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkSlotGrid(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkSlotGrid(0, -1));
            //a radius this large would need more slots than the cap allows, and the check has to run on
            //widened arithmetic or the capacity it is guarding would overflow before being compared.
            Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkSlotGrid(4096, 4096));
        }
    }
}
