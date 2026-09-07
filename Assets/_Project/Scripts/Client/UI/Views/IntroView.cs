using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI.Views
{
    //presents the logo and reports when its presentation is done; flow decides what happens next
    public class IntroView : MenuView, IMenuBackHandler
    {
        [SerializeField]
        private float presentationSeconds = 2f;

        [SerializeField]
        private Button skipButton;

        private CancellationTokenSource presentationCancellation;
        private bool listenersHooked;

        public event Action SkipRequested;

        public event Action PresentationCompleted;

        public UniTask OnBackAsync(CancellationToken cancellationToken)
        {
            RaiseSkipRequested();
            return UniTask.CompletedTask;
        }

        public void RequestSkip()
        {
            RaiseSkipRequested();
        }

        protected override UniTask OnOpenAsync(CancellationToken cancellationToken)
        {
            HookListeners();
            CancelPresentation();
            presentationCancellation = new CancellationTokenSource();
            RunPresentationAsync(presentationCancellation.Token).Forget();
            return UniTask.CompletedTask;
        }

        protected override UniTask OnCloseAsync(CancellationToken cancellationToken)
        {
            CancelPresentation();
            return UniTask.CompletedTask;
        }

        protected virtual void Awake()
        {
            HookListeners();
        }

        protected virtual void OnDestroy()
        {
            CancelPresentation();

            if (!listenersHooked)
            {
                return;
            }

            listenersHooked = false;

            if (skipButton != null)
            {
                skipButton.onClick.RemoveListener(RaiseSkipRequested);
            }
        }

        private async UniTaskVoid RunPresentationAsync(CancellationToken cancellationToken)
        {
            try
            {
                //unscaled time so intro presentation never depends on a simulation clock
                await UniTask.Delay(
                    TimeSpan.FromSeconds(Mathf.Max(0f, presentationSeconds)),
                    DelayType.UnscaledDeltaTime,
                    PlayerLoopTiming.Update,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PresentationCompleted?.Invoke();
        }

        private void CancelPresentation()
        {
            if (presentationCancellation == null)
            {
                return;
            }

            presentationCancellation.Cancel();
            presentationCancellation.Dispose();
            presentationCancellation = null;
        }

        private void HookListeners()
        {
            if (listenersHooked || skipButton == null)
            {
                return;
            }

            listenersHooked = true;
            skipButton.onClick.AddListener(RaiseSkipRequested);
        }

        private void RaiseSkipRequested()
        {
            SkipRequested?.Invoke();
        }
    }
}
