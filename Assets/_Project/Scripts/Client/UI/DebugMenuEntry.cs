namespace DigBlocks.Client.UI
{
    //one presented debug line: what the toggle is called and the key that drives it
    public readonly struct DebugMenuEntry
    {
        public DebugMenuEntry(string label, string bind)
        {
            Label = label ?? string.Empty;
            Bind = bind ?? string.Empty;
        }

        public string Label { get; }

        public string Bind { get; }
    }
}
