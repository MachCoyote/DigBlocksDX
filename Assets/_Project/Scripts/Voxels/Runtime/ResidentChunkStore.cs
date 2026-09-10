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
    public struct ResidentChunk : IComponentData
    { public ChunkAddress Address; public ulong Incarnation, Revision; }

    public sealed class SnapshotResult
    {
        public ulong RequestId { get; internal set; }
        public ChunkAddress Address { get; internal set; }
        public ulong Incarnation { get; internal set; }
        public ulong Revision { get; internal set; }
        public byte[] Payload { get; internal set; }
        public Exception Error { get; internal set; }
    }

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
        private readonly int maxResidents, maxSnapshots, ownerThread;
        private readonly Dictionary<ChunkAddress, Entry> chunks = new();
        private readonly List<Pending> snapshots = new();
        private ulong nextIncarnation = 1, nextRequest = 1;
        private bool disposed;
        private bool replicas;
        private ChunkInterest interest;
        private const int HistoryRevisions = 8, HistoryCells = 4096;
        private sealed class Entry
        {
            public ChunkData Data; public Entity Entity; public int Leases, HistoryCellCount;
            public bool Loaded;
            public ulong[] FaceHashes;
            public readonly Queue<ChunkDelta> History = new();
        }
        private sealed class Pending
        {
            public ulong Id;
            public ChunkCapture Capture;
            public ChunkAddress Address;
            public ulong Incarnation, Revision;
            public bool Cancelled, Encoding;
            public readonly ManualResetEventSlim Done = new(false);
            public SnapshotResult Result;
        }

        internal ResidentChunkStore(EntityManager manager, BlockRegistry registry, int maxResidents, int maxSnapshots)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (maxResidents < 1 || maxResidents > 65536) throw new ArgumentOutOfRangeException(nameof(maxResidents));
            if (maxSnapshots < 1 || maxSnapshots > 64) throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
            this.manager = manager; this.registry = registry; this.maxResidents = maxResidents; this.maxSnapshots = maxSnapshots;
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

        public JobHandle ScheduleReplicaSolidCopy(ChunkAddress address, NativeArray<uint> destination, JobHandle dependency = default)
        {
            RequireAlive();
            if (!replicas || !chunks.TryGetValue(address, out var entry)) throw new InvalidOperationException("Replica is no longer resident.");
            return entry.Data.ScheduleSolidCopy(destination, dependency);
        }
        public int PendingSnapshots => snapshots.Count;
        public int ReadySnapshots
        {
            get
            {
                int count = 0;
                foreach (var item in snapshots) if (item.Encoding && item.Done.IsSet && IsLive(item)) count++;
                return count;
            }
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
            if (chunks.Count != 0 || snapshots.Count != 0) throw new InvalidOperationException("Replica mode requires an empty store.");
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

        public void EnsureLoaded(ChunkLease lease, IAuthoritativeChunkSource source)
        {
            Validate(lease);
            var entry = chunks[lease.Address];
            if (entry.Loaded) return;
            var edits = source?.LoadOrGenerate(lease.Address) ?? Array.Empty<CellEdit>();
            if (edits == null) throw new InvalidOperationException("Chunk source returned no result.");
            ValidateEdits(edits);
            entry.Data.Apply(edits, registry.MaxSolidStateId, registry.MaxFluidStateId);
            manager.SetComponentData(entry.Entity, new ResidentChunk
            {
                Address = lease.Address,
                Incarnation = lease.Incarnation,
                Revision = lease.Data.Revision
            });
            entry.Loaded = true;
        }

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
            RequireAlive();
            if (!replicas) throw new InvalidOperationException("Not a replica store.");
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (interest == null || epoch != interest.Epoch || !interest.Contains(image.Address)) return false;
            chunks.TryGetValue(image.Address, out var previous);
            if (previous != null && (previous.Data.Incarnation != image.Incarnation || previous.Data.Revision > image.Revision)) return false;
            for (int i = 0; i < ChunkLayout.Volume; i++)
            {
                var solid = registry.GetSolid(image.SolidAt(i)); registry.GetFluid(image.FluidAt(i));
                if (image.FluidAt(i) != 0 && !solid.PermitsFluid) throw new ArgumentException("Replica solid does not permit fluid.");
            }
            if (previous != null && previous.Data.Revision == image.Revision)
            {
                for (int i = 0; i < ChunkLayout.Volume; i++)
                    if (previous.Data.SolidAt(i) != image.SolidAt(i) || previous.Data.FluidAt(i) != image.FluidAt(i))
                        throw new ArgumentException("Conflicting replica at the published revision.");
                return true;
            }
            //an absent neighbour is padded as air, so a first arrival compares against the empty plane
            //instead of counting as a change on every face.
            var faces = new ulong[6];
            byte changed = 0;
            ComputeFaceHashes(image, faces);
            for (int face = 0; face < 6; face++)
            {
                ulong before = previous?.FaceHashes == null ? EmptyFaceHash : previous.FaceHashes[face];
                if (before != faces[face]) changed |= (byte)(1 << face);
            }
            //build detached replacement first; failed validation/import leaves the published entity intact.
            var data = ChunkData.FromChannels(image.Address, image.Incarnation, image.Revision, image.Solids, image.Fluids);
            Entity entity = previous?.Entity ?? Entity.Null;
            try
            {
                if (entity == Entity.Null) entity = manager.CreateEntity(typeof(ResidentChunk));
                manager.SetComponentData(entity, new ResidentChunk { Address = image.Address, Incarnation = image.Incarnation, Revision = image.Revision });
                chunks[image.Address] = new Entry { Data = data, Entity = entity, FaceHashes = faces };
            }
            catch
            {
                data.Dispose();
                if (previous == null && entity != Entity.Null && manager.Exists(entity)) manager.DestroyEntity(entity);
                throw;
            }
            previous?.Data.Dispose();
            ReplicaChanged?.Invoke(image.Address, changed);
            return true;
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

        private static void ComputeFaceHashes(ChunkImage image, ulong[] destination)
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
                    hash = (hash ^ image.SolidAt(ChunkLayout.Index(new int3(x, y, z)))) * 1099511628211;
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
            foreach (var item in snapshots) if (item.Address.Equals(lease.Address) && item.Incarnation == lease.Incarnation) item.Cancelled = true;
        }
        public bool TryRequestSnapshot(ChunkLease lease, out ulong id)
        {
            Validate(lease); id = 0;
            if (snapshots.Count >= maxSnapshots) return false;
            if (nextRequest == ulong.MaxValue) throw new InvalidOperationException("Snapshot request space exhausted.");
            var capture = lease.Data.Capture();
            var item = new Pending { Id = nextRequest++, Capture = capture, Address = capture.Address, Incarnation = capture.Incarnation, Revision = capture.Revision };
            snapshots.Add(item); id = item.Id; return true;
        }
        public void PumpSnapshots()
        {
            RequireAlive();
            for (int i = snapshots.Count - 1; i >= 0; i--)
            {
                var item = snapshots[i];
                if (!item.Encoding)
                {
                    if (!item.Capture.IsCompleted) continue;
                    if (!IsLive(item)) { Retire(i); continue; }
                    var image = ChunkImage.FromOwnedChannels(item.Address, item.Incarnation, item.Revision, item.Capture.CopySolids(), item.Capture.CopyFluids());
                    item.Capture.Dispose(); item.Capture = null; item.Encoding = true;
                    //no Unity APIs or native chunk allocations are accessed from this worker.
                    if (!ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var result = new SnapshotResult { RequestId = item.Id, Address = item.Address, Incarnation = item.Incarnation, Revision = item.Revision };
                        try { result.Payload = ChunkWireCodec.EncodeSnapshot(image); }
                        catch (Exception exception) { result.Error = exception; }
                        item.Result = result; item.Done.Set();
                    }))
                    {
                        item.Result = new SnapshotResult { RequestId = item.Id, Error = new InvalidOperationException("Could not queue snapshot worker.") };
                        item.Done.Set();
                    }
                }
                if (item.Done.IsSet && !IsLive(item)) Retire(i);
            }
        }
        public bool TryTakeSnapshot(out SnapshotResult result)
        {
            RequireAlive(); result = null;
            for (int i = 0; i < snapshots.Count; i++)
            {
                var item = snapshots[i];
                if (!item.Encoding || !item.Done.IsSet) continue;
                if (IsLive(item)) result = item.Result;
                Retire(i--);
                if (result != null) return true;
            }
            return false;
        }
        public void CancelSnapshot(ulong id)
        { if (disposed) return; RequireAlive(); foreach (var item in snapshots) if (item.Id == id) item.Cancelled = true; }
        private bool IsLive(Pending item) => !item.Cancelled && chunks.TryGetValue(item.Address, out var entry) && entry.Data.Incarnation == item.Incarnation;
        private void Retire(int index)
        {
            var item = snapshots[index];
            item.Capture?.Dispose();
            if (item.Encoding) item.Done.Wait();
            item.Done.Dispose(); snapshots.RemoveAt(index);
        }
        public void Dispose()
        {
            if (disposed) return;
            RequireAlive();
            for (int i = snapshots.Count - 1; i >= 0; i--) Retire(i);
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
        public ResidentChunkStore Configure(BlockRegistry registry, int maxResidents = 256, int maxSnapshots = 16)
        {
            if (Store != null) throw new InvalidOperationException("World already has a chunk store.");
            return Store = new ResidentChunkStore(EntityManager, registry, maxResidents, maxSnapshots);
        }
        public void ReleaseStore() { Store?.Dispose(); Store = null; }
        protected override void OnUpdate() => Store?.PumpSnapshots();
        protected override void OnDestroy() => ReleaseStore();
    }
}
