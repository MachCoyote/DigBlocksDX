namespace DigBlocks.Voxels
{
    /// <summary>
    /// One channel in the layout storage and the wire both use, held in caller-owned buffers sized for
    /// the widest case. A load reuses one set of these rather than allocating per chunk, and moving a
    /// channel through it is a palette copy plus a word copy in either direction.
    /// </summary>
    public sealed class PackedChannelData
    {
        public readonly uint[] Palette = new uint[PaletteChannel.MaxPaletteEntries];
        public readonly ulong[] Words = new ulong[PaletteChannel.WordCount(PaletteChannel.DirectBits)];
        public ChannelStorage Storage;
        public int BitsPerEntry;
        //entries the palette actually holds. Direct storage carries values in the cells and has none.
        public int PaletteCount;

        //words in use at the current width; a uniform channel has no cells at all.
        public int WordCount => Storage == ChannelStorage.Uniform ? 0 : PaletteChannel.WordCount(BitsPerEntry);

        public uint Get(int index)
        {
            if ((uint)index >= ChunkLayout.Volume) throw new System.ArgumentOutOfRangeException(nameof(index));
            if (Storage == ChannelStorage.Uniform) return Palette[0];
            int perWord = PaletteChannel.EntriesPerWord(BitsPerEntry), word = index / perWord;
            uint raw = (uint)(Words[word] >> (index - word * perWord) * BitsPerEntry & (1ul << BitsPerEntry) - 1);
            return Storage == ChannelStorage.Direct ? raw : Palette[(int)raw];
        }

        public void SetUniform(uint value)
        {
            Storage = ChannelStorage.Uniform; BitsPerEntry = 0; PaletteCount = 1; Palette[0] = value;
        }

        /// <summary>Packs one value per cell into this layout, for callers that do not hold one already.</summary>
        public static void Pack(System.ReadOnlySpan<uint> values, PackedChannelData destination)
        {
            if (destination == null) throw new System.ArgumentNullException(nameof(destination));
            var channel = PaletteChannel.FromValues(values);
            try { channel.CopyInto(destination); }
            finally { channel.Dispose(); }
        }
    }
}
