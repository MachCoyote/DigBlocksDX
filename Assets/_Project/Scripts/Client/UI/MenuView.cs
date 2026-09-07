using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI
{
    //base class for prefab-authored menus; the coordinator decides when transitions happen, the view decides how
    [DisallowMultipleComponent]
    public abstract class MenuView : MonoBehaviour, IMenuView
    {
        [SerializeField]
        private Selectable initialSelection;

        [SerializeField]
        private CanvasGroup canvasGroup;

        public GameObject Root => gameObject;

        public Selectable InitialSelection => initialSelection;

        public bool IsOpen { get; private set; }

        public async UniTask OpenAsync(CancellationToken cancellationToken)
        {
            gameObject.SetActive(true);
            SetInteractable(true);
            IsOpen = true;

            await OnOpenAsync(cancellationToken);
        }

        public async UniTask CloseAsync(CancellationToken cancellationToken)
        {
            SetInteractable(false);

            try
            {
                await OnCloseAsync(cancellationToken);
            }
            finally
            {
                IsOpen = false;
                if (this != null && gameObject != null)
                {
                    gameObject.SetActive(false);
                }
            }
        }

        public virtual void SetInteractable(bool interactable)
        {
            CanvasGroup group = EnsureCanvasGroup();
            if (group == null)
            {
                return;
            }

            group.interactable = interactable;
            group.blocksRaycasts = interactable;
        }

        public virtual void Dispose()
        {
            if (this != null && gameObject != null)
            {
                Destroy(gameObject);
            }
        }

        //views override presentation only; they must never touch session, ECS, or networking state
        protected virtual UniTask OnOpenAsync(CancellationToken cancellationToken)
        {
            return UniTask.CompletedTask;
        }

        protected virtual UniTask OnCloseAsync(CancellationToken cancellationToken)
        {
            return UniTask.CompletedTask;
        }

        protected CanvasGroup EnsureCanvasGroup()
        {
            if (canvasGroup == null && this != null && gameObject != null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = gameObject.AddComponent<CanvasGroup>();
                }
            }

            return canvasGroup;
        }

        internal void SetInitialSelection(Selectable selectable)
        {
            initialSelection = selectable;
        }
    }
}
