using System.Collections.Generic;
using DigBlocks.Core.Hosting;
using Unity.Entities;

namespace DigBlocks.Networking.NetCode
{
    //managed control-plane state, never consulted by hot gameplay jobs.
    public sealed class SessionContext
    {
        public NetworkSessionOptions Options;
        public AdmissionRegistry Registry;
        public IGameLogger Logger;
        public NetworkSessionState State;
        public NetworkFailure Failure;
        public string TransportDisconnectReason;
        public uint Nonce;
        public ulong OfflineXuid;
        public ulong PeerId;
        public Entity ClientConnection;
        public bool HelloSent;
        public bool Accepted;
        public bool Stopping;
        public readonly Dictionary<Entity, ulong> ServerConnections = new();

        public void Fail(NetworkFailure reason)
        {
            if (Stopping) return;
            if (Failure == NetworkFailure.None) Failure = reason;
            State = NetworkSessionState.Disconnected;
            Accepted = false;
            PeerId = 0;
        }

        public static ulong ConnectionKey(Entity entity) => ((ulong)(uint)entity.Version << 32) | (uint)entity.Index;
    }

    public struct SessionActive : IComponentData { }

    //control-plane ownership lives in a managed system, not deprecated managed ECS components.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    public partial class SessionContextSystem : SystemBase
    {
        public SessionContext Context { get; set; }
        protected override void OnCreate() => Enabled = false;
        protected override void OnUpdate() { }
        protected override void OnDestroy() => Context = null;
    }

    public struct NetworkSessionReady : IComponentData
    {
        public uint ProtocolVersion;
        public uint Nonce;
    }

    public struct AdmissionHandled : IComponentData { }
    public struct PendingDisconnect : IComponentData
    {
        public double Deadline;
        public NetworkFailure Reason;
    }

    public struct SessionCloseRpc : Unity.NetCode.IApprovalRpcCommand
    {
        public NetworkFailure Reason;
    }
}
