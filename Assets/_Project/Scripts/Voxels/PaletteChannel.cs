using System;
using Unity.Collections;

namespace DigBlocks.Voxels
{
    public enum ChannelStorage : byte { Uniform, Palette8, Palette16, Direct }

    //exclusive owner; copying this struct does not transfer allocation ownership.
    public struct PaletteChannel : IDisposable
    {
        private const int DirectThreshold = 4096;
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
