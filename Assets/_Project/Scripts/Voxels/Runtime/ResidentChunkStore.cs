using System;
using System.Collections.Generic;
using System.Threading;
using DigBlocks.ChunkProtocol;
using Unity.Entities;

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
        private sealed class Entry { public ChunkData Data; public Entity Entity; public int Leases; }
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
            if (maxSnapshots < 1 || maxSnapshots > 8) throw new ArgumentOutOfRangeException(nameof(maxSnapshots));
            this.manager = manager; this.registry = registry; this.maxResidents = maxResidents; this.maxSnapshots = maxSnapshots;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }
        public int Count => chunks.Count;
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
            if (!chunks.TryGetValue(address, out var entry))
            {
                if (chunks.Count >= maxResidents) throw new InvalidOperationException("Resident chunk capacity exhausted.");
                if (nextIncarnation == ulong.MaxValue) throw new InvalidOperationException("Chunk incarnation space exhausted.");
                var data = new ChunkData(address, nextIncarnation++);
                Entity entity = Entity.Null;
                try
                {
                    entity = manager.CreateEntity(typeof(ResidentChunk));
                    manager.SetComponentData(entity, new ResidentChunk { Address = address, Incarnation = data.Incarnation, Revision = data.Revision });
                    entry = new Entry { Data = data, Entity = entity };
                    chunks.Add(address, entry);
                }
                catch { data.Dispose(); if (entity != Entity.Null && manager.Exists(entity)) manager.DestroyEntity(entity); throw; }
            }
            if (entry.Leases == int.MaxValue) throw new InvalidOperationException("Residency lease count exhausted.");
            entry.Leases++;
            return new ChunkLease(this, entry.Data, entry.Entity);
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
            foreach (var edit in edits)
            {
                var solid = registry.GetSolid(edit.Solid); registry.GetFluid(edit.Fluid);
                if (edit.Fluid != 0 && !solid.PermitsFluid) throw new ArgumentException("Solid state does not permit fluid.", nameof(edits));
            }
            lease.Data.Apply(edits, registry.MaxSolidStateId, registry.MaxFluidStateId);
            manager.SetComponentData(lease.Entity, new ResidentChunk { Address = lease.Address, Incarnation = lease.Incarnation, Revision = lease.Data.Revision });
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
                    var image = new ChunkImage(item.Address, item.Incarnation, item.Revision, item.Capture.CopySolids(), item.Capture.CopyFluids());
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
        { RequireAlive(); foreach (var item in snapshots) if (item.Id == id) item.Cancelled = true; }
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
        public ResidentChunkStore Configure(BlockRegistry registry, int maxResidents = 256, int maxSnapshots = 2)
        {
            if (Store != null) throw new InvalidOperationException("World already has a chunk store.");
            return Store = new ResidentChunkStore(EntityManager, registry, maxResidents, maxSnapshots);
        }
        public void ReleaseStore() { Store?.Dispose(); Store = null; }
        protected override void OnUpdate() => Store?.PumpSnapshots();
        protected override void OnDestroy() => ReleaseStore();
    }
}
