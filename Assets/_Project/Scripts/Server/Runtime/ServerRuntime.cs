using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;

namespace DigBlocks.Server.Runtime
{
    public sealed class ServerRuntime : IGameService
    {
        public string Name => nameof(ServerRuntime);

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.CompletedTask;
        }

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return UniTask.CompletedTask;
        }
    }
}
