using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Core.Session
{
    //a session service that gates playability behind an explicit readiness condition
    public interface ISessionReadinessSource
    {
        string ReadinessDescription { get; }

        UniTask WaitUntilReadyAsync(CancellationToken cancellationToken);
    }
}
