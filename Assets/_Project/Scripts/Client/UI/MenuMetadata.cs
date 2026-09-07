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
            MenuRetentionPolicy retention)
        {
            Layer = layer;
            BlocksInteractionUnderneath = blocksInteractionUnderneath;
            BlocksGameplayInput = blocksGameplayInput;
            ShowsCursor = showsCursor;
            BackBehavior = backBehavior;
            Retention = retention;
        }

        public MenuLayer Layer { get; }

        //when false the menu directly beneath this one stays interactive
        public bool BlocksInteractionUnderneath { get; }

        public bool BlocksGameplayInput { get; }

        public bool ShowsCursor { get; }

        public MenuBackBehavior BackBehavior { get; }

        public MenuRetentionPolicy Retention { get; }

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
