using System;
using System.Collections.Generic;
using Unity.Collections;

namespace DigBlocks.Voxels
{
    public enum ChannelStorage : byte { Uniform, Indirect, Direct }

    //exclusive owner; copying this struct does not transfer allocation ownership.
    public struct PaletteChannel : IDisposable
    {
        //a chunk holds at most Volume distinct values, so an indirect palette never needs more than
        //fifteen bits, and at fifteen the palette already costs more than the values it stands in for.
        public const int MaxIndirectBits = 14;
        public const int MaxPaletteEntries = 1 << MaxIndirectBits;
        public const int DirectBits = 32;

        [ThreadStatic] private static ushort[] scratchEntries;
        [ThreadStatic] private static uint[] scratchCells;
        [ThreadStatic] private static Dictionary<uint, int> scratchIndices;
        [ThreadStatic] private static List<uint> scratchOrder;

        private NativeList<uint> palette;
        //cells packed at BitsPerEntry and never straddling a word, so reading one is a shift and a
        //mask. A word's leftover high bits are padding and are always zero.
        private NativeList<ulong> words;
        private NativeParallelHashMap<uint, int> lookup;
        public ChannelStorage Storage { get; private set; }
        public int BitsPerEntry { get; private set; }

        public PaletteChannel(uint initial)
        {
            palette = new NativeList<uint>(1, Allocator.Persistent);
            palette.Add(initial);
            words = new NativeList<ulong>(0, Allocator.Persistent);
            lookup = default;
            Storage = ChannelStorage.Uniform;
            BitsPerEntry = 0;
        }

        //Bulk load. Driving Set per cell re-reads the cell, probes the palette map and can rewrite the
        //whole channel mid-build when the palette outgrows its width. This collects the palette in one
        //pass, picks the final width up front, and packs the cells once.
        public static PaletteChannel FromValues(ReadOnlySpan<uint> values)
        {
            if (values.Length != ChunkLayout.Volume)
                throw new ArgumentException("A channel load requires exactly one chunk of cells.", nameof(values));

            //scratch is per thread and reused for the life of that thread: a load runs this once per
            //channel per chunk, and freshly allocating it each time was the bulk of the remaining garbage.
            var indices = scratchIndices ??= new Dictionary<uint, int>();
            var order = scratchOrder ??= new List<uint>();
            //a chunk cannot hold more distinct values than cells, so an entry always fits a ushort.
            var entries = scratchEntries ??= new ushort[ChunkLayout.Volume];
            indices.Clear(); order.Clear();
            for (int i = 0; i < values.Length; i++)
            {
                if (!indices.TryGetValue(values[i], out int entry))
                {
                    entry = order.Count;
                    order.Add(values[i]);
                    indices.Add(values[i], entry);
                }
                entries[i] = (ushort)entry;
            }

            var channel = default(PaletteChannel);
            channel.palette = new NativeList<uint>(order.Count, Allocator.Persistent);
            for (int i = 0; i < order.Count; i++) channel.palette.Add(order[i]);

            if (order.Count == 1)
            {
                channel.words = new NativeList<ulong>(0, Allocator.Persistent);
                channel.lookup = default;
                channel.Storage = ChannelStorage.Uniform;
                channel.BitsPerEntry = 0;
                return channel;
            }

            bool direct = order.Count > MaxPaletteEntries;
            channel.Storage = direct ? ChannelStorage.Direct : ChannelStorage.Indirect;
            channel.BitsPerEntry = direct ? DirectBits : BitsFor(order.Count);
            int wordCount = WordCount(channel.BitsPerEntry);
            channel.words = new NativeList<ulong>(wordCount, Allocator.Persistent);
            //cleared so a word's padding bits are deterministic rather than whatever the allocator left.
            channel.words.Resize(wordCount, NativeArrayOptions.ClearMemory);
            var packed = channel.words.AsArray();
            if (direct) for (int i = 0; i < values.Length; i++) Write(packed, i, values[i], DirectBits);
            else for (int i = 0; i < values.Length; i++) Write(packed, i, entries[i], channel.BitsPerEntry);

            //Direct storage holds raw values and needs no reverse map; the promote path disposes it too.
            if (direct) channel.lookup = default;
            else
            {
                channel.lookup = new NativeParallelHashMap<uint, int>(Math.Max(16, order.Count), Allocator.Persistent);
                for (int i = 0; i < order.Count; i++) channel.lookup.Add(order[i], i);
            }
            return channel;
        }

        public uint Get(int index)
        {
            CheckIndex(index);
            if (Storage == ChannelStorage.Uniform) return palette[0];
            uint raw = Read(words.AsArray(), index, BitsPerEntry);
            return Storage == ChannelStorage.Direct ? raw : palette[(int)raw];
        }

