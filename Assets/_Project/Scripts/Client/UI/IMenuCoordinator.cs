using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Client.UI
{
    //owns presentation navigation only; it never touches session, ECS, or networking state
    public interface IMenuCoordinator
    {
        MenuInputRequirements InputRequirements { get; }

        event Action<MenuInputRequirements> InputRequirementsChanged;

        bool IsRegistered(MenuId id);

        bool IsOpen(MenuId id);

        void SetViewBinder(IMenuViewBinder binder);

        UniTask ShowScreenAsync(MenuId id, CancellationToken cancellationToken);

        UniTask ClearScreenAsync(CancellationToken cancellationToken);

        UniTask PushOverlayAsync(MenuId id, CancellationToken cancellationToken);

        UniTask ShowModalAsync(MenuId id, CancellationToken cancellationToken);

        UniTask CloseAsync(MenuId id, CancellationToken cancellationToken);

        UniTask<bool> BackAsync(CancellationToken cancellationToken);

        UniTask ResetAsync(CancellationToken cancellationToken);
    }
}
