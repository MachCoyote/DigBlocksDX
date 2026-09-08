using System;
using System.Collections.Generic;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Networking;
using DigBlocks.Networking.NetCode;
using DigBlocks.Server.Runtime;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    public static class GameServiceComposer
    {
        public static IReadOnlyList<IGameService> Compose(LaunchOptions launchOptions, IGameLogger logger)
            => Compose(launchOptions, logger, NetworkLaunchSettings.Parse(Array.Empty<string>(), launchOptions.Mode, Application.persistentDataPath));

        public static IReadOnlyList<IGameService> Compose(LaunchOptions launchOptions, IGameLogger logger, NetworkLaunchSettings network)
        {
            if (!Enum.IsDefined(typeof(LaunchMode), launchOptions.Mode)) throw new ArgumentOutOfRangeException(nameof(launchOptions));
            bool local = launchOptions.Mode == LaunchMode.SinglePlayer;
            var services = new List<IGameService> { new DiagnosticService(logger) };
            ServerRuntime server = null;
            ClientRuntime client = null;
            if (launchOptions.Mode != LaunchMode.RemoteClient)
            {
                server = new ServerRuntime(() => NetCodeWorldFactory.CreateServerWorld(local));
                services.Add(server);
            }
            if (launchOptions.Mode != LaunchMode.DedicatedServer)
            {
                client = new ClientRuntime(() => NetCodeWorldFactory.CreateClientWorld(local));
                services.Add(client);
            }
            var role = local ? NetworkSessionRole.ClientAndServer : client != null ? NetworkSessionRole.Client : NetworkSessionRole.Server;
            var session = new NetCodeSession(role, network.Session, client == null ? null : () => client.World,
                server == null ? null : () => server.World, logger, () => OfflineIdentityStore.LoadOrCreate(network.IdentityPath));
            services.Add(session);
            services.Add(new ChunkCompanionService(session, network.BulkPort, BlockContentProvider.Load().Registry));
            return services;
        }
    }
}
