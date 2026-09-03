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

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class DigBlocksBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_StartsHostAndRejectsDuplicate()
        {
            var ownerObject = new GameObject("Bootstrap Owner");
            DigBlocksBootstrap owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            var duplicateObject = new GameObject("Bootstrap Duplicate");
            duplicateObject.AddComponent<DigBlocksBootstrap>();

            yield return null;

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
