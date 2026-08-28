using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        public Func<CancellationToken, Task> StartBehavior { get; set; }

        public Func<CancellationToken, Task> StopBehavior { get; set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add($"start:{Name}");
            return StartBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add($"stop:{Name}");
            return StopBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
