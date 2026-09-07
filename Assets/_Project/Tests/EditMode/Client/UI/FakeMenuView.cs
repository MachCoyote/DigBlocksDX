using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.Tests.UI
{
    internal sealed class FakeMenuView : IMenuView, IMenuBackHandler
    {
        public FakeMenuView(string name)
        {
            Root = new GameObject(name);
            Root.SetActive(false);
        }

        public GameObject Root { get; }

        public Selectable InitialSelection => null;

        public bool IsInteractable { get; private set; }

        public int OpenCount { get; private set; }

        public int CloseCount { get; private set; }

        public int BackCount { get; private set; }

        public bool Disposed { get; private set; }

        public UniTask OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            Root.SetActive(true);
            return UniTask.CompletedTask;
        }

        public UniTask CloseAsync(CancellationToken cancellationToken)
        {
            CloseCount++;
            IsInteractable = false;
            Root.SetActive(false);
            return UniTask.CompletedTask;
        }

        public void SetInteractable(bool interactable)
        {
            IsInteractable = interactable;
        }

        public UniTask OnBackAsync(CancellationToken cancellationToken)
        {
            BackCount++;
            return UniTask.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            if (Root != null)
            {
                Object.DestroyImmediate(Root);
            }
        }
    }
}
