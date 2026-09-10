using NUnit.Framework;

namespace DigBlocks.Voxels.Tests
{
    public sealed class PaletteChannelViewTests
    {
        //Meshing hands several parallel jobs a view of the same chunk at once, because neighbouring
        //chunks share sources. Deriving a view from the backing list each time invalidates the last
        //one handed out, so the views have to stay usable alongside each other.
        [Test]
        public void ViewsStayUsableWhileOtherReadersHoldTheirOwn()
        {
            var values = new uint[ChunkLayout.Volume];
            for (int i = 0; i < values.Length; i++) values[i] = (uint)(i % 5);
            var channel = PaletteChannel.FromValues(values);
            try
            {
                var first = channel.AsReadOnly();
                var second = channel.AsReadOnly();
                var third = channel.AsReadOnly();
                for (int i = 0; i < values.Length; i += 331)
                {
                    Assert.That(first.Get(i), Is.EqualTo(values[i]), $"first reader, cell {i}");
                    Assert.That(second.Get(i), Is.EqualTo(values[i]), $"second reader, cell {i}");
                    Assert.That(third.Get(i), Is.EqualTo(values[i]), $"third reader, cell {i}");
                }
            }
            finally { channel.Dispose(); }
        }

        //A channel with no cells to store still has to hand out real arrays: a job cannot be given a
        //view backed by nothing, even one it never reads.
        [Test]
        public void EveryStorageClassExposesCreatedArrays()
        {
            void Check(PaletteChannel channel, ChannelStorage expected, string label)
            {
                try
                {
                    var view = channel.AsReadOnly();
                    Assert.That(view.Storage, Is.EqualTo(expected), label);
                    Assert.That(view.Palette.Length, Is.GreaterThan(0), $"{label} palette");
                    Assert.That(view.Words.Length, Is.GreaterThan(0), $"{label} words");
                }
                finally { channel.Dispose(); }
            }

            Check(new PaletteChannel(0), ChannelStorage.Uniform, "constructed uniform");
            Check(PaletteChannel.FromValues(new uint[ChunkLayout.Volume]), ChannelStorage.Uniform, "bulk-loaded air");

            var mixed = new uint[ChunkLayout.Volume];
            for (int i = 0; i < mixed.Length; i++) mixed[i] = (uint)(i % 5);
            Check(PaletteChannel.FromValues(mixed), ChannelStorage.Indirect, "indirect");

            var dense = new uint[ChunkLayout.Volume];
            for (int i = 0; i < dense.Length; i++) dense[i] = (uint)(i % (PaletteChannel.MaxPaletteEntries + 4000));
            Check(PaletteChannel.FromValues(dense), ChannelStorage.Direct, "direct");
        }
    }
}
