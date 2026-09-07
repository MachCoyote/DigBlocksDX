using System.Collections;
using System;
using System.Collections.Generic;
using System.Threading;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Core.Hosting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Cysharp.Threading.Tasks;
using System.Reflection;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class DigBlocksBootstrapTests
    {
        [UnityTest]
        public IEnumerator ConcurrentBootstrapShutdownAwaitersShareCleanup()
        {
            var ownerObject = new GameObject("Concurrent Shutdown Bootstrap");
            var owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            yield return WaitForStartup(owner);
            var begin = typeof(DigBlocksBootstrap).GetMethod("BeginShutdownAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var first = (UniTask)begin.Invoke(owner, null);
            var second = (UniTask)begin.Invoke(owner, null);
            yield return UniTask.WhenAll(first, second).ToCoroutine();
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Stopped));
            Assert.That(owner.LastFailure, Is.Null);
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (var bootstrap in UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>())
                UnityEngine.Object.Destroy(bootstrap.gameObject);
            //destruction drains the session before disposing runtime worlds.
            double deadline = Time.realtimeSinceStartupAsDouble + 4;
            while (Unity.NetCode.ClientServerBootstrap.ClientWorlds.Count > 0 || Unity.NetCode.ClientServerBootstrap.ServerWorlds.Count > 0)
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "Bootstrap leaked a network world.");
                yield return null;
            }
        }

        private static IEnumerator WaitForStartup(DigBlocksBootstrap bootstrap)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 12;
            while (bootstrap.HostState is GameHostState.Created or GameHostState.Starting)
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "Bootstrap startup did not settle.");
                yield return null;
            }
        }
        [UnityTest]
        public IEnumerator Bootstrap_StartsHostAndRejectsDuplicate()
        {
            var ownerObject = new GameObject("Bootstrap Owner");
            DigBlocksBootstrap owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            var duplicateObject = new GameObject("Bootstrap Duplicate");
            duplicateObject.AddComponent<DigBlocksBootstrap>();

            yield return null;

            yield return WaitForStartup(owner);

            DigBlocksBootstrap[] bootstraps = UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>();
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0], Is.SameAs(owner));
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Running));
            Assert.That(owner.LastFailure, Is.Null);

            UnityEngine.Object.Destroy(ownerObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SampleScene_ContainsRunningBootstrap()
        {
            SceneManager.LoadScene("SampleScene", LoadSceneMode.Single);
            yield return null;

            DigBlocksBootstrap[] bootstraps = UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>();
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            yield return WaitForStartup(bootstraps[0]);
            Assert.That(bootstraps[0].HostState, Is.EqualTo(GameHostState.Running));
            Assert.That(bootstraps[0].LastFailure, Is.Null);
        }

        [Test]
        public void DiagnosticService_StartAsync_LogsItsNameAsTheSource()
        {
            var logger = new CapturingLogger();
            var service = new DiagnosticService(logger);

            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(logger.Messages, Is.EqualTo(new[] { "[DiagnosticService] Started." }));
        }

        private sealed class CapturingLogger : IGameLogger
        {
            public List<string> Messages { get; } = new List<string>();

            public IGameLogger CreateFor(string sourceName)
            {
                return new ScopedGameLogger(this, sourceName);
            }

            public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null)
            {
                Messages.Add(message);
            }
        }
    }
}
