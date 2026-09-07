using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Bootstrap.Session;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Core.Session;
using NUnit.Framework;

namespace DigBlocks.Bootstrap.Tests.Session
{
    public sealed class GameSessionControllerTests
    {
        private static readonly SessionStartRequest Request =
            new SessionStartRequest(new LaunchOptions(LaunchMode.SinglePlayer), "dev");

        [Test]
        public void StartSession_WaitsForExplicitReadiness()
        {
            var readiness = new GatedService("Client");
            GameSessionController controller = CreateController(readiness);

            UniTask<GameSessionStatus> pending = controller.StartSessionAsync(Request, CancellationToken.None);

            Assert.That(controller.Status.Phase, Is.EqualTo(GameSessionPhase.Starting));
            Assert.That(controller.Status.Description, Is.EqualTo(readiness.ReadinessDescription));

            readiness.MarkReady();

            GameSessionStatus status = pending.GetAwaiter().GetResult();

            Assert.That(status.Phase, Is.EqualTo(GameSessionPhase.Ready));
            Assert.That(controller.HostState, Is.EqualTo(GameHostState.Running));
        }

        [Test]
        public void StartSession_WhenSessionActive_DoesNotCreateSecondSession()
        {
            var readiness = new GatedService("Client");
            var factoryCalls = 0;
            var controller = new GameSessionController(
                new NullLogger(),
                _ =>
                {
                    factoryCalls++;
                    return new IGameService[] { readiness };
                });

            readiness.MarkReadyOnStart = true;
            controller.StartSessionAsync(Request, CancellationToken.None).GetAwaiter().GetResult();
            GameSessionStatus second = controller
                .StartSessionAsync(Request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(second.Phase, Is.EqualTo(GameSessionPhase.Ready));
            Assert.That(readiness.StartCount, Is.EqualTo(1));
        }

        [Test]
        public void StartSession_WhenAServiceFails_ReportsFailureAndRollsBack()
        {
            var healthy = new GatedService("Healthy") { MarkReadyOnStart = true };
            var failing = new FailingService();
            GameSessionController controller = CreateController(healthy, failing);

            GameSessionStatus status = controller
                .StartSessionAsync(Request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.That(status.Phase, Is.EqualTo(GameSessionPhase.Failed));
            Assert.That(status.Failure, Is.Not.Null);
            Assert.That(healthy.StopCount, Is.EqualTo(1));
            Assert.That(controller.HostState, Is.EqualTo(GameHostState.Created));
        }

        [Test]
        public void StopSession_StopsServicesAndReturnsToIdle()
        {
            var readiness = new GatedService("Client") { MarkReadyOnStart = true };
            GameSessionController controller = CreateController(readiness);

            controller.StartSessionAsync(Request, CancellationToken.None).GetAwaiter().GetResult();
            controller.StopSessionAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(controller.Status.Phase, Is.EqualTo(GameSessionPhase.Idle));
            Assert.That(readiness.StopCount, Is.EqualTo(1));
        }

        [Test]
        public void StartSession_AfterStop_CreatesAFreshSession()
        {
            var readiness = new GatedService("Client") { MarkReadyOnStart = true };
            var factoryCalls = 0;
            var controller = new GameSessionController(
                new NullLogger(),
                _ =>
                {
                    factoryCalls++;
                    return new IGameService[] { readiness };
                });

            controller.StartSessionAsync(Request, CancellationToken.None).GetAwaiter().GetResult();
            controller.StopSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
            GameSessionStatus second = controller
                .StartSessionAsync(Request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.That(factoryCalls, Is.EqualTo(2));
            Assert.That(second.Phase, Is.EqualTo(GameSessionPhase.Ready));
            Assert.That(readiness.StartCount, Is.EqualTo(2));
        }

        [Test]
        public void SetSimulationPaused_ForwardsToPauseSinks()
        {
            var readiness = new GatedService("Client") { MarkReadyOnStart = true };
            GameSessionController controller = CreateController(readiness);

            controller.StartSessionAsync(Request, CancellationToken.None).GetAwaiter().GetResult();
            controller.SetSimulationPausedAsync(true, CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(readiness.PauseRequests, Is.EqualTo(new[] { true }));
        }

        private static GameSessionController CreateController(params IGameService[] services)
        {
            return new GameSessionController(new NullLogger(), _ => services);
        }

        private sealed class GatedService : IGameService, ISessionReadinessSource, ISimulationPauseSink
        {
            private UniTaskCompletionSource ready;

            public GatedService(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public string ReadinessDescription => $"{Name} is waiting for world data...";

            public bool MarkReadyOnStart { get; set; }

            public int StartCount { get; private set; }

            public int StopCount { get; private set; }

            public List<bool> PauseRequests { get; } = new List<bool>();

            public UniTask StartAsync(CancellationToken cancellationToken)
            {
                StartCount++;
                ready = new UniTaskCompletionSource();
                if (MarkReadyOnStart)
                {
                    MarkReady();
                }

                return UniTask.CompletedTask;
            }

            public UniTask StopAsync(CancellationToken cancellationToken)
            {
                StopCount++;
                ready?.TrySetCanceled(cancellationToken);
                ready = null;
                return UniTask.CompletedTask;
            }

            public void MarkReady()
            {
                ready?.TrySetResult();
            }

            public UniTask WaitUntilReadyAsync(CancellationToken cancellationToken)
            {
                return ready.Task;
            }

            public UniTask SetSimulationPausedAsync(bool paused, CancellationToken cancellationToken)
            {
                PauseRequests.Add(paused);
                return UniTask.CompletedTask;
            }
        }

        private sealed class FailingService : IGameService
        {
            public string Name => "Failing";

            public UniTask StartAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Startup failed on purpose.");
            }

            public UniTask StopAsync(CancellationToken cancellationToken)
            {
                return UniTask.CompletedTask;
            }
        }

        private sealed class NullLogger : IGameLogger
        {
            public IGameLogger CreateFor(string sourceName)
            {
                return this;
            }

            public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null)
            {
            }
        }
    }
}
