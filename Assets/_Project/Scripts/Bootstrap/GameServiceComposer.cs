using System;
using System.Collections.Generic;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Networking;
using DigBlocks.Networking.NetCode;
using DigBlocks.Server.Runtime;
using DigBlocks.Voxels.Runtime;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    public static class GameServiceComposer
    {
        public static IReadOnlyList<IGameService> Compose(LaunchOptions launchOptions, IGameLogger logger)
            => Compose(launchOptions, logger, NetworkLaunchSettings.Parse(Array.Empty<string>(), launchOptions.Mode, Application.persistentDataPath));

        public static IReadOnlyList<IGameService> Compose(LaunchOptions launchOptions, IGameLogger logger, NetworkLaunchSettings network,
            IAuthoritativeChunkSource authoritativeChunkSource = null)
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
            var streamingSettings = Resources.Load<ChunkStreamingSettings>("ChunkStreamingSettings");
            var streamingOptions = streamingSettings != null ? streamingSettings.CreateOptions() : new ChunkStreamingOptions();
            var companion = new ChunkCompanionService(session, network.BulkPort, BlockContentProvider.Load().Registry,
                streamingOptions: streamingOptions, authoritativeSource: authoritativeChunkSource);
            services.Add(companion);
            //after the companion: entity residency is derived from the chunk interest it owns, and
            //services stop in reverse. Single player's server is the player's own process, so debug
            //spawns are theirs to make; a hosted or dedicated server needs a deliberate switch.
            services.Add(new EntityGhostService(session, EntityContentProvider.Load().Registry, companion,
                allowDebugSpawns: local, worldId: streamingOptions.WorldId,
                simulationDistance: streamingSettings != null ? streamingSettings.CreateSimulationDistance() : null));
            return services;
        }
    }
}
