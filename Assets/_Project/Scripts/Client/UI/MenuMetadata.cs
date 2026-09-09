namespace DigBlocks.Client.UI
{
    public readonly struct MenuMetadata
    {
        public MenuMetadata(
            MenuLayer layer,
            bool blocksInteractionUnderneath,
            bool blocksGameplayInput,
            bool showsCursor,
            MenuBackBehavior backBehavior,
            MenuRetentionPolicy retention,
            bool takesNavigationFocus = true)
        {
            Layer = layer;
            BlocksInteractionUnderneath = blocksInteractionUnderneath;
            BlocksGameplayInput = blocksGameplayInput;
            ShowsCursor = showsCursor;
            BackBehavior = backBehavior;
            Retention = retention;
            TakesNavigationFocus = takesNavigationFocus;
        }

        public MenuLayer Layer { get; }

        //when false the menu directly beneath this one stays interactive
        public bool BlocksInteractionUnderneath { get; }

        public bool BlocksGameplayInput { get; }

        public bool ShowsCursor { get; }

        public MenuBackBehavior BackBehavior { get; }

        public MenuRetentionPolicy Retention { get; }

        //when false the menu never becomes the focus target, so a purely informational overlay
        //cannot steal the selected control from the interactive menu beneath it
        public bool TakesNavigationFocus { get; }

        public static MenuMetadata ForLayer(MenuLayer layer)
        {
            switch (layer)
            {
                case MenuLayer.Overlay:
                    return new MenuMetadata(
                        layer,
                        blocksInteractionUnderneath: true,
                        blocksGameplayInput: true,
                        showsCursor: true,
                        MenuBackBehavior.Close,
                        MenuRetentionPolicy.Retain);

                case MenuLayer.Modal:
                    return new MenuMetadata(
                        layer,
                        blocksInteractionUnderneath: true,
                        blocksGameplayInput: true,
                        showsCursor: true,
                        MenuBackBehavior.Close,
                        MenuRetentionPolicy.Destroy);

                default:
                    return new MenuMetadata(
                        MenuLayer.Screen,
                        blocksInteractionUnderneath: true,
                        blocksGameplayInput: true,
                        showsCursor: true,
                        MenuBackBehavior.None,
                        MenuRetentionPolicy.Retain);
            }
        }
    }
}
