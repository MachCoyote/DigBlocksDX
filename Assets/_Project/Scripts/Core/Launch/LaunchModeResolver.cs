using System;
using System.Collections.Generic;

namespace DigBlocks.Core.Launch
{
    public static class LaunchModeResolver
    {
        public static LaunchOptions Resolve(
            LaunchMode configuredDefault,
            IReadOnlyList<string> arguments,
            bool isServerBuild)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            LaunchMode resolvedMode = isServerBuild
                ? LaunchMode.DedicatedServer
                : configuredDefault;
            int overrideCount = 0;

            for (int index = 0; index < arguments.Count; index++)
            {
                if (!TryResolveFlag(arguments[index], out LaunchMode overrideMode))
                {
                    continue;
                }

                overrideCount++;
                if (overrideCount > 1)
                {
                    throw new ArgumentException(
                        "Only one DigBlocks launch mode flag may be supplied.",
                        nameof(arguments));
                }

                resolvedMode = overrideMode;
            }

            return new LaunchOptions(resolvedMode);
        }

        private static bool TryResolveFlag(string argument, out LaunchMode mode)
        {
            if (string.Equals(argument, "--singleplayer", StringComparison.OrdinalIgnoreCase))
            {
                mode = LaunchMode.SinglePlayer;
                return true;
            }

            if (string.Equals(argument, "--client", StringComparison.OrdinalIgnoreCase))
            {
                mode = LaunchMode.RemoteClient;
                return true;
            }

            if (string.Equals(argument, "--server", StringComparison.OrdinalIgnoreCase))
            {
                mode = LaunchMode.DedicatedServer;
                return true;
            }

            mode = default;
            return false;
        }
    }
}
