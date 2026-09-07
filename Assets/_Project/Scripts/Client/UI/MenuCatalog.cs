using System.Collections.Generic;
using DigBlocks.Core.Hosting;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    //asset-authored menu registrations, so adding a menu is asset work rather than a code change
    [CreateAssetMenu(menuName = "DigBlocks/Menu Catalog", fileName = "MenuCatalog")]
    public sealed class MenuCatalog : ScriptableObject
    {
        [SerializeField]
        private List<MenuCatalogEntry> menus = new List<MenuCatalogEntry>();

        public IReadOnlyList<MenuCatalogEntry> Menus => menus;

        public void RegisterInto(MenuRegistry registry, IGameLogger logger)
        {
            if (registry == null)
            {
                return;
            }

            for (int index = 0; index < menus.Count; index++)
            {
                MenuCatalogEntry entry = menus[index];
                if (entry == null)
                {
                    continue;
                }

                if (!entry.TryCreateRegistration(out MenuRegistration registration, out string error))
                {
                    logger?.Log(error, GameLogLevel.Error);
                    continue;
                }

                if (registry.Contains(registration.Id))
                {
                    logger?.Log(
                        $"Menu '{registration.Id}' appears more than once in '{name}'; the later entry is ignored.",
                        GameLogLevel.Warning);
                    continue;
                }

                registry.Register(registration);
            }
        }
    }
}