        public void Set(int index, uint value)
        {
            CheckIndex(index);
            if (Get(index) == value) return;
            if (Storage == ChannelStorage.Uniform)
            {
                lookup = new NativeParallelHashMap<uint, int>(16, Allocator.Persistent);
                lookup.Add(palette[0], 0);
                words.Resize(WordCount(1), NativeArrayOptions.ClearMemory);
                Storage = ChannelStorage.Indirect;
                BitsPerEntry = 1;
            }
            if (Storage == ChannelStorage.Direct) { Write(words.AsArray(), index, value, DirectBits); return; }
            if (!lookup.TryGetValue(value, out int entry))
            {
                if (palette.Length == MaxPaletteEntries)
                {
                    PromoteDirect();
                    Write(words.AsArray(), index, value, DirectBits);
                    return;
                }
                entry = palette.Length;
                //widen before adding, so the entry the palette is about to hand out has room to be written.
                if (entry >= 1 << BitsPerEntry) Widen(BitsPerEntry + 1);
                palette.Add(value);
                if (lookup.Count() == lookup.Capacity) lookup.Capacity *= 2;
                lookup.Add(value, entry);
            }
            Write(words.AsArray(), index, (uint)entry, BitsPerEntry);
        }

        public ReadView AsReadOnly() => new ReadView
        {
            Palette = palette.AsArray().AsReadOnly(), Words = words.AsArray().AsReadOnly(),
            Storage = Storage, BitsPerEntry = BitsPerEntry
        };

        //palette entries per word at a given width, and the words a whole chunk needs. Entries never
        //straddle a word, so a width that does not divide 64 leaves a few bits unused per word.
        public static int EntriesPerWord(int bits) => 64 / bits;

        public static int WordCount(int bits)
        {
            int perWord = EntriesPerWord(bits);
            return (ChunkLayout.Volume + perWord - 1) / perWord;
        }

        //the narrowest width that can index the whole palette. Matches the wire codec's rule.
        public static int BitsFor(int paletteCount)
        {
            int bits = 0;
            for (int value = paletteCount - 1; value != 0; value >>= 1) bits++;
            return Math.Max(1, bits);
        }

        private static void Locate(int index, int bits, out int word, out int shift)
        {
            int perWord = 64 / bits;
            word = index / perWord;
            shift = (index - word * perWord) * bits;
        }

        private static uint Read(NativeArray<ulong> source, int index, int bits)
        {
            Locate(index, bits, out int word, out int shift);
            return (uint)((source[word] >> shift) & ((1ul << bits) - 1));
        }

        private static void Write(NativeArray<ulong> destination, int index, uint value, int bits)
        {
            Locate(index, bits, out int word, out int shift);
            ulong mask = ((1ul << bits) - 1) << shift;
            destination[word] = destination[word] & ~mask | (ulong)value << shift & mask;
        }

        private void Widen(int bits)
        {
            var cells = scratchCells ??= new uint[ChunkLayout.Volume];
            var source = words.AsArray();
            for (int i = 0; i < ChunkLayout.Volume; i++) cells[i] = Read(source, i, BitsPerEntry);
            words.Resize(WordCount(bits), NativeArrayOptions.ClearMemory);
            var destination = words.AsArray();
            BitsPerEntry = bits;
            for (int i = 0; i < ChunkLayout.Volume; i++) Write(destination, i, cells[i], bits);
        }

        private void PromoteDirect()
        {
            var cells = scratchCells ??= new uint[ChunkLayout.Volume];
            var source = words.AsArray();
            for (int i = 0; i < ChunkLayout.Volume; i++) cells[i] = palette[(int)Read(source, i, BitsPerEntry)];
            words.Resize(WordCount(DirectBits), NativeArrayOptions.ClearMemory);
            var destination = words.AsArray();
            Storage = ChannelStorage.Direct;
            BitsPerEntry = DirectBits;
            for (int i = 0; i < ChunkLayout.Volume; i++) Write(destination, i, cells[i], DirectBits);
            lookup.Dispose();
            lookup = default;
        }

        private static void CheckIndex(int index)
        {
            if ((uint)index >= ChunkLayout.Volume) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public void Dispose()
        {
            if (lookup.IsCreated) lookup.Dispose();
            if (words.IsCreated) words.Dispose();
            if (palette.IsCreated) palette.Dispose();
        }

        public struct ReadView
        {
            [ReadOnly] public NativeArray<uint>.ReadOnly Palette;
            [ReadOnly] public NativeArray<ulong>.ReadOnly Words;
            public ChannelStorage Storage;
            public int BitsPerEntry;

            public uint Get(int index)
            {
                CheckIndex(index);
                if (Storage == ChannelStorage.Uniform) return Palette[0];
                Locate(index, BitsPerEntry, out int word, out int shift);
                uint raw = (uint)((Words[word] >> shift) & ((1ul << BitsPerEntry) - 1));
                return Storage == ChannelStorage.Direct ? raw : Palette[(int)raw];
            }
        }
    }
}
