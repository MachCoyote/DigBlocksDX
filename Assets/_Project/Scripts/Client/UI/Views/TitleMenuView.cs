using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI.Views
{
    //emits title-screen intent; it never starts a session itself
    public class TitleMenuView : MenuView
    {
        [SerializeField]
        private Button playButton;

        [SerializeField]
        private Button quitButton;

        private bool listenersHooked;

        public event Action PlayRequested;

        public event Action QuitRequested;

        protected override UniTask OnOpenAsync(CancellationToken cancellationToken)
        {
            HookListeners();
            return UniTask.CompletedTask;
        }

        protected virtual void Awake()
        {
            HookListeners();
        }

        protected virtual void OnDestroy()
        {
            if (!listenersHooked)
            {
                return;
            }

            listenersHooked = false;

            if (playButton != null)
            {
                playButton.onClick.RemoveListener(RaisePlayRequested);
            }

            if (quitButton != null)
            {
                quitButton.onClick.RemoveListener(RaiseQuitRequested);
            }
        }

        private void HookListeners()
        {
            if (listenersHooked)
            {
                return;
            }

            if (playButton == null && quitButton == null)
            {
                return;
            }

            listenersHooked = true;

            if (playButton != null)
            {
                playButton.onClick.AddListener(RaisePlayRequested);
            }
            else
            {
                Debug.LogError($"{nameof(TitleMenuView)} on '{name}' has no play button assigned.", this);
            }

            if (quitButton != null)
            {
                quitButton.onClick.AddListener(RaiseQuitRequested);
            }
        }

        private void RaisePlayRequested()
        {
            PlayRequested?.Invoke();
        }

        private void RaiseQuitRequested()
        {
            QuitRequested?.Invoke();
        }

        internal void SetControls(Button play, Button quit)
        {
            playButton = play;
            quitButton = quit;
        }
    }
}
