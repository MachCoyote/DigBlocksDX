using System.Threading;
using Cysharp.Threading.Tasks;

namespace DigBlocks.Client.UI
{
    //implemented by views whose metadata declares MenuBackBehavior.ViewHandled
    public interface IMenuBackHandler
    {
        UniTask OnBackAsync(CancellationToken cancellationToken);
    }
}
