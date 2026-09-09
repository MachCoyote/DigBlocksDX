using System;
using System.IO;
using DigBlocks.ChunkProtocol;
using DigBlocks.Core.Launch;
using DigBlocks.Networking;
using DigBlocks.Networking.NetCode;
using NUnit.Framework;
using UnityEngine;
namespace DigBlocks.Bootstrap.Tests
{
    public sealed class ChunkLaunchTests
    {
        //the authored distances are tuning, not a contract. What must hold is that whatever is authored
        //stays constructible and inside the residency cap, since exceeding it fails the session at startup.
        [Test]
        public void AuthoredChunkDistancesCreateIndependentThreeDimensionalInterest()
        {
            var settings = Resources.Load<ChunkStreamingSettings>("ChunkStreamingSettings");
            Assert.That(settings, Is.Not.Null);
            var options = settings.CreateOptions();
            Assert.That(options.HorizontalRadius, Is.GreaterThanOrEqualTo(0));
            Assert.That(options.VerticalRadius, Is.GreaterThanOrEqualTo(0));
            long width = 2L * options.HorizontalRadius + 1;
            long count = width * width * (2L * options.VerticalRadius + 1);
            //CreateOptions builds the interest itself, so reaching here already proves it is constructible.
            Assert.That(count, Is.LessThanOrEqualTo(ChunkInterest.MaximumChunks),
                "Authored render distances must fit the admitted residency and the renderer's chunk slots.");
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
