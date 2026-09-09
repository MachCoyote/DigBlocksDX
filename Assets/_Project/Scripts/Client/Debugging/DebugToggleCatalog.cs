using System.Collections.Generic;
using DigBlocks.Core.Diagnostics;
using UnityEngine.InputSystem;

namespace DigBlocks.Client.Debugging
{
    //the debug switches the client offers; adding one here is all a new toggle needs on the UI side
    public static class DebugToggleCatalog
    {
        //the key that shows and hides the debug menu itself; it works whether or not the menu is open
        public const Key MenuKey = Key.F3;

        public static IReadOnlyList<DebugToggle> Default { get; } = new[]
        {
            new DebugToggle(DebugToggleIds.Wireframe, "Wireframe View", Key.F8, "Off", "Overlay", "Only"),
            new DebugToggle(DebugToggleIds.Fullbright, "Fullbright", Key.F7),
            new DebugToggle(DebugToggleIds.Overdraw, "Highlight Overdraw", Key.F6, "Off", "All Layers", "Shaded Only"),
            new DebugToggle(DebugToggleIds.SecondaryCameraCulling, "Secondary Camera Culling", Key.F5, "Own View", "Mirror Main")
        };
    }
}
