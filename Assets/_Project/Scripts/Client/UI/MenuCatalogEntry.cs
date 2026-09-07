using System;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    //one authored menu and the metadata the coordinator needs to manage it
    [Serializable]
    public sealed class MenuCatalogEntry
    {
        [SerializeField]
        [Tooltip("Menu identifier, for example Title, Loading, Pause, or Intro.")]
        private string menuId = string.Empty;

        [SerializeField]
        [Tooltip("Prefab whose root carries a component deriving from MenuView.")]
        private GameObject prefab;

        [SerializeField]
        private MenuLayer layer = MenuLayer.Screen;

        [SerializeField]
        [Tooltip("When cleared, the menu directly beneath this one stays interactive.")]
        private bool blocksInteractionUnderneath = true;

        [SerializeField]
        private bool blocksGameplayInput = true;

        [SerializeField]
        private bool showsCursor = true;

        [SerializeField]
        private MenuBackBehavior backBehavior = MenuBackBehavior.None;

        [SerializeField]
        private MenuRetentionPolicy retention = MenuRetentionPolicy.Retain;

        [SerializeField]
        [Tooltip("Stretches the instantiated root to fill its layer. Clear it for menus that own their own layout.")]
        private bool stretchRootToLayer = true;

        public string MenuIdValue => menuId;

        public bool TryCreateRegistration(out MenuRegistration registration, out string error)
        {
            registration = null;

            if (string.IsNullOrWhiteSpace(menuId))
            {
                error = "A catalog entry has no menu identifier.";
                return false;
            }

            if (prefab == null)
            {
                error = $"Catalog entry '{menuId}' has no prefab assigned.";
                return false;
            }

            if (prefab.GetComponent<IMenuView>() == null)
            {
                error = $"Catalog entry '{menuId}' points at prefab '{prefab.name}', which has no {nameof(MenuView)} component.";
                return false;
            }

            var metadata = new MenuMetadata(
                layer,
                blocksInteractionUnderneath,
                blocksGameplayInput,
                showsCursor,
                backBehavior,
                retention);

            registration = new MenuRegistration(
                new MenuId(menuId.Trim()),
                new PrefabMenuFactory(prefab, stretchRootToLayer),
                metadata);
            error = null;
            return true;
        }
    }
}
