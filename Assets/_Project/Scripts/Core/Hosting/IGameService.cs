using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Core.Hosting
{
    public interface IGameService
    {
        string Name { get; }

        UniTask StartAsync(CancellationToken cancellationToken);

        UniTask StopAsync(CancellationToken cancellationToken);
    }
}
