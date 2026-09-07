using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI
{
    //the lifecycle contract every menu shares, whether authored as a prefab or built in code
    public interface IMenuView : IDisposable
    {
        GameObject Root { get; }

        Selectable InitialSelection { get; }

        UniTask OpenAsync(CancellationToken cancellationToken);

        UniTask CloseAsync(CancellationToken cancellationToken);

        void SetInteractable(bool interactable);
    }
}
