using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;

namespace DigBlocks.Client.Runtime
{
    public sealed class ClientRuntime : IGameService
    {
        public string Name => nameof(ClientRuntime);

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
