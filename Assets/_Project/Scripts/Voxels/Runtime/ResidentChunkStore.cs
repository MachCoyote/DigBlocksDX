using System;
using System.Collections.Generic;
using System.Threading;
using DigBlocks.ChunkProtocol;
using Unity.Entities;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Runtime
{
    public enum ChunkLoadState { Loaded, Pending }

    public struct ResidentChunk : IComponentData
    { public ChunkAddress Address; public ulong Incarnation, Revision; }

    public sealed class ChunkLease : IDisposable
    {
        internal readonly ResidentChunkStore Owner;
        internal readonly ChunkData Data;
        internal bool Released;
        public Entity Entity { get; }
        public ChunkAddress Address => Data.Address;
        public ulong Incarnation => Data.Incarnation;
        public ulong Revision { get { Owner.Validate(this); return Data.Revision; } }
        internal ChunkLease(ResidentChunkStore owner, ChunkData data, Entity entity)
        { Owner = owner; Data = data; Entity = entity; }
        public uint SolidAt(int index) { Owner.Validate(this); return Data.SolidAt(index); }
        public uint FluidAt(int index) { Owner.Validate(this); return Data.FluidAt(index); }
        public void Apply(CellEdit[] edits) => Owner.Apply(this, edits);
        public void Dispose() { if (Released) return; Owner.Release(this); Released = true; }
    }

    //control-plane owner; callers use leases, while workers receive only detached immutable images.
    public sealed class ResidentChunkStore : IDisposable
    {
        private readonly EntityManager manager;
        private readonly BlockRegistry registry;
        private readonly int maxResidents, ownerThread;
        private readonly Dictionary<ChunkAddress, Entry> chunks = new();
        //encoding and publishing are both a palette copy and a word copy now, and both are strictly
        //sequential on the owning thread, so one scratch pair each serves every chunk.
        private readonly PackedChannelData encodeSolids = new(), encodeFluids = new();
        private readonly PackedChannelData publishSolids = new(), publishFluids = new();
        private readonly Dictionary<ChunkAddress, PendingLoad> pendingLoads = new();
        private readonly Stack<PendingLoad> loadPool = new();
        private readonly List<ChunkAddress> completedLoads = new();
        private readonly int maxConcurrentLoads;
        private ulong nextIncarnation = 1;
        private bool disposed;
        private bool replicas;
        private ChunkInterest interest;
        private const int HistoryRevisions = 8, HistoryCells = 4096;
        //one chunk being generated on a worker. The scratch travels with it, so a steady load recycles a
        //bounded set instead of allocating a quarter of a megabyte per chunk.
        private sealed class PendingLoad
        {
            public ChunkAddress Address;
            public ulong Incarnation;
            public readonly uint[] Solids = new uint[ChunkLayout.Volume];
            public readonly uint[] Fluids = new uint[ChunkLayout.Volume];
            public readonly PackedChannelData PackedSolids = new(), PackedFluids = new();
            public volatile bool Done;
            public Exception Error;
        }

        private sealed class Entry
        {
            public ChunkData Data; public Entity Entity; public int Leases, HistoryCellCount;
            public bool Loaded;
            public ulong[] FaceHashes;
            public readonly Queue<ChunkDelta> History = new();
        }
        internal ResidentChunkStore(EntityManager manager, BlockRegistry registry, int maxResidents, int maxConcurrentLoads)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (maxResidents < 1 || maxResidents > 65536) throw new ArgumentOutOfRangeException(nameof(maxResidents));
            if (maxConcurrentLoads < 1 || maxConcurrentLoads > 64) throw new ArgumentOutOfRangeException(nameof(maxConcurrentLoads));
            this.manager = manager; this.registry = registry; this.maxResidents = maxResidents;
            this.maxConcurrentLoads = maxConcurrentLoads;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        public int Count => chunks.Count;
        public bool IsDisposed => disposed;
        //the second argument is a bitmask of BlockFace bits whose boundary plane changed. A neighbour's
        //mesh depends only on the plane facing it, so a clear bit means that neighbour needs no rebuild.
        public event Action<ChunkAddress, byte> ReplicaChanged;
        public event Action<ChunkAddress> ReplicaRemoved;
        public event Action ReplicasReset;

        public bool TryGetReplicaStamp(ChunkAddress address, out ResidentChunk stamp)
        {
            RequireAlive(); stamp = default;
            if (!replicas || !chunks.TryGetValue(address, out var entry)) return false;
            stamp = new ResidentChunk { Address = address, Incarnation = entry.Data.Incarnation, Revision = entry.Data.Revision };
            return true;
        }

        public void GetReplicaStamps(List<ResidentChunk> destination)
        {
            RequireAlive();
            destination.Clear();
            if (!replicas) return;
            foreach (var pair in chunks)
                destination.Add(new ResidentChunk { Address = pair.Key, Incarnation = pair.Value.Data.Incarnation, Revision = pair.Value.Data.Revision });
        }

        /// <summary>
        /// Hands out a job-safe read of the replica's packed solids and folds its outstanding writers
        /// into <paramref name="dependency"/>. The caller registers the job it schedules through
        /// <see cref="RegisterReplicaReader"/>. Meshing reads the stored layout directly rather than
        /// expanding seven whole chunks to reach one chunk and six boundary planes.
        /// </summary>
        public bool TryGetReplicaSolidView(ChunkAddress address, out PaletteChannel.ReadView view, ref JobHandle dependency)
        {
            RequireAlive(); view = default;
            if (!replicas || !chunks.TryGetValue(address, out var entry)) return false;
            view = entry.Data.SolidView();
            dependency = JobHandle.CombineDependencies(dependency, entry.Data.Readers);
            return true;
        }

        public void RegisterReplicaReader(ChunkAddress address, JobHandle reader)
        {
            RequireAlive();
            if (replicas && chunks.TryGetValue(address, out var entry)) entry.Data.AddReader(reader);
        }
        /// <summary>
        /// Encodes the leased chunk in the layout it is already stored in. This used to hand a 256 KiB
        /// expansion to a worker thread and pick the result up a tick or more later; the copy is now
        /// small enough that the latency cost far outweighed the thread.
        /// </summary>
        /// <summary>
        /// Copies the leased chunk out in the layout it is stored in, and reports the revision copied.
        /// This is what single-player direct delivery hands to the client store.
        /// </summary>
        public ulong CopyPacked(ChunkLease lease, PackedChannelData solids, PackedChannelData fluids)
        {
            Validate(lease);
            if (solids == null) throw new ArgumentNullException(nameof(solids));
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            lease.Data.CopyPacked(solids, fluids);
            return lease.Data.Revision;
        }

        public byte[] EncodeSnapshot(ChunkLease lease)
        {
            Validate(lease);
            lease.Data.CopyPacked(encodeSolids, encodeFluids);
            return ChunkWireCodec.EncodeSnapshot(lease.Address, lease.Incarnation, lease.Data.Revision,
                encodeSolids, encodeFluids);
        }
        public ChunkLease Acquire(ChunkAddress address)
        {
            RequireAlive();
            if (replicas) throw new InvalidOperationException("Replica stores cannot acquire authoritative leases.");
            if (!chunks.TryGetValue(address, out var entry))
            {
                if (chunks.Count >= maxResidents) throw new InvalidOperationException("Resident chunk capacity exhausted.");
                entry = CreateEntry(address);
                chunks.Add(address, entry);
            }
            if (entry.Leases == int.MaxValue) throw new InvalidOperationException("Residency lease count exhausted.");
            entry.Leases++;
            return new ChunkLease(this, entry.Data, entry.Entity);
        }
        private Entry CreateEntry(ChunkAddress address)
        {
            if (nextIncarnation == ulong.MaxValue) throw new InvalidOperationException("Chunk incarnation space exhausted.");
            var data = new ChunkData(address, nextIncarnation++);
            Entity entity = Entity.Null;
            try
            {
                entity = manager.CreateEntity(typeof(ResidentChunk));
                manager.SetComponentData(entity, new ResidentChunk { Address = address, Incarnation = data.Incarnation, Revision = data.Revision });
                return new Entry { Data = data, Entity = entity };
            }
            catch { data.Dispose(); if (entity != Entity.Null && manager.Exists(entity)) manager.DestroyEntity(entity); throw; }
        }

        public bool TryReplaceLeases(ChunkLease[] current, ChunkAddress[] wanted, out ChunkLease[] replacement)
        {
            RequireAlive(); replacement = null;
            if (replicas) throw new InvalidOperationException("Replica stores cannot acquire authoritative leases.");
            if (current == null || wanted == null || current.Length > ChunkInterest.MaximumChunks || wanted.Length > ChunkInterest.MaximumChunks)
                throw new ArgumentException("Invalid subscription size.");
            var old = new Dictionary<ChunkAddress, ChunkLease>();
            foreach (var lease in current) { Validate(lease); old.Add(lease.Address, lease); }
            var desired = new HashSet<ChunkAddress>(wanted);
            if (desired.Count != wanted.Length) throw new ArgumentException("Duplicate subscription address.");
            int finalCount = chunks.Count;
            foreach (var pair in old)
                if (!desired.Contains(pair.Key) && chunks[pair.Key].Leases == 1) finalCount--;
            foreach (var address in wanted)
            {
                if (!chunks.TryGetValue(address, out var entry)) finalCount++;
                else if (!old.ContainsKey(address) && entry.Leases == int.MaxValue) throw new InvalidOperationException("Lease count exhausted.");
            }
            if (finalCount > maxResidents) return false;
            //stage bounded new allocations before releasing old leases; capacity rejection leaves interest untouched.
            var staged = new Dictionary<ChunkAddress, Entry>();
            var result = new ChunkLease[wanted.Length];
            try
            {
                for (int i = 0; i < wanted.Length; i++)
                {
                    var address = wanted[i];
                    if (old.TryGetValue(address, out var lease)) { result[i] = lease; continue; }
                    if (!chunks.TryGetValue(address, out var entry)) { entry = CreateEntry(address); staged.Add(address, entry); }
                    result[i] = new ChunkLease(this, entry.Data, entry.Entity);
                }
            }
            catch
            {
                foreach (var entry in staged.Values) { entry.Data.Dispose(); manager.DestroyEntity(entry.Entity); }
                throw;
            }
            foreach (var pair in old) if (!desired.Contains(pair.Key)) pair.Value.Dispose();
            foreach (var pair in staged) chunks.Add(pair.Key, pair.Value);
            for (int i = 0; i < wanted.Length; i++) if (!old.ContainsKey(wanted[i])) chunks[wanted[i]].Leases++;
            replacement = result;
            return true;
        }

        public bool TryGetDelta(ChunkLease lease, ulong baseline, out ChunkDelta delta)
        {
            Validate(lease); delta = null;
            if (baseline == 0 || baseline >= lease.Revision) return false;
            var updates = new Dictionary<int, ChunkCellUpdate>();
            ulong revision = baseline;
            foreach (var item in chunks[lease.Address].History)
            {
                if (item.ResultRevision <= revision) continue;
                if (item.BaseRevision != revision) return false;
                foreach (var update in item.Updates) updates[update.Index] = update;
                revision = item.ResultRevision;
            }
            if (revision != lease.Revision) return false;
            var cells = new List<ChunkCellUpdate>(updates.Values);
            cells.Sort((a, b) => a.Index.CompareTo(b.Index));
            delta = new ChunkDelta(lease.Address, lease.Incarnation, baseline, revision, cells);
            return true;
        }

        public bool DataReady => replicas && interest != null && chunks.Count == interest.Count;
        public ulong InterestEpoch => interest?.Epoch ?? 0;

        public void EnableReplicas()
        {
            RequireAlive();
            if (chunks.Count != 0) throw new InvalidOperationException("Replica mode requires an empty store.");
            replicas = true;
        }

        public bool SetReplicaInterest(ChunkInterest next)
        {
            RequireAlive();
            if (!replicas) throw new InvalidOperationException("Not a replica store.");
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (next.Count > maxResidents) throw new ArgumentException("Interest exceeds replica capacity.");
            if (interest != null && next.Epoch <= interest.Epoch) return false;
            interest = next;
            var removed = new List<ChunkAddress>();
            foreach (var pair in chunks)
                if (!next.Contains(pair.Key)) removed.Add(pair.Key);
            foreach (var address in removed)
            {
                var entry = chunks[address];
                entry.Data.Dispose();
                if (manager.Exists(entry.Entity)) manager.DestroyEntity(entry.Entity);
                chunks.Remove(address);
                ReplicaRemoved?.Invoke(address);
            }
            return true;
        }

        /// <summary>
        /// Starts generating the leased chunk if it is not resident yet, and reports whether it can be
        /// read now. Generation runs on a worker; PumpLoads adopts the result. A caller told Pending
        /// should move on and come back rather than wait.
        /// </summary>
        public ChunkLoadState RequestLoad(ChunkLease lease, IAuthoritativeChunkSource source)
        {
            Validate(lease);
            var entry = chunks[lease.Address];
            if (entry.Loaded) return ChunkLoadState.Loaded;
            if (source == null) { entry.Loaded = true; Stamp(entry, lease.Address); return ChunkLoadState.Loaded; }
            if (pendingLoads.ContainsKey(lease.Address)) return ChunkLoadState.Pending;
            //bounded so a wide interest cannot put thousands of generations in flight at once, each
            //holding its own chunk of scratch.
            if (pendingLoads.Count >= maxConcurrentLoads) return ChunkLoadState.Pending;
            var load = Rent(lease);
            pendingLoads.Add(lease.Address, load);
            //a rejected queue means the pool is saturated, so run it here rather than lose the chunk.
            if (!ThreadPool.QueueUserWorkItem(_ => Generate(load, source))) Generate(load, source);
            return ChunkLoadState.Pending;
        }

        /// <summary>Generates the leased chunk on the calling thread and adopts it before returning.</summary>
        public void EnsureLoaded(ChunkLease lease, IAuthoritativeChunkSource source)
        {
            Validate(lease);
            var entry = chunks[lease.Address];
            if (entry.Loaded) return;
            if (source == null) { entry.Loaded = true; Stamp(entry, lease.Address); return; }
            if (pendingLoads.ContainsKey(lease.Address))
                throw new InvalidOperationException("The chunk is already generating on a worker.");
            var load = Rent(lease);
            try { Generate(load, source); Adopt(load); }
            finally { loadPool.Push(load); }
        }

        private PendingLoad Rent(ChunkLease lease)
        {
            var load = loadPool.Count > 0 ? loadPool.Pop() : new PendingLoad();
            load.Address = lease.Address; load.Incarnation = lease.Incarnation;
            load.Error = null; load.Done = false;
            //a source fills only what it means to place, so the rest has to arrive as air.
            Array.Clear(load.Solids, 0, ChunkLayout.Volume);
            Array.Clear(load.Fluids, 0, ChunkLayout.Volume);
            return load;
        }

        //runs on a worker: no Unity APIs, no store access, no native containers.
        private static void Generate(PendingLoad load, IAuthoritativeChunkSource source)
        {
            try
            {
                source.Generate(load.Address, load.Solids, load.Fluids);
                PackedChannelData.Pack(load.Solids, load.PackedSolids);
                PackedChannelData.Pack(load.Fluids, load.PackedFluids);
            }
            catch (Exception exception) { load.Error = exception; }
            finally { load.Done = true; }
        }

        public void PumpLoads()
        {
            if (disposed || pendingLoads.Count == 0) return;
            RequireAlive();
            completedLoads.Clear();
            foreach (var pair in pendingLoads) if (pair.Value.Done) completedLoads.Add(pair.Key);
            foreach (var address in completedLoads)
            {
                var load = pendingLoads[address];
                pendingLoads.Remove(address);
                try { Adopt(load); }
                finally { loadPool.Push(load); }
            }
        }

        //a chunk released or re-entered while its generation was in flight is simply dropped.
        private void Adopt(PendingLoad load)
        {
            if (load.Error != null) throw new InvalidOperationException($"Generating {load.Address} failed.", load.Error);
            if (!chunks.TryGetValue(load.Address, out var entry)) return;
            if (entry.Loaded || entry.Data.Incarnation != load.Incarnation) return;
            ValidateStates(load.PackedSolids, true);
            ValidateStates(load.PackedFluids, false);
            RequirePermittedFluids(load.PackedSolids, load.PackedFluids);
            entry.Data.LoadGenerated(load.PackedSolids, load.PackedFluids);
            entry.Loaded = true;
            Stamp(entry, load.Address);
        }

        private void Stamp(Entry entry, ChunkAddress address) => manager.SetComponentData(entry.Entity,
            new ResidentChunk { Address = address, Incarnation = entry.Data.Incarnation, Revision = entry.Data.Revision });

        public void ClearReplicas()
        {
            if (disposed) return;
            RequireAlive();
            if (!replicas) throw new InvalidOperationException("Not a replica store.");
            foreach (var entry in chunks.Values)
            { entry.Data.Dispose(); if (manager.Exists(entry.Entity)) manager.DestroyEntity(entry.Entity); }
            chunks.Clear(); interest = null;
            ReplicasReset?.Invoke();
        }

        public bool PublishReplica(ulong epoch, ChunkImage image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            PackedChannelData.Pack(image.Solids, publishSolids);
            PackedChannelData.Pack(image.Fluids, publishFluids);
            return PublishReplica(epoch, image.Address, image.Incarnation, image.Revision, publishSolids, publishFluids);
        }

        //takes the chunk in the layout the wire delivered it in, so publishing is a copy rather than a
        //re-pack, and validation reads a palette of a few hundred entries instead of all 32,768 cells.
        public bool PublishReplica(ulong epoch, ChunkAddress address, ulong incarnation, ulong revision,
            PackedChannelData solids, PackedChannelData fluids)
        {
            RequireAlive();
            if (!replicas) throw new InvalidOperationException("Not a replica store.");
            if (incarnation == 0) throw new ArgumentOutOfRangeException(nameof(incarnation));
            if (revision == 0) throw new ArgumentOutOfRangeException(nameof(revision));
            if (solids == null) throw new ArgumentNullException(nameof(solids));
            if (fluids == null) throw new ArgumentNullException(nameof(fluids));
            if (interest == null || epoch != interest.Epoch || !interest.Contains(address)) return false;
            chunks.TryGetValue(address, out var previous);
            //A skipped interest declaration can hide a server-side eviction and re-entry from the
            //client. A full publication from the live epoch is authoritative, so a new incarnation
            //replaces the retained replica even when its revision sequence restarted. Revision
            //ordering remains strict within one incarnation.
            if (previous != null && previous.Data.Incarnation == incarnation && previous.Data.Revision > revision) return false;
            ValidateStates(solids, true);
            ValidateStates(fluids, false);
            RequirePermittedFluids(solids, fluids);
            if (previous != null && previous.Data.Incarnation == incarnation && previous.Data.Revision == revision)
            {
                for (int i = 0; i < ChunkLayout.Volume; i++)
                    if (previous.Data.SolidAt(i) != solids.Get(i) || previous.Data.FluidAt(i) != fluids.Get(i))
                        throw new ArgumentException("Conflicting replica at the published revision.");
                return true;
            }
            //an absent neighbour is padded as air, so a first arrival compares against the empty plane
            //instead of counting as a change on every face.
            var faces = new ulong[6];
            byte changed = 0;
            ComputeFaceHashes(solids, faces);
            for (int face = 0; face < 6; face++)
            {
                ulong before = previous?.FaceHashes == null ? EmptyFaceHash : previous.FaceHashes[face];
                if (before != faces[face]) changed |= (byte)(1 << face);
            }
            //build detached replacement first; failed validation/import leaves the published entity intact.
            var data = ChunkData.FromPacked(address, incarnation, revision, solids, fluids);
            Entity entity = previous?.Entity ?? Entity.Null;
            try
            {
                if (entity == Entity.Null) entity = manager.CreateEntity(typeof(ResidentChunk));
                manager.SetComponentData(entity, new ResidentChunk { Address = address, Incarnation = incarnation, Revision = revision });
                chunks[address] = new Entry { Data = data, Entity = entity, FaceHashes = faces };
            }
            catch
            {
                data.Dispose();
                if (previous == null && entity != Entity.Null && manager.Exists(entity)) manager.DestroyEntity(entity);
                throw;
            }
            previous?.Data.Dispose();
            ReplicaChanged?.Invoke(address, changed);
            return true;
        }

        //Direct storage carries values in its cells and has no palette to check, but it only appears
        //when a chunk holds more distinct states than a palette may hold, which content never does.
        private void ValidateStates(PackedChannelData channel, bool solid)
        {
            if (channel.Storage == ChannelStorage.Direct)
            {
                for (int i = 0; i < ChunkLayout.Volume; i++) Probe(channel.Get(i), solid);
                return;
            }
            for (int i = 0; i < channel.PaletteCount; i++) Probe(channel.Palette[i], solid);
        }

        private void Probe(uint state, bool solid)
        {
            if (solid) registry.GetSolid(state); else registry.GetFluid(state);
        }

        //the only inherently per-cell check. A chunk holding no fluid at all, which is nearly all of
        //them, skips it outright.
        private void RequirePermittedFluids(PackedChannelData solids, PackedChannelData fluids)
        {
            if (fluids.Storage == ChannelStorage.Uniform && fluids.Palette[0] == 0) return;
            for (int i = 0; i < ChunkLayout.Volume; i++)
                if (fluids.Get(i) != 0 && !registry.GetSolid(solids.Get(i)).PermitsFluid)
                    throw new ArgumentException("Chunk solid does not permit fluid.");
        }

        //solid values on one boundary plane, in the order ChunkMeshScheduler pads its neighbours.
        //Fluids are not meshed, so they cannot change a neighbour and are deliberately excluded.
        private static readonly ulong EmptyFaceHash = EmptyPlaneHash();

        private static ulong EmptyPlaneHash()
        {
            ulong hash = 14695981039346656037;
            for (int i = 0; i < ChunkLayout.Edge * ChunkLayout.Edge; i++) hash = (hash ^ 0u) * 1099511628211;
            return hash;
        }

        private static void ComputeFaceHashes(PackedChannelData solids, ulong[] destination)
        {
            for (int face = 0; face < 6; face++)
            {
                ulong hash = 14695981039346656037;
                for (int a = 0; a < ChunkLayout.Edge; a++)
                for (int b = 0; b < ChunkLayout.Edge; b++)
                {
                    int x, y, z;
                    switch (face)
                    {
                        case 0: x = a; y = 0; z = b; break;
                        case 1: x = a; y = ChunkLayout.Edge - 1; z = b; break;
                        case 2: x = a; y = b; z = ChunkLayout.Edge - 1; break;
                        case 3: x = a; y = b; z = 0; break;
                        case 4: x = 0; y = b; z = a; break;
                        default: x = ChunkLayout.Edge - 1; y = b; z = a; break;
                    }
                    hash = (hash ^ solids.Get(ChunkLayout.Index(new int3(x, y, z)))) * 1099511628211;
                }
                destination[face] = hash;
            }
        }

        public bool TryCaptureReplica(ChunkAddress address, out ChunkCapture capture)
        {
            RequireAlive(); capture = null;
            if (!replicas || !chunks.TryGetValue(address, out var entry)) return false;
            capture = entry.Data.Capture(); return true;
        }

        public bool TryReadReplica(ChunkAddress address, out ChunkImage image)
        {
            image = null;
            if (!TryCaptureReplica(address, out var pending)) return false;
            using var capture = pending;
            image = ChunkImage.FromOwnedChannels(address, capture.Incarnation, capture.Revision, capture.CopySolids(), capture.CopyFluids());
            return true;
        }

        internal void Validate(ChunkLease lease)
        {
            RequireAlive();
            if (lease == null || lease.Owner != this) throw new ArgumentException("Lease belongs to another store.", nameof(lease));
            if (lease.Released) throw new ObjectDisposedException(nameof(ChunkLease));
        }
        internal void Apply(ChunkLease lease, CellEdit[] edits)
        {
            Validate(lease);
            if (edits == null) throw new ArgumentNullException(nameof(edits));
            ValidateEdits(edits);
            ulong baseline = lease.Data.Revision;
            lease.Data.Apply(edits, registry.MaxSolidStateId, registry.MaxFluidStateId);
            chunks[lease.Address].Loaded = true;
            if (lease.Data.Revision != baseline)
            {
                var entry = chunks[lease.Address];
                if (edits.Length > HistoryCells) { entry.History.Clear(); entry.HistoryCellCount = 0; }
                else
                {
                    var updates = new ChunkCellUpdate[edits.Length];
                    for (int i = 0; i < edits.Length; i++) updates[i] = new ChunkCellUpdate(edits[i].Index, edits[i].Solid, edits[i].Fluid);
                    entry.History.Enqueue(new ChunkDelta(lease.Address, lease.Incarnation, baseline, lease.Data.Revision, updates));
                    entry.HistoryCellCount += updates.Length;
                    while (entry.History.Count > HistoryRevisions || entry.HistoryCellCount > HistoryCells)
                        entry.HistoryCellCount -= entry.History.Dequeue().Updates.Count;
                }
            }
            manager.SetComponentData(lease.Entity, new ResidentChunk { Address = lease.Address, Incarnation = lease.Incarnation, Revision = lease.Data.Revision });
        }

        private void ValidateEdits(CellEdit[] edits)
        {
            foreach (var edit in edits)
            {
                var solid = registry.GetSolid(edit.Solid); registry.GetFluid(edit.Fluid);
                if (edit.Fluid != 0 && !solid.PermitsFluid) throw new ArgumentException("Solid state does not permit fluid.", nameof(edits));
            }
        }
        internal void Release(ChunkLease lease)
        {
            if (disposed) return;
            Validate(lease);
            var entry = chunks[lease.Address];
            if (--entry.Leases != 0) return;
            entry.Data.Dispose();
            if (manager.Exists(entry.Entity)) manager.DestroyEntity(entry.Entity);
            chunks.Remove(lease.Address);
        }
        public void Dispose()
        {
            if (disposed) return;
            RequireAlive();
            foreach (var entry in chunks.Values)
            { entry.Data.Dispose(); if (manager.Exists(entry.Entity)) manager.DestroyEntity(entry.Entity); }
            chunks.Clear(); disposed = true;
            ReplicasReset?.Invoke();
        }
        private void RequireAlive()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ResidentChunkStore));
            if (Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Chunk ownership is main-thread-only.");
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial class ChunkWorldSystem : SystemBase
    {
        public ResidentChunkStore Store { get; private set; }
        //each concurrent load holds a chunk of generation scratch and its packed result, roughly
        //640 KiB, so this trades memory for how many chunks a tick can start generating at once.
        public ResidentChunkStore Configure(BlockRegistry registry, int maxResidents = 256, int maxConcurrentLoads = 32)
        {
            if (Store != null) throw new InvalidOperationException("World already has a chunk store.");
            return Store = new ResidentChunkStore(EntityManager, registry, maxResidents, maxConcurrentLoads);
        }
        public void ReleaseStore() { Store?.Dispose(); Store = null; }
        protected override void OnUpdate() => Store?.PumpLoads();
        protected override void OnDestroy() => ReleaseStore();
    }
}
