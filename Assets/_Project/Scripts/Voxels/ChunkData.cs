using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace DigBlocks.Voxels
{
    public readonly struct CellEdit
    {
        public readonly int Index;
        public readonly uint Solid, Fluid;
        public CellEdit(int index, uint solid, uint fluid) { Index = index; Solid = solid; Fluid = fluid; }
    }

    //world-local owner. Reads can run in jobs; mutations fence outstanding readers.
    public sealed class ChunkData : IDisposable
    {
        public ChunkAddress Address { get; }
        public ulong Incarnation { get; }
        public ulong Revision { get; private set; } = 1;
        private PaletteChannel solids, fluids;
        private NativeArray<uint> placed;
        private JobHandle readers;
        private bool disposed;
        private readonly Dictionary<int, BlockRecord> records = new();
        public ChunkData(ChunkAddress address, ulong incarnation, uint solid = 0, uint fluid = 0)
        {
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            Address = address; Incarnation = incarnation;
            solids = new PaletteChannel(solid); fluids = new PaletteChannel(fluid);
        }
        public uint SolidAt(int index) { RequireAlive(); return solids.Get(index); }
        public uint FluidAt(int index) { RequireAlive(); return fluids.Get(index); }

        //takes read-only views rather than arrays so a freshly decoded image can be loaded without copying
        //128 KiB per channel first. Neither span is retained.
        public static ChunkData FromChannels(ChunkAddress address, ulong incarnation, ulong revision, ReadOnlySpan<uint> solid, ReadOnlySpan<uint> fluid)
        {
            if (revision == 0 || solid.Length != ChunkLayout.Volume || fluid.Length != ChunkLayout.Volume)
                throw new ArgumentException("Invalid chunk image.");
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            var loadedSolids = PaletteChannel.FromValues(solid);
            PaletteChannel loadedFluids;
            try { loadedFluids = PaletteChannel.FromValues(fluid); }
            catch { loadedSolids.Dispose(); throw; }
            return new ChunkData(address, incarnation, loadedSolids, loadedFluids) { Revision = revision };
        }

        /// <summary>Adopts channels already in the stored layout, so loading one is a copy.</summary>
        public static ChunkData FromPacked(ChunkAddress address, ulong incarnation, ulong revision,
            PackedChannelData solid, PackedChannelData fluid)
        {
            if (revision == 0) throw new ArgumentException("Invalid chunk image.");
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            var loadedSolids = PaletteChannel.FromPacked(solid);
            PaletteChannel loadedFluids;
            try { loadedFluids = PaletteChannel.FromPacked(fluid); }
            catch { loadedSolids.Dispose(); throw; }
            return new ChunkData(address, incarnation, loadedSolids, loadedFluids) { Revision = revision };
        }

        /// <summary>
        /// One-shot bulk load of a freshly created chunk: replaces both channels outright rather than
        /// walking every generated cell through the per-cell mutation path.
        /// </summary>
        public void LoadGenerated(PackedChannelData solid, PackedChannelData fluid)
        {
            RequireAlive();
            if (Revision != 1) throw new InvalidOperationException("A generated load must precede any edit.");
            readers.Complete();
            var loadedSolids = PaletteChannel.FromPacked(solid);
            PaletteChannel loadedFluids;
            try { loadedFluids = PaletteChannel.FromPacked(fluid); }
            catch { loadedSolids.Dispose(); throw; }
            solids.Dispose(); fluids.Dispose();
            solids = loadedSolids; fluids = loadedFluids;
            Revision = 2;
        }

        /// <summary>Copies both channels out in the layout they are stored in, fencing outstanding readers.</summary>
        public void CopyPacked(PackedChannelData solid, PackedChannelData fluid)
        {
            RequireAlive();
            readers.Complete();
            solids.CopyInto(solid);
            fluids.CopyInto(fluid);
        }

        private ChunkData(ChunkAddress address, ulong incarnation, PaletteChannel solid, PaletteChannel fluid)
        {
            Address = address; Incarnation = incarnation;
            solids = solid; fluids = fluid;
        }

        public void ApplyReplica(CellEdit[] edits, ulong baseRevision, ulong resultRevision, uint maxSolid, uint maxFluid)
        {
            RequireAlive();
            if (Revision != baseRevision || resultRevision <= baseRevision) throw new InvalidOperationException("Replica baseline mismatch.");
            Apply(edits, maxSolid, maxFluid);
            Revision = resultRevision;
        }

        public void Apply(CellEdit[] edits, uint maxSolid, uint maxFluid)
        {
            RequireAlive();
            if (edits == null || edits.Length > ChunkLayout.Volume) throw new ArgumentException("Invalid edit batch.");
            //validate the whole batch before taking ownership of a mutable channel.
            var indices = new HashSet<int>();
            foreach (var edit in edits)
            {
                CheckIndex(edit.Index);
                if (edit.Solid > maxSolid || edit.Fluid > maxFluid) throw new ArgumentOutOfRangeException(nameof(edits));
                if (!indices.Add(edit.Index)) throw new ArgumentException("Duplicate cell in edit batch.");
            }
            if (edits.Length == 0) return;
            if (Revision == ulong.MaxValue) throw new InvalidOperationException("Chunk revision exhausted.");
            readers.Complete();
            bool changed = false;
            foreach (var edit in edits)
            {
                bool solidChanged = solids.Get(edit.Index) != edit.Solid;
                if (!solidChanged && fluids.Get(edit.Index) == edit.Fluid) continue;
                if (solidChanged) { SetPlayerPlaced(edit.Index, false); records.Remove(edit.Index); }
                solids.Set(edit.Index, edit.Solid); fluids.Set(edit.Index, edit.Fluid);
                changed = true;
            }
            if (changed) Revision++;
        }

        public void SetPlayerPlaced(int index, bool value)
        {
            RequireAlive(); CheckIndex(index);
            if (!placed.IsCreated)
            {
                if (!value) return;
                placed = new NativeArray<uint>((ChunkLayout.Volume + 31) / 32, Allocator.Persistent);
            }
            uint bit = 1u << (index & 31);
            placed[index >> 5] = value ? placed[index >> 5] | bit : placed[index >> 5] & ~bit;
        }
        public bool IsPlayerPlaced(int index)
        {
            RequireAlive(); CheckIndex(index);
            return placed.IsCreated && (placed[index >> 5] & (1u << (index & 31))) != 0;
        }

        //private persistent records are deliberately excluded from the public channel capture.
        public void SetRecord(int index, BlockRecord record)
        {
            RequireAlive(); CheckIndex(index);
            if (record == null) records.Remove(index);
            else records[index] = record;
        }
        public BlockRecord GetRecord(int index)
        { RequireAlive(); CheckIndex(index); return records.TryGetValue(index, out var value) ? value : null; }

        public ChunkCapture Capture()
        {
            RequireAlive();
            var capture = new ChunkCapture(Address, Incarnation, Revision);
            capture.Handle = new CaptureJob
            {
                Solids = solids.AsReadOnly(), Fluids = fluids.AsReadOnly(),
                SolidOutput = capture.Solids, FluidOutput = capture.Fluids
            }.Schedule(ChunkLayout.Volume, 256, readers);
            readers = capture.Handle;
            return capture;
        }

        //detaches solid data into caller-owned reusable memory; later mutation waits only for this copy.
        public JobHandle ScheduleSolidCopy(NativeArray<uint> destination, JobHandle dependency = default)
        {
            RequireAlive();
            if (!destination.IsCreated || destination.Length != ChunkLayout.Volume) throw new ArgumentException("Expected a full chunk destination.", nameof(destination));
            var handle = new SolidCopyJob { Source = solids.AsReadOnly(), Destination = destination }
                .Schedule(ChunkLayout.Volume, 256, JobHandle.CombineDependencies(readers, dependency));
            readers = handle;
            return handle;
        }

        [BurstCompile]
        private struct SolidCopyJob : IJobParallelFor
        {
            [ReadOnly] public PaletteChannel.ReadView Source;
            [WriteOnly] public NativeArray<uint> Destination;
            public void Execute(int index) => Destination[index] = Source.Get(index);
        }

        public void Dispose()
        {
            if (disposed) return;
            readers.Complete(); disposed = true;
            solids.Dispose(); fluids.Dispose();
            if (placed.IsCreated) placed.Dispose();
            records.Clear();
        }
        private void RequireAlive() { if (disposed) throw new ObjectDisposedException(nameof(ChunkData)); }
        private static void CheckIndex(int index)
        { if ((uint)index >= ChunkLayout.Volume) throw new ArgumentOutOfRangeException(nameof(index)); }

        [BurstCompile]
        private struct CaptureJob : IJobParallelFor
        {
            [ReadOnly] public PaletteChannel.ReadView Solids, Fluids;
            [WriteOnly] public NativeArray<uint> SolidOutput, FluidOutput;
            public void Execute(int index)
            { SolidOutput[index] = Solids.Get(index); FluidOutput[index] = Fluids.Get(index); }
        }
    }

    public sealed class BlockRecord
    {
        public const int MaxBytes = 16384;
        public string TypeKey { get; }
        public uint SchemaVersion { get; }
        private readonly byte[] payload;
        public BlockRecord(string typeKey, uint schemaVersion, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(typeKey) || typeKey.Length > 128 || schemaVersion == 0 || payload == null || payload.Length > MaxBytes)
                throw new ArgumentException("Invalid block record.");
            TypeKey = typeKey; SchemaVersion = schemaVersion; this.payload = (byte[])payload.Clone();
        }
        public byte[] CopyPayload() => (byte[])payload.Clone();
    }

    public sealed class ChunkCapture : IDisposable
    {
        public ChunkAddress Address { get; }
        public ulong Incarnation { get; }
        public ulong Revision { get; }
        internal NativeArray<uint> Solids, Fluids;
        internal JobHandle Handle;
        public bool IsCompleted => Handle.IsCompleted;
        internal ChunkCapture(ChunkAddress address, ulong incarnation, ulong revision)
        {
            Address = address; Incarnation = incarnation; Revision = revision;
            Solids = new NativeArray<uint>(ChunkLayout.Volume, Allocator.Persistent);
            Fluids = new NativeArray<uint>(ChunkLayout.Volume, Allocator.Persistent);
        }
        public uint[] CopySolids() { Handle.Complete(); return Solids.ToArray(); }
        public uint[] CopyFluids() { Handle.Complete(); return Fluids.ToArray(); }
        //overloads that fill a caller-owned buffer, so the snapshot pipeline can recycle its copies
        //instead of allocating two 128 KiB arrays for every chunk it encodes.
        public void CopySolids(uint[] destination) { Handle.Complete(); NativeArray<uint>.Copy(Solids, destination); }
        public void CopyFluids(uint[] destination) { Handle.Complete(); NativeArray<uint>.Copy(Fluids, destination); }
        public void Dispose()
        {
            Handle.Complete();
            if (Solids.IsCreated) Solids.Dispose();
            if (Fluids.IsCreated) Fluids.Dispose();
        }
    }
}
