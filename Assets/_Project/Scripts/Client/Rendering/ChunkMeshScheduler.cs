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
            //where this entry sits in the ready queue, so leaving it is a swap-remove rather than a scan.
            public int Bucket = -1, BucketSlot = -1;
        }
        private sealed class Worker : IDisposable
        {
            public readonly PaletteChannel.ReadView[] Views = new PaletteChannel.ReadView[7];
            public readonly ResidentChunk[] Stamps = new ResidentChunk[7];
            public readonly NativeArray<uint> Padded = new(GreedyMesherJob.PaddedVolume, Allocator.Persistent);
            public readonly NativeArray<ulong> Mask = new(1024, Allocator.Persistent);
            public readonly NativeArray<byte> VisibilityVisited = new(ChunkLayout.Volume, Allocator.Persistent);
            public readonly NativeArray<int> VisibilityQueue = new(ChunkLayout.Volume, Allocator.Persistent);
            public readonly NativeReference<ulong> Visibility = new(Allocator.Persistent);
            public NativeList<PackedQuad> Output = new(GreedyMesherJob.MaximumQuads, Allocator.Persistent);
            public JobHandle Handle;
            public Entry Entry;
            public ResidentChunkStore Store;
            public ulong Epoch, Version;
            public int Present;
            public void Dispose()
            {
                Handle.Complete();
                Padded.Dispose(); Mask.Dispose(); VisibilityVisited.Dispose(); VisibilityQueue.Dispose(); Visibility.Dispose(); Output.Dispose();
            }
        }

        private readonly Worker[] workers;
        //an absent neighbour is padded as air, and a channel holding nothing but air is exactly that,
        //so the job always gets a real view rather than an uncreated one to guard against.
        private PaletteChannel air = new PaletteChannel(0);
        private readonly Dictionary<ChunkAddress, Entry> entries = new();
        private readonly List<ResidentChunk> initial = new();
        private readonly BlockAttributeTable attributes;
        private readonly BlockAppearanceTable appearance;
        private readonly TerrainRenderer renderer;
        private readonly TerrainRenderSettings settings;
        private readonly ChunkSlotGrid grid;
        private ResidentChunkStore store;
        //chunks waiting to be meshed, bucketed by how many chunks away from the camera they are, so
        //picking the next one is a walk of a few buckets rather than of every resident chunk. A linear
        //scan per free worker per tick is invisible at a few hundred chunks and is not at a few thousand.
        private const int Buckets = 256;
        private readonly List<Entry>[] ready = new List<Entry>[Buckets];
        private int3 bucketAnchor;
        private bool anchored;
        private int queuedCount, pendingCount;
        public int ResidentCount => entries.Count;
        public int BuiltCount { get; private set; }
        public int StaleResults { get; private set; }
        public bool IsCurrent => queuedCount == 0 && pendingCount == 0;

        public ChunkMeshScheduler(CompiledBlockContent content, TerrainRenderSettings settings, TerrainRenderer renderer, ChunkSlotGrid grid)
        {
            this.settings = settings; this.renderer = renderer; this.grid = grid;
            attributes = content.Registry.CreateAttributeTable(Allocator.Persistent);
            appearance = BlockAppearanceTable.Create(content, Allocator.Persistent);
            workers = new Worker[settings.MeshWorkers];
            for (int i = 0; i < workers.Length; i++) workers[i] = new Worker();
            for (int i = 0; i < ready.Length; i++) ready[i] = new List<Entry>();
        }

        public void Tick(ResidentChunkStore current, float3 cameraPosition)
        {
            if (current != null && current.IsDisposed) current = null;
            if (!ReferenceEquals(store, current))
            {
                if (store != null) { store.ReplicaChanged -= OnChanged; store.ReplicaRemoved -= OnRemoved; store.ReplicasReset -= OnReset; }
                OnReset(); store = current;
                if (store != null)
                {
                    store.ReplicaChanged += OnChanged; store.ReplicasReset += OnReset;
                    store.ReplicaRemoved += OnRemoved;
                    store.GetReplicaStamps(initial);
                    //adopting a populated store says nothing about what changed, so rebuild every face.
                    foreach (var stamp in initial) OnChanged(stamp.Address, AllFaces);
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
                    Settle(entry); worker.Entry = null; StaleResults++; continue;
                }
                if (!renderer.TryPublish(entry.Slot, entry.Address.Position, worker.Output.AsArray(), new ChunkFaceConnectivity(worker.Visibility.Value))) continue;
                if (entry.Built == 0) BuiltCount++;
                entry.Built = worker.Version; Settle(entry); worker.Entry = null;
            }
            if (store == null) { renderer.SetGraphReady(false); return; }
            //the queue is ordered against the camera's chunk, so it only has to be rebuilt when the
            //camera crosses a chunk boundary rather than every frame the camera moves at all.
            var anchor = (int3)math.floor(cameraPosition / ChunkLayout.Edge);
            if (!anchored || !anchor.Equals(bucketAnchor)) Rebucket(anchor);
            foreach (var worker in workers)
            {
                if (worker.Entry != null) continue;
                var best = TakeNearest();
                if (best == null) break;
                Schedule(worker, best);
            }
            renderer.SetGraphReady(store.DataReady && BuiltCount == entries.Count && IsCurrent);
        }

        //---------------------------------------------------------------- ready queue

        private int BucketOf(Entry entry)
        {
            int3 delta = entry.Address.Position - bucketAnchor;
            int distance = (int)math.round(math.length((float3)delta));
            return math.clamp(distance, 0, Buckets - 1);
        }

        private void Enqueue(Entry entry)
        {
            if (entry.Bucket >= 0 || entry.Pending || entry.Built == entry.Dirty) return;
            int bucket = anchored ? BucketOf(entry) : 0;
            entry.Bucket = bucket; entry.BucketSlot = ready[bucket].Count;
            ready[bucket].Add(entry);
            queuedCount++;
        }

        private void Remove(Entry entry)
        {
            if (entry.Bucket < 0) return;
            var list = ready[entry.Bucket];
            int slot = entry.BucketSlot, last = list.Count - 1;
            list[slot] = list[last];
            list[slot].BucketSlot = slot;
            list.RemoveAt(last);
            entry.Bucket = -1; entry.BucketSlot = -1;
            queuedCount--;
        }

        private Entry TakeNearest()
        {
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                var list = ready[bucket];
                if (list.Count == 0) continue;
                var entry = list[list.Count - 1];
                Remove(entry);
                return entry;
            }
            return null;
        }

        private void Rebucket(int3 anchor)
        {
            rebucketing.Clear();
            for (int bucket = 0; bucket < Buckets; bucket++)
            {
                var list = ready[bucket];
                for (int i = 0; i < list.Count; i++) { rebucketing.Add(list[i]); list[i].Bucket = -1; list[i].BucketSlot = -1; }
                list.Clear();
            }
            queuedCount = 0;
            bucketAnchor = anchor; anchored = true;
            foreach (var entry in rebucketing) Enqueue(entry);
        }

        private readonly List<Entry> rebucketing = new();

        //a chunk stops waiting once its worker finishes, and joins the queue again if it was dirtied
        //while it was out. An entry the store has since dropped simply leaves.
        private void Settle(Entry entry)
        {
            if (entry.Pending) { entry.Pending = false; pendingCount--; }
            if (entries.TryGetValue(entry.Address, out var active) && ReferenceEquals(active, entry)) Enqueue(entry);
        }

        private void Schedule(Worker worker, Entry entry)
        {
            worker.Present = 0; worker.Handle = default;
            worker.Store = store; worker.Epoch = store.InterestEpoch; worker.Version = entry.Dirty;
            var emptyView = air.AsReadOnly();
            for (int i = 0; i < 7; i++)
            {
                worker.Views[i] = emptyView;
                var address = Neighbor(entry.Address, i);
                if (!store.TryGetReplicaStamp(address, out worker.Stamps[i])) continue;
                if (!store.TryGetReplicaSolidView(address, out worker.Views[i], ref worker.Handle)) { worker.Views[i] = emptyView; continue; }
                worker.Present |= 1 << i;
            }
            var padding = new PaddingJob
            {
                Center = worker.Views[0], Down = worker.Views[1], Up = worker.Views[2], North = worker.Views[3],
                South = worker.Views[4], West = worker.Views[5], East = worker.Views[6], Present = worker.Present, Output = worker.Padded
            }.Schedule(GreedyMesherJob.PaddedVolume, 256, worker.Handle);
            //every source has to fence the padding job, or a chunk could be replaced while it reads.
            for (int i = 0; i < 7; i++)
                if ((worker.Present & (1 << i)) != 0) store.RegisterReplicaReader(Neighbor(entry.Address, i), padding);
            var mesh = new GreedyMesherJob
            {
                Voxels = worker.Padded, Mask = worker.Mask, Output = worker.Output, Appearance = appearance.AsReadOnly(),
                Attributes = attributes.AsReadOnly().Solids, ChunkPosition = entry.Address.Position, Slot = entry.Slot,
                Seed = settings.VisualSeed ^ entry.Address.World
            }.Schedule(padding);
            var visibility = new ChunkVisibilityJob
            {
                Voxels = worker.Padded, Attributes = attributes.AsReadOnly().Solids, Visited = worker.VisibilityVisited,
                Queue = worker.VisibilityQueue, Result = worker.Visibility
            }.Schedule(padding);
            worker.Handle = JobHandle.CombineDependencies(mesh, visibility);
            worker.Entry = entry; entry.Pending = true; pendingCount++;
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

        private const byte AllFaces = 0x3F;

        private void OnChanged(ChunkAddress address, byte changedFaces)
        {
            renderer.SetGraphReady(false);
            if (!entries.TryGetValue(address, out var entry))
            {
                //no allocation and nothing that can run out: a chunk's slot is a function of where it
                //is, and the chunk that left the volume already gave this one up.
                entry = new Entry { Address = address, Slot = (uint)grid.SlotOf(address.Position) };
                entries.Add(address, entry);
            }
            Dirty(entry);
            //a neighbour's geometry depends only on the plane facing it, so an unchanged plane needs no rebuild.
            //During a radial load this keeps each chunk from being remeshed once per arriving neighbour.
            for (int i = 1; i < 7; i++)
                if ((changedFaces & (1 << (i - 1))) != 0 && entries.TryGetValue(Neighbor(address, i), out var dirty))
                    Dirty(dirty);
        }

        private void Dirty(Entry entry)
        {
            entry.Dirty++;
            Enqueue(entry);
        }
        private void OnReset()
        {
            entries.Clear(); BuiltCount = 0; renderer.SetGraphReady(false); renderer.ClearMeshes();
            foreach (var list in ready) list.Clear();
            queuedCount = 0; anchored = false;
            //workers still running belong to the store that just went away. Their results are rejected
            //on completion, but each still settles, so the count has to keep expecting them.
            pendingCount = 0;
            foreach (var worker in workers) if (worker.Entry != null) pendingCount++;
        }

        private void OnRemoved(ChunkAddress address)
        {
            if (!entries.Remove(address, out var removed)) return;
            Remove(removed);
            renderer.SetGraphReady(false);
            renderer.Remove(address.Position);
            if (removed.Built != 0) BuiltCount--;
            //the departed chunk's planes are gone with it, so every neighbour re-pads that side as air.
            for (int i = 1; i < 7; i++)
                if (entries.TryGetValue(Neighbor(address, i), out var dirty)) Dirty(dirty);
        }
        private static ChunkAddress Neighbor(ChunkAddress address, int index)
        {
            if (index == 0) return address;
            FaceBasis.Get((BlockFace)(index - 1), out var normal, out _, out _);
            return new ChunkAddress(address.World, address.Position + normal);
        }
        public void Dispose()
        {
            if (store != null) { store.ReplicaChanged -= OnChanged; store.ReplicaRemoved -= OnRemoved; store.ReplicasReset -= OnReset; }
            foreach (var worker in workers) worker.Dispose();
            air.Dispose();
            appearance.Dispose(); attributes.Dispose(); entries.Clear();
        }

        [BurstCompile]
        private struct PaddingJob : IJobParallelFor
        {
            [ReadOnly] public PaletteChannel.ReadView Center, Down, Up, North, South, West, East;
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
                    case 0: Output[index] = Center.Get(cell); break;
                    case 1: Output[index] = Down.Get(cell); break;
                    case 2: Output[index] = Up.Get(cell); break;
                    case 3: Output[index] = North.Get(cell); break;
                    case 4: Output[index] = South.Get(cell); break;
                    case 5: Output[index] = West.Get(cell); break;
                    default: Output[index] = East.Get(cell); break;
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
