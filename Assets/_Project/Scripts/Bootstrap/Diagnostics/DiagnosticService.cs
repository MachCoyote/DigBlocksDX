using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using System.Threading;
using UnityEngine;

namespace DigBlocks.Bootstrap.Diagnostics
{
    public sealed class DiagnosticService : IGameService
    {
        public string Name => "DiagnosticService";

        private readonly IGameLogger logger;

        public DiagnosticService(IGameLogger logger)
        {
            this.logger = logger.CreateFor(Name);
        }

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.Log("Started.");

            return UniTask.CompletedTask;
        }

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.Log("Stopped.");

            return UniTask.CompletedTask;
        }

    }
}
