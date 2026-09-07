using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using System;
using Unity.Entities;

namespace DigBlocks.Client.Runtime
{
    public sealed class ClientRuntime : IGameService
    {
        public string Name => nameof(ClientRuntime);

        private readonly Func<World> createWorld;

        public World World { get; private set; }

        public ClientRuntime(Func<World> createWorld)
        {
            this.createWorld = createWorld ??
                throw new ArgumentNullException(nameof(createWorld));
        }

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (World != null) throw new InvalidOperationException("Client world already started.");
            World = createWorld();
            if (World is not { IsCreated: true }) throw new InvalidOperationException("Client factory did not create a world.");

            return UniTask.CompletedTask;
        }

        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (World is { IsCreated: true })
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(World);
                World.Dispose();
            }

            World = null;

            return UniTask.CompletedTask;
        }
    }
}
