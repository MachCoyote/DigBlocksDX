using System;

namespace DigBlocks.Client.UI
{
    //identifies a menu independently of how it is authored so prefab and generated menus can share one registry
    public readonly struct MenuId : IEquatable<MenuId>
    {
        private readonly string value;

        public MenuId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A menu identifier is required.", nameof(value));
            }

            this.value = value;
        }

        public string Value => value ?? string.Empty;

        public bool IsValid => !string.IsNullOrEmpty(value);

        public bool Equals(MenuId other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is MenuId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }

        public static bool operator ==(MenuId left, MenuId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MenuId left, MenuId right)
        {
            return !left.Equals(right);
        }
    }
}
