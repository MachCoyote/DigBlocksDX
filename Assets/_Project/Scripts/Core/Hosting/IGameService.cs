using System.Threading;
using System.Threading.Tasks;

namespace DigBlocks.Core.Hosting
{
    public interface IGameService
    {
        string Name { get; }

        Task StartAsync(CancellationToken cancellationToken);

        Task StopAsync(CancellationToken cancellationToken);
    }
}
