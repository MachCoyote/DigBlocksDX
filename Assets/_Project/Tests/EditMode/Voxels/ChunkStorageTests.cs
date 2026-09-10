using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Tests
{
    public class ChunkStorageTests
    {
        [Test]
        public void NegativeCoordinatesUseFloorDivision()
        {
            Assert.That(ChunkLayout.ChunkOf(new int3(-1, -ChunkLayout.Edge, -ChunkLayout.Edge - 1)),
                Is.EqualTo(new int3(-1, -1, -2)));
            Assert.That(ChunkLayout.LocalOf(new int3(-1, -ChunkLayout.Edge, -ChunkLayout.Edge - 1)),
                Is.EqualTo(new int3(ChunkLayout.Edge - 1, 0, ChunkLayout.Edge - 1)));
        }

        [Test]
        public void IndexRoundTripsEveryCell()
        {
            for (int i = 0; i < ChunkLayout.Volume; i++)
                Assert.That(ChunkLayout.Index(ChunkLayout.LocalOfIndex(i)), Is.EqualTo(i));
        }

        [Test]
        public void EditingOneCellPreservesOthersAndPromotesPalette()
        {
            var channel = new PaletteChannel(90000);
            try
            {
                channel.Set(7, 80000);
                Assert.That(channel.Get(0), Is.EqualTo(90000u));
                Assert.That(channel.Get(7), Is.EqualTo(80000u));
                //two values need one bit each; the channel only widens when the palette outgrows the width.
                Assert.That(channel.Storage, Is.EqualTo(ChannelStorage.Indirect));
                Assert.That(channel.BitsPerEntry, Is.EqualTo(1));
                for (int i = 0; i < 300; i++) channel.Set(i, (uint)i);
                Assert.That(channel.Storage, Is.EqualTo(ChannelStorage.Indirect));
                Assert.That(channel.BitsPerEntry, Is.EqualTo(PaletteChannel.BitsFor(302)));
                for (int i = 0; i < 300; i++) Assert.That(channel.Get(i), Is.EqualTo((uint)i));
                Assert.That(channel.Get(301), Is.EqualTo(90000u));
            }
            finally { channel.Dispose(); }
        }

        [Test]
        public void DirectFallbackPreservesAllCellsAndFullWidthStateIds()
        {
            var channel = new PaletteChannel(uint.MaxValue);
            try
            {
                //one past what an indirect palette is allowed to hold, so the channel carries values directly.
                for (int i = 0; i < PaletteChannel.MaxPaletteEntries; i++) channel.Set(i, (uint)i);
                Assert.That(channel.Storage, Is.EqualTo(ChannelStorage.Direct));
                Assert.That(channel.BitsPerEntry, Is.EqualTo(PaletteChannel.DirectBits));
                for (int i = 0; i < ChunkLayout.Volume; i++)
                    Assert.That(channel.Get(i), Is.EqualTo(i < PaletteChannel.MaxPaletteEntries ? (uint)i : uint.MaxValue));
                channel.Set(1, 0xABCDEF12);
                Assert.That(channel.AsReadOnly().Get(1), Is.EqualTo(0xABCDEF12));
            }
            finally { channel.Dispose(); }
        }

        [Test]
        public void InvalidCellIndicesAndCoordinatesAreRejected()
        {
            var channel = new PaletteChannel(0);
            try
            {
                foreach (int index in new[] { -1, ChunkLayout.Volume, int.MaxValue })
                {
                    Assert.Throws<System.ArgumentOutOfRangeException>(() => channel.Get(index));
                    Assert.Throws<System.ArgumentOutOfRangeException>(() => channel.Set(index, 1));
                    Assert.Throws<System.ArgumentOutOfRangeException>(() => channel.AsReadOnly().Get(index));
                    Assert.Throws<System.ArgumentOutOfRangeException>(() => ChunkLayout.LocalOfIndex(index));
                }
                Assert.Throws<System.ArgumentOutOfRangeException>(() => ChunkLayout.Index(new int3(-1, 0, 0)));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => ChunkLayout.Index(new int3(0, ChunkLayout.Edge, 0)));
            }
            finally { channel.Dispose(); }
        }
    }
}
