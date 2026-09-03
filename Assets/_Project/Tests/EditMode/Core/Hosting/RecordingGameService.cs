using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;

namespace DigBlocks.Core.Tests.Hosting
{
    internal sealed class RecordingGameService : IGameService
    {
        private readonly List<string> events;

        public RecordingGameService(string name, List<string> events)
        {
            Name = name;
            this.events = events;
        }

        public string Name { get; }

        public Func<CancellationToken, UniTask> StartBehavior { get; set; }

        public Func<CancellationToken, UniTask> StopBehavior { get; set; }

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            events.Add($"start:{Name}");
            return StartBehavior?.Invoke(cancellationToken) ?? UniTask.CompletedTask;
        }

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            events.Add($"stop:{Name}");
            return StopBehavior?.Invoke(cancellationToken) ?? UniTask.CompletedTask;
        }
    }
}
