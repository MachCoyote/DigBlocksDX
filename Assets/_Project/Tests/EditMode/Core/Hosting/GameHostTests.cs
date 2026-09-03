using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using NUnit.Framework;

namespace DigBlocks.Core.Tests.Hosting
{
    public sealed class GameHostTests
    {
        [Test]
        public void GameServiceLifecycleMethods_ReturnUniTask()
        {
            Assert.That(
                typeof(IGameService).GetMethod(nameof(IGameService.StartAsync)).ReturnType,
                Is.EqualTo(typeof(UniTask)));
            Assert.That(
                typeof(IGameService).GetMethod(nameof(IGameService.StopAsync)).ReturnType,
                Is.EqualTo(typeof(UniTask)));
        }

        [Test]
        public async Task StartAsync_StartsServicesInRegistrationOrder()
        {
            var events = new List<string>();
            var first = new RecordingGameService("first", events);
            var second = new RecordingGameService("second", events);
            var host = CreateHost(first, second);

            await host.StartAsync(CancellationToken.None);

            Assert.That(events, Is.EqualTo(new[] { "start:first", "start:second" }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Running));
        }

        [Test]
        public async Task StartAsync_WithPreCancelledToken_DoesNotStartServices()
        {
            var events = new List<string>();
            var host = CreateHost(new RecordingGameService("unused", events));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            OperationCanceledException actual = null;
            try
            {
                await host.StartAsync(cancellation.Token);
            }
            catch (OperationCanceledException exception)
            {
                actual = exception;
            }

            Assert.That(actual, Is.Not.Null);
            Assert.That(events, Is.Empty);
            Assert.That(host.State, Is.EqualTo(GameHostState.Faulted));
        }

        [Test]
        public void StartAsync_WhenServiceFails_RollsBackStartedServicesInReverseOrder()
        {
            var events = new List<string>();
            var expected = new InvalidOperationException("startup failed");
            var first = new RecordingGameService("first", events);
            var second = new RecordingGameService("second", events);
            var third = new RecordingGameService("third", events)
            {
                StartBehavior = _ => UniTask.FromException(expected)
            };
            var host = CreateHost(first, second, third);

            InvalidOperationException actual = Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(CancellationToken.None).AsTask());

            Assert.That(actual, Is.SameAs(expected));
            Assert.That(events, Is.EqualTo(new[]
            {
                "start:first", "start:second", "start:third", "stop:second", "stop:first"
            }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Faulted));
        }

        [Test]
        public async Task StartAsync_WhenCancelled_RollsBackAndPreservesCancellation()
        {
            var events = new List<string>();
            using var cancellation = new CancellationTokenSource();
            var first = new RecordingGameService("first", events);
            var second = new RecordingGameService("second", events)
            {
                StartBehavior = token =>
                {
                    cancellation.Cancel();
                    return UniTask.FromCanceled(token);
                }
            };
            var host = CreateHost(first, second);

            OperationCanceledException actual = null;
            try
            {
                await host.StartAsync(cancellation.Token);
            }
            catch (OperationCanceledException exception)
            {
                actual = exception;
            }

            Assert.That(actual, Is.Not.Null);
            Assert.That(events, Is.EqualTo(new[] { "start:first", "start:second", "stop:first" }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Faulted));
        }

        [Test]
        public void StartAsync_WhenRollbackFails_ReportsPrimaryAndRollbackFailures()
        {
            var events = new List<string>();
            var startupFailure = new InvalidOperationException("startup failed");
            var rollbackFailure = new InvalidOperationException("rollback failed");
            var first = new RecordingGameService("first", events)
            {
                StopBehavior = _ => UniTask.FromException(rollbackFailure)
            };
            var second = new RecordingGameService("second", events)
            {
                StartBehavior = _ => UniTask.FromException(startupFailure)
            };
            var host = CreateHost(first, second);

            GameHostStartException actual = Assert.ThrowsAsync<GameHostStartException>(
                () => host.StartAsync(CancellationToken.None).AsTask());

            Assert.That(actual.InnerException, Is.SameAs(startupFailure));
            Assert.That(actual.RollbackErrors, Is.EqualTo(new[] { rollbackFailure }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Faulted));
        }

