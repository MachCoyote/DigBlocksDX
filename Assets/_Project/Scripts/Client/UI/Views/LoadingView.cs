using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace DigBlocks.Client.UI.Views
{
    //displays session startup status supplied by application flow
    public class LoadingView : MenuView
    {
        [SerializeField]
        private Text statusLabel;

        [SerializeField]
        private Text errorLabel;

        public void SetStatus(string message)
        {
            ApplyStatus(message ?? string.Empty);
        }

        public void SetError(string message)
        {
            ApplyError(message ?? string.Empty);
        }

        protected override UniTask OnOpenAsync(CancellationToken cancellationToken)
        {
            ApplyError(string.Empty);
            return UniTask.CompletedTask;
        }

        //overridden by views that present status through something other than legacy uGUI text
        protected virtual void ApplyStatus(string message)
        {
            if (statusLabel != null)
            {
                statusLabel.text = message;
            }
        }

        protected virtual void ApplyError(string message)
        {
            if (errorLabel == null)
            {
                return;
            }

            errorLabel.text = message;
            errorLabel.gameObject.SetActive(!string.IsNullOrEmpty(message));
        }

        internal void SetLabels(Text status, Text error)
        {
            statusLabel = status;
            errorLabel = error;
        }
    }
}
