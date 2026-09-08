using System;
using Unity.Collections;

namespace DigBlocks.Voxels
{
    //caller-owned compiled attribute tables indexed by runtime state id. The registry stays non-disposable
    //so a world can own exactly one shared copy without every registry consumer inheriting a lifetime.
    public struct BlockAttributeTable : IDisposable
    {
        private NativeArray<BlockAttributes> solids, fluids;
        public bool IsCreated => solids.IsCreated;

        internal BlockAttributeTable(NativeArray<BlockAttributes> solids, NativeArray<BlockAttributes> fluids)
        {
            this.solids = solids; this.fluids = fluids;
        }

        public ReadOnly AsReadOnly()
        {
            if (!IsCreated) throw new InvalidOperationException("Attribute table has not been created.");
            return new ReadOnly(solids.AsReadOnly(), fluids.AsReadOnly());
        }

        public void Dispose()
        {
            if (solids.IsCreated) solids.Dispose();
            if (fluids.IsCreated) fluids.Dispose();
            solids = default; fluids = default;
        }

        //Burst-friendly value copy; safe to capture inside jobs.
        public readonly struct ReadOnly
        {
            public readonly NativeArray<BlockAttributes>.ReadOnly Solids;
            public readonly NativeArray<BlockAttributes>.ReadOnly Fluids;

            internal ReadOnly(NativeArray<BlockAttributes>.ReadOnly solids, NativeArray<BlockAttributes>.ReadOnly fluids)
            {
                Solids = solids; Fluids = fluids;
            }

            public BlockAttributes Solid(uint id) => Solids[(int)id];
            public BlockAttributes Fluid(uint id) => Fluids[(int)id];
        }
    }
}
