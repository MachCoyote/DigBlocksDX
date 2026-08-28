using System;
using DigBlocks.Core.Launch;
using NUnit.Framework;

namespace DigBlocks.Core.Tests.Launch
{
    public sealed class LaunchModeResolverTests
    {
        [TestCase(LaunchMode.SinglePlayer)]
        [TestCase(LaunchMode.RemoteClient)]
        [TestCase(LaunchMode.DedicatedServer)]
        public void Resolve_WithoutOverride_ReturnsConfiguredDefault(LaunchMode configuredDefault)
        {
            LaunchOptions options = LaunchModeResolver.Resolve(
                configuredDefault,
                Array.Empty<string>(),
                false);

            Assert.That(options.Mode, Is.EqualTo(configuredDefault));
        }

        [Test]
        public void Resolve_ServerBuildWithoutOverride_ReturnsDedicatedServer()
        {
            LaunchOptions options = LaunchModeResolver.Resolve(
                LaunchMode.SinglePlayer,
                Array.Empty<string>(),
                true);

            Assert.That(options.Mode, Is.EqualTo(LaunchMode.DedicatedServer));
        }

        [TestCase("--singleplayer", LaunchMode.SinglePlayer)]
        [TestCase("--client", LaunchMode.RemoteClient)]
        [TestCase("--server", LaunchMode.DedicatedServer)]
        [TestCase("--SERVER", LaunchMode.DedicatedServer)]
        public void Resolve_WithModeFlag_ReturnsOverride(string flag, LaunchMode expected)
        {
            LaunchOptions options = LaunchModeResolver.Resolve(
                LaunchMode.SinglePlayer,
                new[] { "app", flag },
                false);

            Assert.That(options.Mode, Is.EqualTo(expected));
        }

        [Test]
        public void Resolve_WithUnknownArguments_IgnoresThem()
        {
            LaunchOptions options = LaunchModeResolver.Resolve(
                LaunchMode.RemoteClient,
                new[] { "app", "-logFile", "server.log" },
                false);

            Assert.That(options.Mode, Is.EqualTo(LaunchMode.RemoteClient));
        }

        [Test]
        public void Resolve_WithMultipleModeFlags_Throws()
        {
            Assert.That(
                () => LaunchModeResolver.Resolve(
                    LaunchMode.SinglePlayer,
                    new[] { "app", "--client", "--server" },
                    false),
                Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void Resolve_WithDuplicateModeFlag_Throws()
        {
            Assert.That(
                () => LaunchModeResolver.Resolve(
                    LaunchMode.SinglePlayer,
                    new[] { "app", "--client", "--client" },
                    false),
                Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void Resolve_WithNullArguments_Throws()
        {
            Assert.That(
                () => LaunchModeResolver.Resolve(LaunchMode.SinglePlayer, null, false),
                Throws.TypeOf<ArgumentNullException>());
        }
    }
}
