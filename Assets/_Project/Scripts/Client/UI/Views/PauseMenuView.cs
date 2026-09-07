using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI.Views
{
    //emits pause intent; session teardown and simulation pause belong to application flow
    public class PauseMenuView : MenuView, IMenuBackHandler
    {
        [SerializeField]
        private Button resumeButton;

        [SerializeField]
        private Button returnToTitleButton;

        private bool listenersHooked;

        public event Action ResumeRequested;

        public event Action ReturnToTitleRequested;

        public UniTask OnBackAsync(CancellationToken cancellationToken)
        {
            RaiseResumeRequested();
            return UniTask.CompletedTask;
        }

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

            if (resumeButton != null)
            {
                resumeButton.onClick.RemoveListener(RaiseResumeRequested);
            }

            if (returnToTitleButton != null)
            {
                returnToTitleButton.onClick.RemoveListener(RaiseReturnToTitleRequested);
            }
        }

        private void HookListeners()
        {
            if (listenersHooked)
            {
                return;
            }

            if (resumeButton == null && returnToTitleButton == null)
            {
                return;
            }

            listenersHooked = true;

            if (resumeButton != null)
            {
                resumeButton.onClick.AddListener(RaiseResumeRequested);
            }

            if (returnToTitleButton != null)
            {
                returnToTitleButton.onClick.AddListener(RaiseReturnToTitleRequested);
            }
        }

        private void RaiseResumeRequested()
        {
            ResumeRequested?.Invoke();
        }

        private void RaiseReturnToTitleRequested()
        {
            ReturnToTitleRequested?.Invoke();
        }

        internal void SetControls(Button resume, Button returnToTitle)
        {
            resumeButton = resume;
            returnToTitleButton = returnToTitle;
        }
    }
}
