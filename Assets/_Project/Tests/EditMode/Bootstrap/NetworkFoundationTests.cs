using System;
using System.IO;
using DigBlocks.Core.Launch;
using DigBlocks.Networking;
using NUnit.Framework;

namespace DigBlocks.Bootstrap.Tests
{
    public sealed class NetworkFoundationTests
    {
        [Test]
        public void LaunchSettings_ReadDirectEndpointAndIdentityPath()
        {
            var settings = NetworkLaunchSettings.Parse(new[] { "--address", "192.168.1.7", "--port", "8010", "--name", "Alice", "--identity-file", "alice.dat" }, LaunchMode.RemoteClient, Path.GetTempPath());
            Assert.That(settings.Session.Address, Is.EqualTo("192.168.1.7"));
            Assert.That(settings.Session.Port, Is.EqualTo(8010));
            Assert.That(settings.Session.DisplayName, Is.EqualTo("Alice"));
            Assert.That(settings.IdentityPath, Is.EqualTo(Path.GetFullPath("alice.dat")));
        }

        [TestCase("--port", "0")]
        [TestCase("--port", "65536")]
        [TestCase("--capacity", "0")]
        [TestCase("--name", "invalid name")]
        public void LaunchSettings_RejectInvalidInput(string flag, string value)
        {
            Assert.That(() => NetworkLaunchSettings.Parse(new[] { flag, value }, LaunchMode.RemoteClient, Path.GetTempPath()), Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void LaunchSettings_RejectMissingAndDuplicateOptions()
        {
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--port" }, LaunchMode.RemoteClient, Path.GetTempPath()));
            Assert.Throws<ArgumentException>(() => NetworkLaunchSettings.Parse(new[] { "--port", "8000", "--port", "8001" }, LaunchMode.RemoteClient, Path.GetTempPath()));
        }

        [TestCase("")]
        [TestCase("not an address")]
        [TestCase("999.1.1.1")]
        public void Options_RejectInvalidAddresses(string address)
        {
            Assert.Throws<ArgumentException>(() => new NetworkSessionOptions(address, 7979, 1));
        }

        [Test]
        public void Admission_ReservesLastSlotAndReleasesIt()
        {
            var registry = new AdmissionRegistry(new NetworkSessionOptions("127.0.0.1", 7979, 1, capacity: 1));
            Assert.That(registry.Admit(10, 1, 100, "Alice", out var first), Is.EqualTo(NetworkFailure.None));
            Assert.That(registry.Admit(11, 1, 200, "Bob", out _), Is.EqualTo(NetworkFailure.ServerFull));
            registry.Release(10);
            Assert.That(registry.Admit(11, 1, 200, "Bob", out var second), Is.EqualTo(NetworkFailure.None));
            Assert.That(second.PeerId, Is.GreaterThan(first.PeerId));
            Assert.That(registry.Peers[0].OfflineXuid, Is.EqualTo(200));
        }

        [TestCase(2u, 1ul, "Alice", NetworkFailure.ProtocolMismatch)]
        [TestCase(1u, 0ul, "Alice", NetworkFailure.InvalidIdentity)]
        [TestCase(1u, 1ul, "invalid name", NetworkFailure.InvalidName)]
        public void Admission_RejectsInvalidClaims(uint protocol, ulong xuid, string name, NetworkFailure expected)
        {
            var registry = new AdmissionRegistry(new NetworkSessionOptions("127.0.0.1", 7979, 1));
            Assert.That(registry.Admit(10, protocol, xuid, name, out _), Is.EqualTo(expected));
            Assert.That(registry.Peers, Is.Empty);
        }

        [Test]
        public void Admission_DuplicateIdentityCannotReserveSecondSlot()
        {
            var registry = new AdmissionRegistry(new NetworkSessionOptions("127.0.0.1", 7979, 1));
            registry.Admit(10, 1, 100, "Alice", out var first);
            Assert.That(registry.Admit(11, 1, 100, "Other", out _), Is.EqualTo(NetworkFailure.DuplicateIdentity));
            Assert.That(registry.Admit(10, 1, 100, "Alice", out var repeated), Is.EqualTo(NetworkFailure.None));
            Assert.That(repeated.PeerId, Is.EqualTo(first.PeerId));
            Assert.That(registry.Peers.Count, Is.EqualTo(1));
        }

        [Test]
        public void Identity_PersistsAndDoesNotReplaceCorruptFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "DigBlocksIdentity-" + Guid.NewGuid());
            string path = Path.Combine(directory, "uid.dat");
            try
            {
                ulong id = OfflineIdentityStore.LoadOrCreate(path);
                Assert.That(id, Is.Not.Zero);
                Assert.That(OfflineIdentityStore.LoadOrCreate(path), Is.EqualTo(id));
                Assert.That(File.ReadAllText(path).Trim().Length, Is.EqualTo(16));
                File.WriteAllText(path, "corrupt");
                Assert.Throws<InvalidDataException>(() => OfflineIdentityStore.LoadOrCreate(path));
                Assert.That(File.ReadAllText(path), Is.EqualTo("corrupt"));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
