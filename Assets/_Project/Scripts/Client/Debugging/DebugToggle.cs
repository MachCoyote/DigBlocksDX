using System;
using System.Collections.Generic;
using DigBlocks.Core.Diagnostics;
using UnityEngine.InputSystem;

namespace DigBlocks.Client.Debugging
{
    //one debug switch: what it is called, which key drives it, and the states it cycles through
    public sealed class DebugToggle
    {
        private static readonly string[] OnOff = { "Off", "On" };

        public DebugToggle(DebugToggleId id, string label, Key key, params string[] stateLabels)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("A debug toggle needs a valid identifier.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("A debug toggle needs a display name.", nameof(label));
            }

            Id = id;
            Label = label;
            Key = key;
            StateLabels = stateLabels != null && stateLabels.Length >= 2 ? stateLabels : OnOff;
        }

        public DebugToggleId Id { get; }

        public string Label { get; }

        public Key Key { get; }

        public IReadOnlyList<string> StateLabels { get; }

        public int StateCount => StateLabels.Count;

        //enum names match keyboard control names and path matching ignores case, so this needs no table
        public string BindingPath => "<Keyboard>/" + Key;

        public string BindDisplay => Key.ToString();

        public string DescribeState(int state)
        {
            return state >= 0 && state < StateLabels.Count ? StateLabels[state] : state.ToString();
        }
    }
}
