using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    //creates a menu instance beneath a parent supplied by the UI system
    public interface IMenuFactory
    {
        UniTask<IMenuView> CreateAsync(Transform parent, CancellationToken cancellationToken);
    }
}
