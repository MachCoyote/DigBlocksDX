using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Core.Session
{
    //owns exactly one game session lifetime at a time
    public interface IGameSessionController
    {
        GameSessionStatus Status { get; }

        event Action<GameSessionStatus> StatusChanged;

        UniTask<GameSessionStatus> StartSessionAsync(
            SessionStartRequest request,
            CancellationToken cancellationToken);

        UniTask StopSessionAsync(CancellationToken cancellationToken);

        UniTask SetSimulationPausedAsync(bool paused, CancellationToken cancellationToken);
    }
}
