namespace DigBlocks.Core.Launch
{
    public readonly struct LaunchOptions
    {
        public LaunchOptions(LaunchMode mode)
        {
            Mode = mode;
        }

        public LaunchMode Mode { get; }
    }
}
