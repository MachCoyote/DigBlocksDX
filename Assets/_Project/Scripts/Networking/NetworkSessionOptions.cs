using System;
using System.Net;
using System.Net.Sockets;

namespace DigBlocks.Networking
{
    public readonly struct NetworkSessionOptions
    {
        public NetworkSessionOptions(
            string address,
            ushort port,
            uint protocolVersion,
            string displayName = "Player",
            int capacity = 32,
            string bindAddress = "0.0.0.0",
            double startupTimeoutSeconds = 10,
            double shutdownTimeoutSeconds = 2)
        {
            ValidateAddress(address, nameof(address));
            ValidateAddress(bindAddress, nameof(bindAddress));
            if (protocolVersion == 0) throw new ArgumentOutOfRangeException(nameof(protocolVersion));
            if (!IsValidName(displayName)) throw new ArgumentException("Use 1-16 ASCII letters, digits or underscores.", nameof(displayName));
            if (capacity < 1 || capacity > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!(startupTimeoutSeconds > 0 && startupTimeoutSeconds <= 120)) throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds));
            if (!(shutdownTimeoutSeconds > 0 && shutdownTimeoutSeconds <= 30)) throw new ArgumentOutOfRangeException(nameof(shutdownTimeoutSeconds));
            Address = address;
            Port = port;
            ProtocolVersion = protocolVersion;
            DisplayName = displayName;
            Capacity = capacity;
            BindAddress = bindAddress;
            StartupTimeoutSeconds = startupTimeoutSeconds;
            ShutdownTimeoutSeconds = shutdownTimeoutSeconds;
        }

        public string Address { get; }
        public ushort Port { get; }
        public uint ProtocolVersion { get; }
        public string DisplayName { get; }
        public int Capacity { get; }
        public string BindAddress { get; }
        public double StartupTimeoutSeconds { get; }
        public double ShutdownTimeoutSeconds { get; }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 16) return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }

        private static void ValidateAddress(string address, string parameter)
        {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || address.Split('.').Length != 4)
                throw new ArgumentException("An IPv4 literal is required for this direct-connect slice.", parameter);
        }
    }
}
