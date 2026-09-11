using System;
using UnityEngine.InputSystem;

namespace DigBlocks.Client.Debugging
{
    /// <summary>
    /// A debug command that does something once when its key is pressed, as opposed to a
    /// <see cref="DebugToggle"/>, which cycles a persistent state.
    /// </summary>
    /// <remarks>
    /// The work itself is a callback supplied by the composition root, because a debug command
    /// usually wants something this assembly must not see. Spawning an entity, for instance, is a
    /// networking concern, and client presentation holds no session or transport knowledge.
    /// </remarks>
    public sealed class DebugAction
    {
        public DebugAction(string id, string label, Key key, Action invoke)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A debug action needs an identifier.", nameof(id));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A debug action needs a display name.", nameof(label));
            Id = id;
            Label = label;
            Key = key;
            Invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        }

        public string Id { get; }
        public string Label { get; }
        public Key Key { get; }
        public Action Invoke { get; }

        //enum names match keyboard control names and path matching ignores case, so this needs no table
        public string BindingPath => "<Keyboard>/" + Key;

        public string BindDisplay => Key.ToString();
    }
}
