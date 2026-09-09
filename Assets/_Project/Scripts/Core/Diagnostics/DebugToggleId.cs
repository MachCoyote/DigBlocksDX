using System;

namespace DigBlocks.Core.Diagnostics
{
    //identifies a debug toggle across assemblies so the UI that presents it and the subsystem that
    //obeys it never have to reference one another
    public readonly struct DebugToggleId : IEquatable<DebugToggleId>
    {
        private readonly string value;

        public DebugToggleId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A debug toggle identifier is required.", nameof(value));
            }

            this.value = value;
        }

        public string Value => value ?? string.Empty;

        public bool IsValid => !string.IsNullOrEmpty(value);

        public bool Equals(DebugToggleId other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is DebugToggleId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }

        public static bool operator ==(DebugToggleId left, DebugToggleId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(DebugToggleId left, DebugToggleId right)
        {
            return !left.Equals(right);
        }
    }
}
