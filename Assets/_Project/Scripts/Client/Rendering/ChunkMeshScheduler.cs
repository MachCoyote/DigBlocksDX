using System;
using System.Collections.Generic;
using DigBlocks.Voxels;
using DigBlocks.Voxels.Appearance;
using DigBlocks.Voxels.Definitions;
using DigBlocks.Voxels.Meshing;
using DigBlocks.Voxels.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering
{
    public sealed class ChunkMeshScheduler : IDisposable
    {
        private sealed class Entry
        {
            public ChunkAddress Address;
            public uint Slot;
            public ulong Dirty = 1, Built;
            public bool Pending;
            public long Enqueued;
        }
        private sealed class Worker : IDisposable
        {
            public readonly NativeArray<uint>[] Sources = new NativeArray<uint>[7];
            public readonly ResidentChunk[] Stamps = new ResidentChunk[7];
            public readonly NativeArray<uint> Padded = new(GreedyMesherJob.PaddedVolume, Allocator.Persistent);
            public readonly NativeArray<ulong> Mask = new(1024, Allocator.Persistent);
            public NativeList<PackedQuad> Output = new(GreedyMesherJob.MaximumQuads, Allocator.Persistent);
            public JobHandle Handle;
            public Entry Entry;
            public ResidentChunkStore Store;
            public ulong Epoch, Version;
            public int Present;
            public Worker() { for (int i = 0; i < Sources.Length; i++) Sources[i] = new NativeArray<uint>(ChunkLayout.Volume, Allocator.Persistent, NativeArrayOptions.UninitializedMemory); }
            public void Dispose()
            {
                Handle.Complete();
                foreach (var source in Sources) source.Dispose();
                Padded.Dispose(); Mask.Dispose(); Output.Dispose();
            }
        }

        private readonly Worker[] workers;
        private readonly Dictionary<ChunkAddress, Entry> entries = new();
        private readonly Stack<uint> freeSlots = new();
        private readonly List<ResidentChunk> initial = new();
        private readonly BlockAttributeTable attributes;
        private readonly BlockAppearanceTable appearance;
        private readonly TerrainRenderer renderer;
        private readonly TerrainRenderSettings settings;
        private ResidentChunkStore store;
        private long tick;
        public int ResidentCount => entries.Count;
        public int BuiltCount { get; private set; }
        public int StaleResults { get; private set; }
        public bool IsCurrent
        {
            get
            {
                foreach (var entry in entries.Values) if (entry.Pending || entry.Built != entry.Dirty) return false;
                return true;
            }
        }

        public ChunkMeshScheduler(CompiledBlockContent content, TerrainRenderSettings settings, TerrainRenderer renderer)
        {
            this.settings = settings; this.renderer = renderer;
            attributes = content.Registry.CreateAttributeTable(Allocator.Persistent);
            appearance = BlockAppearanceTable.Create(content, Allocator.Persistent);
            workers = new Worker[settings.MeshWorkers];
            for (int i = 0; i < workers.Length; i++) workers[i] = new Worker();
            ResetSlots();
        }

        public void Tick(ResidentChunkStore current, float3 cameraPosition)
        {
            tick++;
            if (current != null && current.IsDisposed) current = null;
            if (!ReferenceEquals(store, current))
            {
                if (store != null) { store.ReplicaChanged -= OnChanged; store.ReplicasReset -= OnReset; }
                OnReset(); store = current;
                if (store != null)
                {
                    store.ReplicaChanged += OnChanged; store.ReplicasReset += OnReset;
                    store.GetReplicaStamps(initial);
                    foreach (var stamp in initial) OnChanged(stamp.Address);
                }
            }
            foreach (var worker in workers)
            {
                if (worker.Entry == null || !worker.Handle.IsCompleted) continue;
                worker.Handle.Complete();
                var entry = worker.Entry;
                bool currentEntry = entries.TryGetValue(entry.Address, out var active) && ReferenceEquals(active, entry);
                if (!currentEntry || !IsWorkerCurrent(worker))
                {
                    entry.Pending = false; worker.Entry = null; StaleResults++; continue;
                }
                if (!renderer.TryPublish(entry.Slot, entry.Address.Position, worker.Output.AsArray())) continue;
                if (entry.Built == 0) BuiltCount++;
                entry.Built = worker.Version; entry.Pending = false; worker.Entry = null;
            }
            if (store == null) return;
            foreach (var worker in workers)
            {
                if (worker.Entry != null) continue;
                Entry best = null; double priority = double.MaxValue;
                foreach (var entry in entries.Values)
                {
                    if (entry.Pending || entry.Dirty == entry.Built) continue;
                    double distance = math.lengthsq((float3)entry.Address.Position * 32 + 16 - cameraPosition);
                    double score = distance / (1 + (tick - entry.Enqueued) * 0.1);
                    if (score < priority) { best = entry; priority = score; }
                }
                if (best == null) break;
                Schedule(worker, best);
            }
        }

        private void Schedule(Worker worker, Entry entry)
        {
            worker.Present = 0; worker.Handle = default;
            worker.Store = store; worker.Epoch = store.InterestEpoch; worker.Version = entry.Dirty;
            for (int i = 0; i < 7; i++)
            {
                var address = Neighbor(entry.Address, i);
                if (!store.TryGetReplicaStamp(address, out worker.Stamps[i])) continue;
                worker.Present |= 1 << i;
                worker.Handle = JobHandle.CombineDependencies(worker.Handle, store.ScheduleReplicaSolidCopy(address, worker.Sources[i]));
            }
            var padding = new PaddingJob
            {
                Center = worker.Sources[0], Down = worker.Sources[1], Up = worker.Sources[2], North = worker.Sources[3],
                South = worker.Sources[4], West = worker.Sources[5], East = worker.Sources[6], Present = worker.Present, Output = worker.Padded
            }.Schedule(GreedyMesherJob.PaddedVolume, 256, worker.Handle);
            worker.Handle = new GreedyMesherJob
            {
                Voxels = worker.Padded, Mask = worker.Mask, Output = worker.Output, Appearance = appearance.AsReadOnly(),
                Attributes = attributes.AsReadOnly().Solids, ChunkPosition = entry.Address.Position, Slot = entry.Slot,
                Seed = settings.VisualSeed ^ entry.Address.World
            }.Schedule(padding);
            worker.Entry = entry; entry.Pending = true;
        }

        private bool IsWorkerCurrent(Worker worker)
        {
            if (store == null || !ReferenceEquals(store, worker.Store) || store.InterestEpoch != worker.Epoch || worker.Entry.Dirty != worker.Version) return false;
            for (int i = 0; i < 7; i++)
            {
                bool present = store.TryGetReplicaStamp(Neighbor(worker.Entry.Address, i), out var stamp);
                if (present != ((worker.Present & (1 << i)) != 0)) return false;
                if (present && (stamp.Incarnation != worker.Stamps[i].Incarnation || stamp.Revision != worker.Stamps[i].Revision)) return false;
            }
            return true;
        }

        private void OnChanged(ChunkAddress address)
        {
            if (!entries.TryGetValue(address, out var entry))
            {
                if (freeSlots.Count == 0) throw new InvalidOperationException("Terrain chunk capacity is smaller than admitted residency.");
                entry = new Entry { Address = address, Slot = freeSlots.Pop(), Enqueued = tick };
                entries.Add(address, entry);
            }
            for (int i = 0; i < 7; i++)
                if (entries.TryGetValue(Neighbor(address, i), out var dirty))
                {
                    if (dirty.Built == dirty.Dirty) dirty.Enqueued = tick;
                    dirty.Dirty++;
                }
        }
        private void OnReset()
        {
            entries.Clear(); ResetSlots(); BuiltCount = 0; renderer.ClearMeshes();
        }
        private void ResetSlots() { freeSlots.Clear(); for (int i = settings.MaxChunks - 1; i >= 0; i--) freeSlots.Push((uint)i); }
        private static ChunkAddress Neighbor(ChunkAddress address, int index)
        {
            if (index == 0) return address;
            FaceBasis.Get((BlockFace)(index - 1), out var normal, out _, out _);
            return new ChunkAddress(address.World, address.Position + normal);
        }
        public void Dispose()
        {
            if (store != null) { store.ReplicaChanged -= OnChanged; store.ReplicasReset -= OnReset; }
            foreach (var worker in workers) worker.Dispose();
            appearance.Dispose(); attributes.Dispose(); entries.Clear();
        }

        [BurstCompile]
        private struct PaddingJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<uint> Center, Down, Up, North, South, West, East;
            public int Present;
            [WriteOnly] public NativeArray<uint> Output;
            public void Execute(int index)
            {
                int x = index % 34 - 1, z = index / 34 % 34 - 1, y = index / (34 * 34) - 1;
                int outside = (x < 0 || x > 31 ? 1 : 0) + (y < 0 || y > 31 ? 1 : 0) + (z < 0 || z > 31 ? 1 : 0);
                if (outside > 1) { Output[index] = 0; return; }
                int source = y < 0 ? 1 : y > 31 ? 2 : z > 31 ? 3 : z < 0 ? 4 : x < 0 ? 5 : x > 31 ? 6 : 0;
                if ((Present & (1 << source)) == 0) { Output[index] = 0; return; }
                int cell = (x & 31) + 32 * ((z & 31) + 32 * (y & 31));
                switch (source)
                {
                    case 0: Output[index] = Center[cell]; break;
                    case 1: Output[index] = Down[cell]; break;
                    case 2: Output[index] = Up[cell]; break;
                    case 3: Output[index] = North[cell]; break;
                    case 4: Output[index] = South[cell]; break;
                    case 5: Output[index] = West[cell]; break;
                    default: Output[index] = East[cell]; break;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial class TerrainMeshingSystem : SystemBase
    {
        public Action Tick;
        protected override void OnUpdate() => Tick?.Invoke();
    }
}
