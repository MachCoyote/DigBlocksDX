namespace DigBlocks.Core.Diagnostics
{
    //well-known toggles whose state is read outside the assembly that declares the keybind
    public static class DebugToggleIds
    {
        public static readonly DebugToggleId Wireframe = new DebugToggleId("Wireframe");

        public static readonly DebugToggleId Fullbright = new DebugToggleId("Fullbright");

        public static readonly DebugToggleId Overdraw = new DebugToggleId("Overdraw");
    }
}
