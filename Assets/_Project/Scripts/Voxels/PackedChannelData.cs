using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels
{
    /// <summary>
    /// One channel in the layout storage and the wire both use, held in caller-owned buffers sized for
    /// the widest case. A load reuses one set of these rather than allocating per chunk, and moving a
    /// channel through it is a palette copy plus a word copy in either direction.
    /// <para>
    /// Deliberately managed throughout: worldgen packs into one of these on a worker thread, where
    /// touching native containers is not allowed.
    /// </para>
    /// </summary>
    public sealed class PackedChannelData
    {
        [ThreadStatic] private static Dictionary<uint, int> scratchIndices;
        [ThreadStatic] private static ushort[] scratchEntries;

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
            if ((uint)index >= ChunkLayout.Volume) throw new ArgumentOutOfRangeException(nameof(index));
            if (Storage == ChannelStorage.Uniform) return Palette[0];
            int perWord = PaletteChannel.EntriesPerWord(BitsPerEntry), word = index / perWord;
            uint raw = (uint)(Words[word] >> (index - word * perWord) * BitsPerEntry & (1ul << BitsPerEntry) - 1);
            return Storage == ChannelStorage.Direct ? raw : Palette[(int)raw];
        }

        public void SetUniform(uint value)
        {
            Storage = ChannelStorage.Uniform; BitsPerEntry = 0; PaletteCount = 1; Palette[0] = value;
        }

        /// <summary>
        /// Packs one value per cell into this layout. Collects the palette in one pass, picks the final
        /// width up front, and fills the words once; driving a channel cell by cell instead re-reads
        /// each cell and can rewrite the whole thing when the palette outgrows its width.
        /// </summary>
        public static void Pack(ReadOnlySpan<uint> values, PackedChannelData destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (values.Length != ChunkLayout.Volume)
                throw new ArgumentException("A channel holds exactly one chunk of cells.", nameof(values));

            //scratch is per thread and reused for its lifetime: packing runs once per channel per chunk,
            //and allocating it each time was the bulk of the garbage this path used to produce.
            var indices = scratchIndices ??= new Dictionary<uint, int>();
            //a chunk cannot hold more distinct values than cells, so an entry always fits a ushort.
            var entries = scratchEntries ??= new ushort[ChunkLayout.Volume];
            indices.Clear();
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (!indices.TryGetValue(values[i], out int entry))
                {
                    entry = count++;
                    indices.Add(values[i], entry);
                    //past the indirect limit the palette is not written at all; Direct carries values.
                    if (entry < PaletteChannel.MaxPaletteEntries) destination.Palette[entry] = values[i];
                }
                entries[i] = (ushort)entry;
            }

            if (count == 1) { destination.SetUniform(values[0]); return; }

            bool direct = count > PaletteChannel.MaxPaletteEntries;
            destination.Storage = direct ? ChannelStorage.Direct : ChannelStorage.Indirect;
            destination.BitsPerEntry = direct ? PaletteChannel.DirectBits : PaletteChannel.BitsFor(count);
            destination.PaletteCount = direct ? 0 : count;

            int bits = destination.BitsPerEntry, perWord = PaletteChannel.EntriesPerWord(bits);
            int words = PaletteChannel.WordCount(bits);
            //cleared first so a partly filled word's padding bits are zero rather than left over from
            //whatever chunk used this buffer before.
            Array.Clear(destination.Words, 0, words);
            for (int i = 0; i < values.Length; i++)
            {
                int word = i / perWord;
                ulong value = direct ? values[i] : entries[i];
                destination.Words[word] |= value << (i - word * perWord) * bits;
            }
        }
    }
}
