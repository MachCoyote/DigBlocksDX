using DigBlocks.Simulation;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Simulation.Tests
{
    public sealed class WorldPositionTests
    {
        //the whole reason this type exists. A float at fifty million has a spacing of about six
        //blocks, so a small step vanishes entirely; the split representation keeps it.
        [Test]
        public void SmallStepsSurviveFiftyMillionBlocksFromTheOrigin()
        {
            const float farAway = 50_000_000f;
            Assert.That(farAway + 0.01f, Is.EqualTo(farAway), "a raw float should be unable to represent this step at all");

            var position = SectorGrid.FromBlocks(new int3(50_000_000, 96, -50_000_000));
            var moved = SectorGrid.Offset(position, new float3(0.01f, 0f, -0.01f));

            double3 before = SectorGrid.ToBlocks(position), after = SectorGrid.ToBlocks(moved);
            Assert.That(after.x - before.x, Is.EqualTo(0.01).Within(1e-3));
            Assert.That(after.z - before.z, Is.EqualTo(-0.01).Within(1e-3));
            Assert.That(after.y, Is.EqualTo(96d).Within(1e-6));
        }

        [Test]
        public void NormalizeCarriesWholeSectorsOutOfTheOffset()
        {
            var carried = SectorGrid.Normalize(new WorldPosition(new int3(3, 0, 0), new float3(SectorGrid.SectorEdge + 5f, 0f, 0f)));
            Assert.That(carried.Sector.x, Is.EqualTo(4));
            Assert.That(carried.Local.x, Is.EqualTo(5f).Within(1e-3f));
        }

        [Test]
        public void NormalizeBringsNegativeOffsetsBackIntoRange()
        {
            var carried = SectorGrid.Normalize(new WorldPosition(new int3(0, 0, 0), new float3(-1f, 0f, 0f)));
            Assert.That(carried.Sector.x, Is.EqualTo(-1));
            Assert.That(carried.Local.x, Is.EqualTo(SectorGrid.SectorEdge - 1f).Within(1e-3f));
            Assert.That(carried.Local.x, Is.LessThan(SectorGrid.SectorEdge));
        }

        //a rounding step must never leave an offset sitting exactly on the next sector's origin,
        //because that is a position with two spellings.
        [Test]
        public void NormalizeNeverLeavesAnOffsetOnTheSectorEdge()
        {
            var carried = SectorGrid.Normalize(new WorldPosition(int3.zero, new float3(SectorGrid.SectorEdge, 0f, 0f)));
            Assert.That(carried.Sector.x, Is.EqualTo(1));
            Assert.That(carried.Local.x, Is.LessThan(SectorGrid.SectorEdge));
            Assert.That(SectorGrid.MaxLocal, Is.LessThan((float)SectorGrid.SectorEdge));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(-1)]
        [TestCase(SectorGrid.SectorEdge)]
        [TestCase(-SectorGrid.SectorEdge)]
        [TestCase(-SectorGrid.SectorEdge - 1)]
        [TestCase(123_456_789)]
        [TestCase(-123_456_789)]
        public void BlockCoordinatesRoundTripExactly(int block)
        {
            var position = SectorGrid.FromBlocks(new int3(block, block, block));
            Assert.That(position.Local.x, Is.GreaterThanOrEqualTo(0f));
            Assert.That(position.Local.x, Is.LessThan(SectorGrid.SectorEdge));
            Assert.That(SectorGrid.ToBlocks(position).x, Is.EqualTo((double)block).Within(1e-3));
        }

        //sector size is a whole number of chunks on purpose, so no chunk ever straddles a sector.
        [Test]
        public void SectorBoundariesFallOnChunkBoundaries()
        {
            Assert.That(SectorGrid.SectorEdge % ChunkLayout.Edge, Is.Zero);
            Assert.That(SectorGrid.ChunksPerSector, Is.EqualTo(SectorGrid.SectorEdge / ChunkLayout.Edge));
        }

        [TestCase(0, 0)]
        [TestCase(31, 0)]
        [TestCase(32, 1)]
        [TestCase(-1, -1)]
        [TestCase(-32, -1)]
        [TestCase(-33, -2)]
        [TestCase(SectorGrid.SectorEdge, SectorGrid.ChunksPerSector)]
        public void ChunkAddressesAgreeWithTheChunkGrid(int block, int expectedChunk)
        {
            var address = SectorGrid.ChunkOf(1, SectorGrid.FromBlocks(new int3(block, block, block)));
            Assert.That(address.World, Is.EqualTo(1u));
            Assert.That(address.Position.x, Is.EqualTo(expectedChunk));
            //the authority on the chunk grid is ChunkLayout; this must not drift from it.
            Assert.That(address.Position.x, Is.EqualTo(ChunkLayout.ChunkOf(new int3(block, block, block)).x));
        }

        [Test]
        public void SeparationIsMeasuredAcrossASectorBoundary()
        {
            var before = SectorGrid.FromBlocks(new int3(SectorGrid.SectorEdge - 1, 0, 0));
            var after = SectorGrid.FromBlocks(new int3(SectorGrid.SectorEdge + 1, 0, 0));
            Assert.That(before.Sector.x, Is.Not.EqualTo(after.Sector.x), "the two sides should be in different sectors");
            Assert.That(SectorGrid.Delta(before, after).x, Is.EqualTo(2f).Within(1e-3f));
            Assert.That(SectorGrid.Delta(after, before).x, Is.EqualTo(-2f).Within(1e-3f));
        }

        //what physics and rendering actually consume: a small number, however far out the frame is.
        [Test]
        public void FrameLocalOffsetsStaySmallFarFromTheOrigin()
        {
            var position = SectorGrid.FromBlocks(new int3(50_000_000, 64, 50_000_000));
            float3 local = SectorGrid.ToFrame(position, position.Sector);
            Assert.That(math.length(local), Is.LessThan(SectorGrid.SectorEdge * 2f));
        }
    }
}
