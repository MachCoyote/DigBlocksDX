using System;
using System.Collections.Generic;
using DigBlocks.Core.Launch;
using System.Globalization;
using System.IO;

namespace DigBlocks.Networking
{
    public readonly struct NetworkLaunchSettings
    {
        public NetworkSessionOptions Session { get; }
        public string IdentityPath { get; }
        public ushort? BulkPort { get; }
        public NetworkLaunchSettings(NetworkSessionOptions session, string identityPath, ushort? bulkPort = null)
        {
            Session = session;
            IdentityPath = identityPath;
            BulkPort = bulkPort;
        }

        public static NetworkLaunchSettings Parse(IReadOnlyList<string> arguments, LaunchMode mode, string persistentDirectory)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--address", "--bind", "--port", "--name", "--capacity", "--identity-file", "--bulk-port" };
            for (int i = 0; i < arguments.Count; i++)
            {
                string flag = arguments[i];
                if (!flags.Contains(flag)) continue;
                if (values.ContainsKey(flag)) throw new ArgumentException($"Duplicate option: {flag}.");
                if (++i >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith("--"))
                    throw new ArgumentException($"Missing value for {flag}.");
                values.Add(flag, arguments[i]);
            }

            string Get(string flag, string fallback) => values.TryGetValue(flag, out var value) ? value : fallback;
            if (!ushort.TryParse(Get("--port", "7979"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
                throw new ArgumentException("--port must be in 1..65535.");
            if (!int.TryParse(Get("--capacity", "32"), NumberStyles.None, CultureInfo.InvariantCulture, out var capacity))
                throw new ArgumentException("--capacity must be an integer in 1..1024.");
            if (mode == LaunchMode.SinglePlayer && (values.ContainsKey("--address") || values.ContainsKey("--bind") || values.ContainsKey("--port")))
                throw new ArgumentException("Singleplayer uses a private endpoint; use --client or --server for direct connections.");
            ushort? bulkPort = null;
            if (values.TryGetValue("--bulk-port", out string bulkValue))
            {
                if (mode != LaunchMode.DedicatedServer) throw new ArgumentException("Only the dedicated server chooses --bulk-port; clients use its offer and singleplayer uses IPC.");
                if (!ushort.TryParse(bulkValue, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed) || parsed == 0)
                    throw new ArgumentException("--bulk-port must be in 1..65535.");
                bulkPort = parsed;
            }
            if (mode == LaunchMode.DedicatedServer)
            {
                if (!bulkPort.HasValue && port == ushort.MaxValue) throw new ArgumentException("--port 65535 requires an explicit --bulk-port.");
                bulkPort ??= (ushort)(port + 1);
                if (bulkPort == port) throw new ArgumentException("Game and bulk UDP ports must differ.");
            }
            string identity = Path.GetFullPath(Get("--identity-file", Path.Combine(persistentDirectory, "uid.dat")));
            return new NetworkLaunchSettings(new NetworkSessionOptions(Get("--address", "127.0.0.1"), port, 1,
                Get("--name", "Player"), capacity, Get("--bind", "0.0.0.0")), identity, bulkPort);
        }
    }
}
