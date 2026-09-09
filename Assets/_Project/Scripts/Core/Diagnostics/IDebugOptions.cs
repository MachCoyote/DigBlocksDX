using System;

namespace DigBlocks.Core.Diagnostics
{
    //read side of the debug switchboard; subsystems observe it without knowing what drives it
    public interface IDebugOptions
    {
        int GetState(DebugToggleId id);

        bool IsEnabled(DebugToggleId id);

        event Action<DebugToggleId, int> StateChanged;
    }
}
