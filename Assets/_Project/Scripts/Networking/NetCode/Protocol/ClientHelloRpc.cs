using Unity.NetCode;
using Unity.Collections;

namespace DigBlocks.Networking.NetCode
{
    public struct ClientHelloRpc : IApprovalRpcCommand
    {
        public uint ProtocolVersion;
        public uint Nonce;
        public ulong OfflineXuid;
        public FixedString32Bytes DisplayName;
    }
}
