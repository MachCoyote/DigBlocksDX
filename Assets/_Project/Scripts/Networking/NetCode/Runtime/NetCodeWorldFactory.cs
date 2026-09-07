using Unity.Entities;
using Unity.NetCode;
using System;

namespace DigBlocks.Networking.NetCode
{
    public static class NetCodeWorldFactory
    {
        private const int DefaultSimulationTickRate = 30;

        public static World CreateClientWorld()
        {
            return CreateClientWorld(false);
        }

        public static World CreateClientWorld(bool privateConnection) => Create(false, privateConnection);

        public static World CreateServerWorld()
        {
            return CreateServerWorld(false);
        }

        public static World CreateServerWorld(bool privateConnection) => Create(true, privateConnection);

        private static World Create(bool server, bool privateConnection)
        {
            //world creation is main-thread-only; do not leave a global transport override installed.
            var previous = NetworkStreamReceiveSystem.DriverConstructor;
            World world;
            try
            {
                NetworkStreamReceiveSystem.DriverConstructor = new SessionDriverConstructor(privateConnection);
                world = server ? ClientServerBootstrap.CreateServerWorld("DigBlocks Server World")
                    : ClientServerBootstrap.CreateClientWorld("DigBlocks Client World");
            }
            finally { NetworkStreamReceiveSystem.DriverConstructor = previous; }
            if (!server) return world;

            var tickRate = new ClientServerTickRate
            {
                SimulationTickRate = DefaultSimulationTickRate,
                NetworkTickRate = DefaultSimulationTickRate
            };

            tickRate.ResolveDefaults();
            world.EntityManager.CreateSingleton(tickRate);

            return world;
        }

        private sealed class SessionDriverConstructor : INetworkStreamDriverConstructor
        {
            private readonly bool ipc;
            public SessionDriverConstructor(bool ipc) => this.ipc = ipc;
            public void CreateClientDriver(World world, ref NetworkDriverStore driver, NetDebug debug)
            {
                using var settings = DefaultDriverBuilder.GetNetworkClientSettings();
                if (ipc) DefaultDriverBuilder.RegisterClientIpcDriver(world, ref driver, debug, settings);
                else DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driver, debug, settings);
            }
            public void CreateServerDriver(World world, ref NetworkDriverStore driver, NetDebug debug)
            {
                using var settings = DefaultDriverBuilder.GetNetworkServerSettings();
                if (ipc) DefaultDriverBuilder.RegisterServerIpcDriver(world, ref driver, debug, settings);
                else DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driver, debug, settings);
            }
        }
    }
}
