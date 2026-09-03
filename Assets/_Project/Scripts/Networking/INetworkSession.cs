using DigBlocks.Core.Hosting;

namespace DigBlocks.Networking
{
    public interface INetworkSession : IGameService
    {
        NetworkSessionRole Role { get; }
    }
}
