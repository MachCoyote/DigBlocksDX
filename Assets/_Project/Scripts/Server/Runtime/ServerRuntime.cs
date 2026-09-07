using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using System.Threading;
using Unity.Entities;
using System;

namespace DigBlocks.Server.Runtime
{
    public sealed class ServerRuntime : IGameService
    {
        public string Name => nameof(ServerRuntime);

        private readonly Func<World> createWorld;

        public World World { get; private set; }

        public ServerRuntime(Func<World> createWorld)
        {
            this.createWorld = createWorld ??
                throw new ArgumentNullException(nameof(createWorld));
        }

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (World != null) throw new InvalidOperationException("Server world already started.");
            World = createWorld();
            if (World is not { IsCreated: true }) throw new InvalidOperationException("Server factory did not create a world.");

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