        [Test]
        public async Task EmptyHost_StartsAndStops()
        {
            var host = CreateHost();

            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);

            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StopAsync_StopsServicesInReverseRegistrationOrder()
        {
            var events = new List<string>();
            var host = CreateHost(
                new RecordingGameService("first", events),
                new RecordingGameService("second", events));
            await host.StartAsync(CancellationToken.None);

            await host.StopAsync(CancellationToken.None);

            Assert.That(events, Is.EqualTo(new[]
            {
                "start:first", "start:second", "stop:second", "stop:first"
            }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StopAsync_WhenLoggerThrows_StillStopsService()
        {
            var events = new List<string>();
            var service = new RecordingGameService("service", events);
            var logger = new ThrowingLogger(message => message.Contains("Stopping"));
            var host = new GameHost(new[] { service }, logger);
            await host.StartAsync(CancellationToken.None);

            await host.StopAsync(CancellationToken.None);

            Assert.That(events, Is.EqualTo(new[] { "start:service", "stop:service" }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StartAsync_LogsTheAffectedServiceNameAsTheSource()
        {
            var events = new List<string>();
            var service = new RecordingGameService("example-service", events);
            var logger = new RecordingLogger();
            var host = new GameHost(new[] { service }, logger);

            await host.StartAsync(CancellationToken.None);

            Assert.That(logger.Entries, Has.Count.EqualTo(1));
            Assert.That(logger.Entries[0].Message, Is.EqualTo("[example-service] Starting."));
        }

        [Test]
        public void StartAsync_WhenRollbackLoggerThrows_StillRollsBackService()
        {
            var events = new List<string>();
            var first = new RecordingGameService("first", events);
            var second = new RecordingGameService("second", events)
            {
                StartBehavior = _ => UniTask.FromException(new InvalidOperationException("startup failed"))
            };
            var logger = new ThrowingLogger(message => message.Contains("Rolling back"));
            var host = new GameHost(new IGameService[] { first, second }, logger);

            Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(CancellationToken.None).AsTask());

            Assert.That(events, Does.Contain("stop:first"));
            Assert.That(host.State, Is.EqualTo(GameHostState.Faulted));
        }

        [Test]
        public async Task StopAsync_WhenServiceObservesCancellation_PreservesCancellationAfterCleanup()
        {
            var events = new List<string>();
            using var cancellation = new CancellationTokenSource();
            var first = new RecordingGameService("first", events);
            var second = new RecordingGameService("second", events)
            {
                StopBehavior = token => UniTask.FromCanceled(token)
            };
            var host = CreateHost(first, second);
            host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            cancellation.Cancel();

            OperationCanceledException actual = null;
            try
            {
                await host.StopAsync(cancellation.Token);
            }
            catch (OperationCanceledException exception)
            {
                actual = exception;
            }

            Assert.That(actual, Is.Not.Null);
            Assert.That(events, Is.EqualTo(new[]
            {
                "start:first", "start:second", "stop:second", "stop:first"
            }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StopAsync_FromCreated_DoesNotCallServices()
        {
            var events = new List<string>();
            var host = CreateHost(new RecordingGameService("unused", events));

            await host.StopAsync(CancellationToken.None);

            Assert.That(events, Is.Empty);
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StopAsync_WhenCalledTwice_IsIdempotent()
        {
            var events = new List<string>();
            var host = CreateHost(new RecordingGameService("service", events));
            await host.StartAsync(CancellationToken.None);

            await host.StopAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);

            Assert.That(events, Is.EqualTo(new[] { "start:service", "stop:service" }));
        }

        [Test]
        public void StopAsync_WhenServicesFail_StopsAllAndAggregatesFailures()
        {
            var events = new List<string>();
            var firstFailure = new InvalidOperationException("first failed");
            var secondFailure = new InvalidOperationException("second failed");
            var first = new RecordingGameService("first", events)
            {
                StopBehavior = _ => UniTask.FromException(firstFailure)
            };
            var second = new RecordingGameService("second", events)
            {
                StopBehavior = _ => UniTask.FromException(secondFailure)
            };
            var host = CreateHost(first, second);
            host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            AggregateException actual = Assert.ThrowsAsync<AggregateException>(
                () => host.StopAsync(CancellationToken.None).AsTask());

            Assert.That(actual.InnerExceptions, Is.EqualTo(new[] { secondFailure, firstFailure }));
            Assert.That(events, Is.EqualTo(new[]
            {
                "start:first", "start:second", "stop:second", "stop:first"
            }));
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        [Test]
        public async Task StartAsync_AfterStop_Throws()
        {
            var host = CreateHost();
            await host.StopAsync(CancellationToken.None);

            Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(CancellationToken.None).AsTask());
        }

        [Test]
        public async Task LifecycleCalls_WhileStartupIsActive_AreRejected()
        {
            var events = new List<string>();
            var releaseStartup = new UniTaskCompletionSource<bool>();
            var service = new RecordingGameService("blocking", events)
            {
                StartBehavior = _ => releaseStartup.Task
            };
            var host = CreateHost(service);
            UniTask startup = host.StartAsync(CancellationToken.None);

            Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(CancellationToken.None).AsTask());
            Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StopAsync(CancellationToken.None).AsTask());

            releaseStartup.TrySetResult(true);
            await startup;
            Assert.That(host.State, Is.EqualTo(GameHostState.Running));
        }

        [Test]
        public async Task StopAsync_FromFaulted_RetriesOnlyRollbackFailures()
        {
            var events = new List<string>();
            int stopAttempts = 0;
            var first = new RecordingGameService("first", events)
            {
                StopBehavior = _ => ++stopAttempts == 1
                    ? UniTask.FromException(new InvalidOperationException("first rollback failed"))
                    : UniTask.CompletedTask
            };
            var second = new RecordingGameService("second", events)
            {
                StartBehavior = _ => UniTask.FromException(new InvalidOperationException("startup failed"))
            };
            var host = CreateHost(first, second);
            Assert.ThrowsAsync<GameHostStartException>(
                () => host.StartAsync(CancellationToken.None).AsTask());

            await host.StopAsync(CancellationToken.None);

            Assert.That(events.Count(value => value == "stop:first"), Is.EqualTo(2));
            Assert.That(host.State, Is.EqualTo(GameHostState.Stopped));
        }

        private static GameHost CreateHost(params IGameService[] services)
        {
            return new GameHost(services, new RecordingLogger());
        }
    }
}
