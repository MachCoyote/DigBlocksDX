using System.Collections;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Bootstrap.Diagnostics;
using DigBlocks.Client.Flow;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class DigBlocksBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_KeepsOneInstanceAndReachesTitle()
        {
            var ownerObject = new GameObject("Bootstrap Owner");
            DigBlocksBootstrap owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            var duplicateObject = new GameObject("Bootstrap Duplicate");
            duplicateObject.AddComponent<DigBlocksBootstrap>();

            yield return WaitForApplicationState(owner, ApplicationState.Title);

            DigBlocksBootstrap[] bootstraps = UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>();
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0], Is.SameAs(owner));
            Assert.That(owner.LastFailure, Is.Null);

            //a client creates no session until the play action is taken
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Created));
            Assert.That(owner.SessionStatus.Phase, Is.EqualTo(GameSessionPhase.Idle));
        }

        [UnityTest]
        public IEnumerator SampleScene_BootstrapReachesTitle()
        {
            SceneManager.LoadScene("SampleScene", LoadSceneMode.Single);
            yield return null;

            DigBlocksBootstrap[] bootstraps = UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>();
            Assert.That(bootstraps, Has.Length.EqualTo(1));

            yield return WaitForApplicationState(bootstraps[0], ApplicationState.Title);
            Assert.That(bootstraps[0].LastFailure, Is.Null);
        }

        [UnityTest]
        public IEnumerator Play_StartsSessionThenReturnToTitleStopsIt()
        {
            var ownerObject = new GameObject("Session Flow Bootstrap");
            DigBlocksBootstrap owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            yield return WaitForApplicationState(owner, ApplicationState.Title);

            owner.RequestPlay();
            yield return WaitForApplicationState(owner, ApplicationState.Playing, 30);

            Assert.That(owner.SessionStatus.Phase, Is.EqualTo(GameSessionPhase.Ready));
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Running));
            Assert.That(owner.NetworkSession, Is.Not.Null);

            owner.RequestReturnToTitle();
            yield return WaitForApplicationState(owner, ApplicationState.Title, 30);

            Assert.That(owner.SessionStatus.Phase, Is.EqualTo(GameSessionPhase.Idle));
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Created));
            Assert.That(owner.LastFailure, Is.Null);
        }

        [UnityTest]
        public IEnumerator ConcurrentBootstrapShutdownAwaitersShareCleanup()
        {
            var ownerObject = new GameObject("Concurrent Shutdown Bootstrap");
            var owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            yield return WaitForApplicationState(owner, ApplicationState.Title);

            MethodInfo begin = typeof(DigBlocksBootstrap).GetMethod(
                "BeginShutdownAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var first = (UniTask)begin.Invoke(owner, null);
            var second = (UniTask)begin.Invoke(owner, null);

            yield return UniTask.WhenAll(first, second).ToCoroutine();

            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Created));
            Assert.That(owner.LastFailure, Is.Null);
        }

        [Test]
        public void DiagnosticService_StartAsync_LogsItsNameAsTheSource()
        {
            var logger = new CapturingLogger();
            var service = new DiagnosticService(logger);

            service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(logger.Messages, Is.EqualTo(new[] { "[DiagnosticService] Started." }));
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (var bootstrap in UnityEngine.Object.FindObjectsByType<DigBlocksBootstrap>())
                UnityEngine.Object.Destroy(bootstrap.gameObject);
            //destruction drains the session before disposing runtime worlds.
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (Unity.NetCode.ClientServerBootstrap.ClientWorlds.Count > 0 || Unity.NetCode.ClientServerBootstrap.ServerWorlds.Count > 0)
            {
                Assert.That(Time.realtimeSinceStartupAsDouble, Is.LessThan(deadline), "Bootstrap leaked a network world.");
                yield return null;
            }
        }

        private static IEnumerator WaitForApplicationState(
            DigBlocksBootstrap bootstrap,
            ApplicationState expected,
            double timeoutSeconds = 12)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (bootstrap.CurrentApplicationState != expected)
            {
                Assert.That(
                    Time.realtimeSinceStartupAsDouble,
                    Is.LessThan(deadline),
                    $"The application stayed in {bootstrap.CurrentApplicationState} instead of reaching {expected}. Session: {bootstrap.SessionStatus}");
                yield return null;
            }
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
