using System;
using System.Linq;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Client.Runtime;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Server.Runtime;
using NUnit.Framework;

namespace DigBlocks.Bootstrap.Tests
{
    public sealed class GameServiceComposerTests
    {
        [Test]
        public void Compose_SinglePlayer_ContainsServerThenClient()
        {
            Type[] serviceTypes = Compose(LaunchMode.SinglePlayer);

            Assert.That(serviceTypes, Is.EqualTo(new[]
            {
                typeof(DiagnosticService),
                typeof(ServerRuntime),
                typeof(ClientRuntime)
            }));
        }

        [Test]
        public void Compose_RemoteClient_ContainsOnlyClientRuntime()
        {
            Type[] serviceTypes = Compose(LaunchMode.RemoteClient);

            Assert.That(serviceTypes, Is.EqualTo(new[]
            {
                typeof(DiagnosticService),
                typeof(ClientRuntime)
            }));
        }

        [Test]
        public void Compose_DedicatedServer_ContainsOnlyServerRuntime()
        {
            Type[] serviceTypes = Compose(LaunchMode.DedicatedServer);

            Assert.That(serviceTypes, Is.EqualTo(new[]
            {
                typeof(DiagnosticService),
                typeof(ServerRuntime)
            }));
        }

        private static Type[] Compose(LaunchMode mode)
        {
            return GameServiceComposer
                .Compose(new LaunchOptions(mode), new NullLogger())
                .Select(service => service.GetType())
                .ToArray();
        }

        private sealed class NullLogger : IGameLogger
        {
            public IGameLogger CreateFor(string sourceName)
            {
                return this;
            }

            public void Log(
                string message,
                GameLogLevel level = GameLogLevel.Information,
                Exception exception = null)
            {
            }
        }
    }
}
