using System;
using System.Collections.Generic;
using Unity.Collections;

namespace DigBlocks.Voxels
{
    public enum ChannelStorage : byte { Uniform, Palette8, Palette16, Direct }

    //exclusive owner; copying this struct does not transfer allocation ownership.
    public struct PaletteChannel : IDisposable
    {
        private const int DirectThreshold = 4096;
        [ThreadStatic] private static ushort[] scratchEntries;
        [ThreadStatic] private static byte[] scratchPacked;
        [ThreadStatic] private static Dictionary<uint, int> scratchIndices;
        [ThreadStatic] private static List<uint> scratchOrder;
        private NativeList<uint> palette;
        private NativeList<byte> cells;
        private NativeParallelHashMap<uint, int> lookup;
        public ChannelStorage Storage { get; private set; }

        public PaletteChannel(uint initial)
        {
            palette = new NativeList<uint>(1, Allocator.Persistent);
            palette.Add(initial);
            cells = new NativeList<byte>(0, Allocator.Persistent);
            lookup = default;
            Storage = ChannelStorage.Uniform;
        }

        //Bulk load. Driving Set per cell re-reads the cell, probes the palette map and constructs a read
        //view every time, and can rewrite the whole channel mid-build when the palette crosses a storage
        //threshold. This collects the palette in one pass, picks the final width up front, and fills the
        //cells with a single copy.
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
                channel.cells = new NativeList<byte>(0, Allocator.Persistent);
                channel.lookup = default;
                channel.Storage = ChannelStorage.Uniform;
                return channel;
            }

            channel.Storage = order.Count <= 256 ? ChannelStorage.Palette8
                : order.Count <= DirectThreshold ? ChannelStorage.Palette16 : ChannelStorage.Direct;
            int width = channel.Storage == ChannelStorage.Palette8 ? 1 : channel.Storage == ChannelStorage.Palette16 ? 2 : 4;

            //sized for the widest storage class once, then only the leading width bytes per cell are used.
            var packed = scratchPacked ??= new byte[ChunkLayout.Volume * 4];
            int packedLength = ChunkLayout.Volume * width;
            if (channel.Storage == ChannelStorage.Direct)
                for (int i = 0; i < values.Length; i++)
                {
                    uint value = values[i];
                    int at = i * 4;
                    packed[at] = (byte)value; packed[at + 1] = (byte)(value >> 8);
                    packed[at + 2] = (byte)(value >> 16); packed[at + 3] = (byte)(value >> 24);
                }
            else if (width == 1)
                for (int i = 0; i < entries.Length; i++) packed[i] = (byte)entries[i];
            else
                for (int i = 0; i < entries.Length; i++)
                { packed[i * 2] = (byte)entries[i]; packed[i * 2 + 1] = (byte)(entries[i] >> 8); }

            channel.cells = new NativeList<byte>(packedLength, Allocator.Persistent);
            channel.cells.ResizeUninitialized(packedLength);
            NativeArray<byte>.Copy(packed, 0, channel.cells.AsArray(), 0, packedLength);

            //Direct storage holds raw values and needs no reverse map; the promote path disposes it too.
            if (channel.Storage == ChannelStorage.Direct) channel.lookup = default;
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
            return AsReadOnly().Get(index);
        }

        public void Set(int index, uint value)
        {
            CheckIndex(index);
            if (Get(index) == value) return;
            if (Storage == ChannelStorage.Uniform)
            {
                lookup = new NativeParallelHashMap<uint, int>(16, Allocator.Persistent);
                lookup.Add(palette[0], 0);
                cells.Resize(ChunkLayout.Volume, NativeArrayOptions.ClearMemory);
                Storage = ChannelStorage.Palette8;
            }
            if (Storage == ChannelStorage.Direct) { Write(index, value, 4); return; }
            if (!lookup.TryGetValue(value, out int entry))
            {
                if (palette.Length == DirectThreshold)
                {
                    PromoteDirect();
                    Write(index, value, 4);
                    return;
                }
                entry = palette.Length;
                palette.Add(value);
                if (lookup.Count() == lookup.Capacity) lookup.Capacity *= 2;
                lookup.Add(value, entry);
                if (entry == 256) Promote16();
            }
            Write(index, (uint)entry, Storage == ChannelStorage.Palette8 ? 1 : 2);
        }

        public ReadView AsReadOnly() => new ReadView
        {
            Palette = palette.AsArray().AsReadOnly(), Cells = cells.AsArray().AsReadOnly(), Storage = Storage
        };

        private void Promote16()
        {
            cells.ResizeUninitialized(ChunkLayout.Volume * 2);
            for (int i = ChunkLayout.Volume - 1; i >= 0; i--) Write(i, cells[i], 2);
            Storage = ChannelStorage.Palette16;
        }

        private void PromoteDirect()
        {
            cells.ResizeUninitialized(ChunkLayout.Volume * 4);
            for (int i = ChunkLayout.Volume - 1; i >= 0; i--)
            {
                uint entry = (uint)(cells[i * 2] | cells[i * 2 + 1] << 8);
                Write(i, palette[(int)entry], 4);
            }
            Storage = ChannelStorage.Direct;
            lookup.Dispose();
        }

        private void Write(int index, uint value, int width)
        {
            for (int b = 0; b < width; b++) cells[index * width + b] = (byte)(value >> (b * 8));
        }

        private static void CheckIndex(int index)
        {
            if ((uint)index >= ChunkLayout.Volume) throw new ArgumentOutOfRangeException(nameof(index));
        }

        public void Dispose()
        {
            if (lookup.IsCreated) lookup.Dispose();
            if (cells.IsCreated) cells.Dispose();
            if (palette.IsCreated) palette.Dispose();
        }

        public struct ReadView
        {
            [ReadOnly] public NativeArray<uint>.ReadOnly Palette;
            [ReadOnly] public NativeArray<byte>.ReadOnly Cells;
            public ChannelStorage Storage;

            public uint Get(int index)
            {
                CheckIndex(index);
                if (Storage == ChannelStorage.Uniform) return Palette[0];
                int width = Storage == ChannelStorage.Palette8 ? 1 : Storage == ChannelStorage.Palette16 ? 2 : 4;
                uint value = 0;
                for (int b = 0; b < width; b++) value |= (uint)Cells[index * width + b] << (b * 8);
                return Storage == ChannelStorage.Direct ? value : Palette[(int)value];
            }
        }
    }
}
