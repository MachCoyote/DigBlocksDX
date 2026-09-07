using DigBlocks.Core.Hosting;
using System.Collections.Generic;

namespace DigBlocks.Networking
{
    public interface INetworkSession : IGameService
    {
        NetworkSessionRole Role { get; }
        NetworkSessionState State { get; }
        NetworkFailure LastFailure { get; }
        string LastTransportDisconnectReason { get; }
        IReadOnlyList<PeerRecord> Peers { get; }
    }
}
