using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    public struct ServerHelloRpc : IApprovalRpcCommand
    {
        public uint ProtocolVersion;
        public uint Nonce;
        public ulong PeerId;
        public ulong OfflineXuid;
        public NetworkFailure Failure;
    }
}
