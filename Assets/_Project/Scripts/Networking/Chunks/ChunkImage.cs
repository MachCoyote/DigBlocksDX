using System;
using System.Collections.Generic;
using DigBlocks.Voxels;

namespace DigBlocks.ChunkProtocol
{
    /// <summary>Detached immutable public projection; safe to encode on a worker thread.</summary>
    public sealed class ChunkImage
    {
        public ChunkAddress Address { get; }
        public ulong Incarnation { get; }
        public ulong Revision { get; }
        private readonly uint[] solids;
        private readonly uint[] fluids;

        public ChunkImage(ChunkAddress address, ulong incarnation, ulong revision, uint[] solids, uint[] fluids)
        {
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            if (revision == 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (solids == null || solids.Length != ChunkLayout.Volume)
                throw new ArgumentException("Solid channel must contain exactly one chunk.", nameof(solids));
            if (fluids == null || fluids.Length != ChunkLayout.Volume)
                throw new ArgumentException("Fluid channel must contain exactly one chunk.", nameof(fluids));
            Address = address;
            Incarnation = incarnation;
            Revision = revision;
            this.solids = (uint[])solids.Clone();
            this.fluids = (uint[])fluids.Clone();
        }

        public uint SolidAt(int index) => solids[index];
        public uint FluidAt(int index) => fluids[index];
        public uint[] CopySolids() => (uint[])solids.Clone();
        public uint[] CopyFluids() => (uint[])fluids.Clone();
    }

    public readonly struct ChunkCellUpdate
    {
        public readonly int Index;
        public readonly uint Solid;
        public readonly uint Fluid;
        public ChunkCellUpdate(int index, uint solid, uint fluid)
        {
            Index = index;
            Solid = solid;
            Fluid = fluid;
        }
    }

    /// <summary>Absolute cell replacements against one exact immutable baseline.</summary>
    public sealed class ChunkDelta
    {
        public ChunkAddress Address { get; }
        public ulong Incarnation { get; }
        public ulong BaseRevision { get; }
        public ulong ResultRevision { get; }
        public IReadOnlyList<ChunkCellUpdate> Updates { get; }

        public ChunkDelta(ChunkAddress address, ulong incarnation, ulong baseRevision,
            ulong resultRevision, IReadOnlyList<ChunkCellUpdate> updates)
        {
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            if (baseRevision == 0) throw new ArgumentOutOfRangeException(nameof(baseRevision));
            if (resultRevision <= baseRevision) throw new ArgumentOutOfRangeException(nameof(resultRevision));
            if (updates == null || updates.Count == 0 || updates.Count > ChunkLayout.Volume)
                throw new ArgumentException("Delta must contain between one and Volume cell replacements.", nameof(updates));
            var copy = new ChunkCellUpdate[updates.Count];
            var seen = new HashSet<int>();
            for (int i = 0; i < copy.Length; i++)
            {
                var update = updates[i];
                if ((uint)update.Index >= ChunkLayout.Volume) throw new ArgumentOutOfRangeException(nameof(updates));
                if (!seen.Add(update.Index)) throw new ArgumentException("Duplicate cell replacement.", nameof(updates));
                copy[i] = update;
            }
            Address = address;
            Incarnation = incarnation;
            BaseRevision = baseRevision;
            ResultRevision = resultRevision;
            Updates = Array.AsReadOnly(copy);
        }

        public ChunkImage ApplyTo(ChunkImage baseline)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (!baseline.Address.Equals(Address) || baseline.Incarnation != Incarnation || baseline.Revision != BaseRevision)
                throw new InvalidOperationException("Delta does not match the chunk baseline.");
            uint[] solids = baseline.CopySolids();
            uint[] fluids = baseline.CopyFluids();
            foreach (var update in Updates)
            {
                solids[update.Index] = update.Solid;
                fluids[update.Index] = update.Fluid;
            }
            return new ChunkImage(Address, Incarnation, ResultRevision, solids, fluids);
        }
    }
}
