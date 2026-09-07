using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Core.Session
{
    //a session service able to freeze and resume its own simulation
    public interface ISimulationPauseSink
    {
        UniTask SetSimulationPausedAsync(bool paused, CancellationToken cancellationToken);
    }
}
