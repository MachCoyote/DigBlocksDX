using DigBlocks.Core.Hosting;

namespace DigBlocks.Client.UI.Placeholders
{
    //fills in menus that have not been authored yet so the full application flow stays exercisable
    public static class PlaceholderMenus
    {
        public static void RegisterMissing(MenuRegistry registry, IGameLogger logger)
        {
            if (registry == null)
            {
                return;
            }

            Register(
                registry,
                logger,
                MenuIds.Title,
                new MenuMetadata(
                    MenuLayer.Screen,
                    blocksInteractionUnderneath: true,
                    blocksGameplayInput: true,
                    showsCursor: true,
                    MenuBackBehavior.None,
                    MenuRetentionPolicy.Retain),
                new DelegateMenuFactory(PlaceholderMenuBuilder.BuildTitle));

            Register(
                registry,
                logger,
                MenuIds.Loading,
                new MenuMetadata(
                    MenuLayer.Screen,
                    blocksInteractionUnderneath: true,
                    blocksGameplayInput: true,
                    showsCursor: false,
                    MenuBackBehavior.None,
                    MenuRetentionPolicy.Retain),
                new DelegateMenuFactory(PlaceholderMenuBuilder.BuildLoading));

            Register(
                registry,
                logger,
                MenuIds.Pause,
                new MenuMetadata(
                    MenuLayer.Overlay,
                    blocksInteractionUnderneath: true,
                    blocksGameplayInput: true,
                    showsCursor: true,
                    MenuBackBehavior.ViewHandled,
                    MenuRetentionPolicy.Retain),
                new DelegateMenuFactory(PlaceholderMenuBuilder.BuildPause));
        }

        private static void Register(
            MenuRegistry registry,
            IGameLogger logger,
            MenuId id,
            MenuMetadata metadata,
            IMenuFactory factory)
        {
            if (registry.Contains(id))
            {
                return;
            }

            registry.Register(new MenuRegistration(id, factory, metadata));
            logger?.Log($"Menu '{id}' is not authored yet; using the built-in placeholder.", GameLogLevel.Warning);
        }
    }
}
