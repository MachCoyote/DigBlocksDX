using System;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    //the coordinator's runtime record for one managed menu
    internal sealed class MenuEntry
    {
        public MenuEntry(MenuRegistration registration, IMenuView view)
        {
            Registration = registration ?? throw new ArgumentNullException(nameof(registration));
            View = view ?? throw new ArgumentNullException(nameof(view));
            State = MenuLifecycleState.Opening;
        }

        public MenuRegistration Registration { get; }

        public IMenuView View { get; }

        public MenuId Id => Registration.Id;

        public MenuMetadata Metadata => Registration.Metadata;

        public MenuLayer Layer => Registration.Metadata.Layer;

        public GameObject PreviousSelection { get; set; }

        public MenuLifecycleState State { get; set; }
    }
}
