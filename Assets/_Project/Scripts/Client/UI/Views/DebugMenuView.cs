using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace DigBlocks.Client.UI.Views
{
    //presents the debug switchboard; content is pushed in, so a new toggle needs no change here
    public sealed class DebugMenuView : MenuView
    {
        [SerializeField]
        [Tooltip("Heading shown above the toggle list.")]
        private TMP_Text titleLabel;

        [SerializeField]
        [Tooltip("One line per debug toggle, formatted as 'Name - Bind'.")]
        private TMP_Text toggleLabel;

        private readonly StringBuilder builder = new StringBuilder();

        public void SetTitle(string title)
        {
            if (titleLabel != null)
            {
                titleLabel.text = title ?? string.Empty;
            }
        }

        public void SetToggles(IReadOnlyList<DebugMenuEntry> entries)
        {
            if (toggleLabel == null)
            {
                return;
            }

            builder.Clear();

            for (int index = 0; entries != null && index < entries.Count; index++)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(entries[index].Label).Append(" - ").Append(entries[index].Bind);
            }

            toggleLabel.SetText(builder);
        }

        //the overlay is informational: it is always visually live and never consumes pointer input
        public override void SetInteractable(bool interactable)
        {
            CanvasGroup group = EnsureCanvasGroup();
            if (group == null)
            {
                return;
            }

            group.interactable = false;
            group.blocksRaycasts = false;
        }

        private void Awake()
        {
            //authored labels may still be raycast targets; a debug overlay must never eat a click
            if (titleLabel != null)
            {
                titleLabel.raycastTarget = false;
            }

            if (toggleLabel != null)
            {
                toggleLabel.raycastTarget = false;
            }
        }
    }
}
