using System;

namespace DigBlocks.Client.UI
{
    //what the currently visible UI needs from client input, reported to the input router
    public readonly struct MenuInputRequirements : IEquatable<MenuInputRequirements>
    {
        public static readonly MenuInputRequirements None = default;

        public MenuInputRequirements(bool anyMenuOpen, bool blocksGameplayInput, bool showsCursor)
        {
            AnyMenuOpen = anyMenuOpen;
            BlocksGameplayInput = blocksGameplayInput;
            ShowsCursor = showsCursor;
        }

        public bool AnyMenuOpen { get; }

        public bool BlocksGameplayInput { get; }

        public bool ShowsCursor { get; }

        public bool Equals(MenuInputRequirements other)
        {
            return AnyMenuOpen == other.AnyMenuOpen
                && BlocksGameplayInput == other.BlocksGameplayInput
                && ShowsCursor == other.ShowsCursor;
        }

        public override bool Equals(object obj)
        {
            return obj is MenuInputRequirements other && Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = AnyMenuOpen ? 1 : 0;
            hash |= BlocksGameplayInput ? 2 : 0;
            hash |= ShowsCursor ? 4 : 0;
            return hash;
        }
    }
}
