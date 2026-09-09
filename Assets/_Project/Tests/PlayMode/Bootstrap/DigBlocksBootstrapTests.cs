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
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.That(owner.Terrain, Is.Not.Null);
                Assert.That(owner.Terrain.BuiltChunks, Is.EqualTo(75));
                Assert.That(owner.Terrain.Quads, Is.GreaterThan(0));
            }

            owner.RequestReturnToTitle();
            yield return WaitForApplicationState(owner, ApplicationState.Title, 30);

            Assert.That(owner.SessionStatus.Phase, Is.EqualTo(GameSessionPhase.Idle));
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Created));
            Assert.That(owner.LastFailure, Is.Null);
        }

        [UnityTest]
        public IEnumerator TerrainScene_RendersAndRestarts()
        {
            SceneManager.LoadScene("Bootstrap", LoadSceneMode.Single);
            yield return null;
            var owner = UnityEngine.Object.FindFirstObjectByType<DigBlocksBootstrap>();
            yield return WaitForApplicationState(owner, ApplicationState.Title);
            for (int session = 0; session < 2; session++)
            {
                owner.RequestPlay();
                yield return WaitForApplicationState(owner, ApplicationState.Playing, 30);
                Assert.That(owner.Terrain.BuiltChunks, Is.EqualTo(75));
                Assert.That(owner.Terrain.Quads, Is.GreaterThan(0));
                for (int frame = 0; frame < 4; frame++) yield return null;
                Assert.That(owner.Terrain.Quads, Is.GreaterThan(0), "All chunks in the initial moving interest must be meshed before Playing.");
                yield return new WaitForEndOfFrame();
                var capture = ScreenCapture.CaptureScreenshotAsTexture();
                try
                {
                    var pixels = capture.GetPixels32();
                    int terrainPixels = 0;
                    foreach (var pixel in pixels)
                        if (pixel.g > pixel.b * 1.2f && pixel.g > pixel.r * 1.1f) terrainPixels++;
                    Assert.That(terrainPixels, Is.GreaterThan(100), "Expected visible grass terrain in the rendered frame.");
                    System.IO.Directory.CreateDirectory(".utmp");
                    System.IO.File.WriteAllBytes(".utmp/terrain-world.png", capture.EncodeToPNG());
                    Debug.Log($"Terrain verification: {owner.Terrain.Quads} packed quads, {terrainPixels} grass pixels, session {session + 1}.");
                }
                finally { UnityEngine.Object.Destroy(capture); }
                owner.RequestReturnToTitle();
                yield return WaitForApplicationState(owner, ApplicationState.Title, 30);
                Assert.That(owner.LastFailure, Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator TerrainMaterialColorIsRenderedFromAnOwnedClone()
        {
            SceneManager.LoadScene("Bootstrap", LoadSceneMode.Single);
            yield return null;
            var owner = UnityEngine.Object.FindFirstObjectByType<DigBlocksBootstrap>();
            yield return WaitForApplicationState(owner, ApplicationState.Title);
            var settings = Resources.Load<DigBlocks.Client.Rendering.TerrainRenderSettings>("TerrainRenderSettings");
            var original = settings.Materials[0].Material;
            var source = new Material(original);
            settings.Materials[0].Material = source;
            try
            {
                source.SetColor("_BaseColor", Color.black);
                for (int session = 0; session < 2; session++)
                {
                    owner.RequestPlay();
                    yield return WaitForApplicationState(owner, ApplicationState.Playing, 30);
                    //changing the template cannot change the already-owned clone; the next session picks it up.
                    source.SetColor("_BaseColor", Color.white);
                    for (int frame = 0; frame < 4; frame++) yield return null;
                    yield return new WaitForEndOfFrame();
                    var capture = ScreenCapture.CaptureScreenshotAsTexture();
                    try
                    {
                        int grass = 0;
                        foreach (var pixel in capture.GetPixels32())
                            if (pixel.g > pixel.b * 1.2f && pixel.g > pixel.r * 1.1f) grass++;
                        if (session == 0) Assert.That(grass, Is.LessThan(100), "Black material must suppress grass albedo, even after its template changes.");
                        else Assert.That(grass, Is.GreaterThan(100), "The next clone must inherit the white template.");
                    }
                    finally { UnityEngine.Object.Destroy(capture); }
                    owner.RequestReturnToTitle();
                    yield return WaitForApplicationState(owner, ApplicationState.Title, 30);
                    Assert.That(source != null && original != null, Is.True, "Shutdown must not destroy source materials.");
                }
            }
            finally
            {
                settings.Materials[0].Material = original;
                UnityEngine.Object.Destroy(source);
            }
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
