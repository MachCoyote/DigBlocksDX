using System;
using System.Collections.Generic;

namespace DigBlocks.Core.Diagnostics
{
    //mutable debug switchboard: a state index per toggle, with change notification for observers.
    //toggles are stored by identifier rather than declared here, so a new one costs no change to Core
    public sealed class DebugOptions : IDebugOptions
    {
        private readonly Dictionary<DebugToggleId, int> states = new Dictionary<DebugToggleId, int>();

        public event Action<DebugToggleId, int> StateChanged;

        public int GetState(DebugToggleId id)
        {
            return states.TryGetValue(id, out int state) ? state : 0;
        }

        public bool IsEnabled(DebugToggleId id)
        {
            return GetState(id) != 0;
        }

        public void SetState(DebugToggleId id, int state)
        {
            if (!id.IsValid || state < 0)
            {
                return;
            }

            if (GetState(id) == state)
            {
                return;
            }

            states[id] = state;
            StateChanged?.Invoke(id, state);
        }

        //advances to the next state and wraps; a two-state toggle is the on/off case of the same rule
        public int Cycle(DebugToggleId id, int stateCount)
        {
            if (!id.IsValid || stateCount < 2)
            {
                return GetState(id);
            }

            int next = (GetState(id) + 1) % stateCount;
            SetState(id, next);
            return next;
        }

        public void Reset()
        {
            if (states.Count == 0)
            {
                return;
            }

            var cleared = new List<DebugToggleId>(states.Keys);
            states.Clear();

            for (int index = 0; index < cleared.Count; index++)
            {
                StateChanged?.Invoke(cleared[index], 0);
            }
        }
    }
}
