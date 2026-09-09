using System;
using System.Collections.Generic;

namespace DigBlocks.Client.Rendering
{
    //frame snapshots hold references until their GPU work completes and the snapshot is replaced.
    //retired geometry cannot be reused while any snapshot can still draw it, including a reused frame.
    public sealed class MeshRangeAllocator
    {
        public sealed class Allocation
        {
            public int Start { get; internal set; }
            public int Count { get; internal set; }
            internal int References;
            internal bool Retired, Freed;
        }
        private struct Range { public int Start, Count; }
        private readonly List<Range> free = new();
        public int Capacity { get; }
        public int Allocated { get; private set; }
        public MeshRangeAllocator(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity; free.Add(new Range { Count = capacity });
        }
        public Allocation Allocate(int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < free.Count; i++)
            {
                var range = free[i];
                if (range.Count < count) continue;
                var allocation = new Allocation { Start = range.Start, Count = count };
                range.Start += count; range.Count -= count;
                if (range.Count == 0) free.RemoveAt(i); else free[i] = range;
                Allocated += count;
                return allocation;
            }
            return null;
        }
        public void Retain(Allocation allocation)
        {
            if (allocation == null || allocation.Freed) throw new InvalidOperationException("Cannot retain freed geometry.");
            allocation.References++;
        }
        public void Release(Allocation allocation)
        {
            if (allocation == null || allocation.References <= 0) throw new InvalidOperationException("Unbalanced mesh reference.");
            allocation.References--;
            if (allocation.Retired && allocation.References == 0) Free(allocation);
        }
        public void Retire(Allocation allocation)
        {
            if (allocation == null) return;
            if (allocation.Retired) throw new InvalidOperationException("Mesh already retired.");
            allocation.Retired = true;
            if (allocation.References == 0) Free(allocation);
        }
        private void Free(Allocation allocation)
        {
            allocation.Freed = true; Allocated -= allocation.Count;
            int index = 0;
            while (index < free.Count && free[index].Start < allocation.Start) index++;
            free.Insert(index, new Range { Start = allocation.Start, Count = allocation.Count });
            if (index > 0 && free[index - 1].Start + free[index - 1].Count == free[index].Start)
            {
                var previous = free[index - 1]; previous.Count += free[index].Count;
                free[index - 1] = previous; free.RemoveAt(index); index--;
            }
            if (index + 1 < free.Count && free[index].Start + free[index].Count == free[index + 1].Start)
            {
                var range = free[index]; range.Count += free[index + 1].Count;
                free[index] = range; free.RemoveAt(index + 1);
            }
        }
    }
}
