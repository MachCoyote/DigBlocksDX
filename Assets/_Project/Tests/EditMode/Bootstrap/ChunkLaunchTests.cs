using System;
using System.IO;
using DigBlocks.Core.Launch;
using DigBlocks.Networking;
using DigBlocks.Networking.NetCode;
using NUnit.Framework;
using UnityEngine;
namespace DigBlocks.Bootstrap.Tests
{
    public sealed class ChunkLaunchTests
    {
        [Test]
        public void AuthoredChunkDistancesCreateIndependentThreeDimensionalInterest()
        {
            var settings = Resources.Load<ChunkStreamingSettings>("ChunkStreamingSettings");
            Assert.That(settings, Is.Not.Null);
            var options = settings.CreateOptions();
            Assert.That(options.HorizontalRadius, Is.EqualTo(2));
            Assert.That(options.VerticalRadius, Is.EqualTo(1));
            Assert.That((2 * options.HorizontalRadius + 1) * (2 * options.HorizontalRadius + 1) *
                (2 * options.VerticalRadius + 1), Is.EqualTo(75));
        }

        [Test]
        public void DedicatedServerDerivesOrOverridesBulkPort()
        {
            var path = Path.GetTempPath();
            Assert.That(NetworkLaunchSettings.Parse(new[] { "--port", "8100" }, LaunchMode.DedicatedServer, path).BulkPort, Is.EqualTo(8101));
            Assert.That(NetworkLaunchSettings.Parse(new[] { "--port", "65535", "--bulk-port", "8102" }, LaunchMode.DedicatedServer, path).BulkPort, Is.EqualTo(8102));
            Assert.That(NetworkLaunchSettings.Parse(Array.Empty<string>(), LaunchMode.RemoteClient, path).BulkPort, Is.Null);
            Assert.That(NetworkLaunchSettings.Parse(Array.Empty<string>(), LaunchMode.SinglePlayer, path).BulkPort, Is.Null);
        }
        [Test]
        public void BulkPortRejectsOverflowConflictInvalidAndWrongRoleOverrides()
        {
            var path = Path.GetTempPath();
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--port", "65535" }, LaunchMode.DedicatedServer, path));
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--port", "8100", "--bulk-port", "8100" }, LaunchMode.DedicatedServer, path));
            foreach (string value in new[] { "0", "65536", "-1", "abc" })
                Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--bulk-port", value }, LaunchMode.DedicatedServer, path));
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--bulk-port", "8100" }, LaunchMode.SinglePlayer, path));
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--bulk-port", "8100" }, LaunchMode.RemoteClient, path));
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--bulk-port", "8100", "--bulk-port", "8101" }, LaunchMode.DedicatedServer, path));
        }
    }
}
