using System;
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

        [ThreadStatic] private static uint[] scratchCells;
        [ThreadStatic] private static PackedChannelData scratchPacked;

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

        //one packing implementation serves storage, the wire and worldgen; this is the storage end of it.
        public static PaletteChannel FromValues(ReadOnlySpan<uint> values)
        {
            var scratch = scratchPacked ??= new PackedChannelData();
            PackedChannelData.Pack(values, scratch);
            return FromPacked(scratch);
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

        /// <summary>Copies the channel out in the layout it already holds: a palette copy and a word copy.</summary>
        public void CopyInto(PackedChannelData destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Storage = Storage;
            destination.BitsPerEntry = BitsPerEntry;
            destination.PaletteCount = palette.Length;
            if (palette.Length != 0) NativeArray<uint>.Copy(palette.AsArray(), destination.Palette, palette.Length);
            if (words.Length != 0) NativeArray<ulong>.Copy(words.AsArray(), destination.Words, words.Length);
        }

        /// <summary>Adopts an already packed channel. The caller must have validated it.</summary>
        public static PaletteChannel FromPacked(PackedChannelData source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var channel = default(PaletteChannel);
            channel.Storage = source.Storage;
            channel.BitsPerEntry = source.BitsPerEntry;
            channel.palette = new NativeList<uint>(Math.Max(1, source.PaletteCount), Allocator.Persistent);
            for (int i = 0; i < source.PaletteCount; i++) channel.palette.Add(source.Palette[i]);
            int wordCount = source.WordCount;
            channel.words = new NativeList<ulong>(wordCount, Allocator.Persistent);
            if (wordCount != 0)
            {
                channel.words.ResizeUninitialized(wordCount);
                NativeArray<ulong>.Copy(source.Words, 0, channel.words.AsArray(), 0, wordCount);
            }
            //Uniform and Direct both index nothing, so neither needs the reverse map.
            if (source.Storage != ChannelStorage.Indirect) channel.lookup = default;
            else
            {
                channel.lookup = new NativeParallelHashMap<uint, int>(Math.Max(16, source.PaletteCount), Allocator.Persistent);
                for (int i = 0; i < source.PaletteCount; i++) channel.lookup.TryAdd(source.Palette[i], i);
            }
            return channel;
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
            //Resize only clears what it adds, and a wider entry can leave a word with padding bits
            //where the narrower layout kept data, so every word is zeroed before it is refilled.
            words.Resize(WordCount(bits), NativeArrayOptions.UninitializedMemory);
            var destination = words.AsArray();
            for (int i = 0; i < destination.Length; i++) destination[i] = 0;
            BitsPerEntry = bits;
            for (int i = 0; i < ChunkLayout.Volume; i++) Write(destination, i, cells[i], bits);
        }

        private void PromoteDirect()
        {
            var cells = scratchCells ??= new uint[ChunkLayout.Volume];
            var source = words.AsArray();
            for (int i = 0; i < ChunkLayout.Volume; i++) cells[i] = palette[(int)Read(source, i, BitsPerEntry)];
            words.Resize(WordCount(DirectBits), NativeArrayOptions.UninitializedMemory);
            var destination = words.AsArray();
            for (int i = 0; i < destination.Length; i++) destination[i] = 0;
            Storage = ChannelStorage.Direct;
            BitsPerEntry = DirectBits;
            for (int i = 0; i < ChunkLayout.Volume; i++) Write(destination, i, cells[i], DirectBits);
            palette.Clear();
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
