using System;
using System.Collections.Generic;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Server.Runtime;

namespace DigBlocks.Bootstrap
{
    public static class GameServiceComposer
    {
        public static IReadOnlyList<IGameService> Compose(
            LaunchOptions launchOptions,
            IGameLogger logger)
        {
            switch (launchOptions.Mode)
            {
                case LaunchMode.SinglePlayer:
                    return new IGameService[]
                    {
                        new DiagnosticService(logger),
                        new ServerRuntime(),
                        new ClientRuntime()
                    };

                case LaunchMode.RemoteClient:
                    return new IGameService[]
                    {
                        new DiagnosticService(logger),
                        new ClientRuntime()
                    };

                case LaunchMode.DedicatedServer:
                    return new IGameService[]
                    {
                        new DiagnosticService(logger),
                        new ServerRuntime()
                    };

                default:
                    throw new ArgumentOutOfRangeException(nameof(launchOptions));
            }
        }
    }
}
