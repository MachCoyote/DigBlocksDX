using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Simulation.Definitions;
using Unity.Entities;

namespace DigBlocks.Client.Rendering
{
    /// <summary>
    /// Gives the client world a way to draw entities. Composed only for graphical clients, in the
    /// same way terrain rendering is, so a dedicated server never constructs one.
    /// </summary>
    public sealed class EntityRenderService : IGameService
    {
        private readonly Func<World> getClientWorld;
        private readonly CompiledEntityContent content;
        private readonly Func<IEntityPresentationBackend> createBackend;
        private EntityViewSystem system;

        public EntityRenderService(Func<World> getClientWorld, CompiledEntityContent content,
            Func<IEntityPresentationBackend> createBackend = null)
        {
            this.getClientWorld = getClientWorld ?? throw new ArgumentNullException(nameof(getClientWorld));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            //the seam that makes the renderer replaceable: nothing outside this line chooses one.
            this.createBackend = createBackend ?? (() => new EntitiesGraphicsPresentationBackend());
        }

        public string Name => nameof(EntityRenderService);

        public UniTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            World world = getClientWorld();
            if (world is not { IsCreated: true }) return UniTask.CompletedTask;
            system = world.GetOrCreateSystemManaged<EntityViewSystem>();
            system.Configure(createBackend(), content);
            return UniTask.CompletedTask;
        }

        //the backend owns runtime meshes and materials, and the system disposes it when the world
        //goes away. Stopping before the world is torn down would leave live entities without one.
        public UniTask StopAsync(CancellationToken cancellationToken)
        {
            system = null;
            return UniTask.CompletedTask;
        }
    }
}
