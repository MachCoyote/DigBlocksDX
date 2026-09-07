using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Async;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Core.Session;

namespace DigBlocks.Bootstrap.Session
{
    //owns one game session lifetime at a time: host creation, explicit readiness, teardown, and disposal
    public sealed class GameSessionController : IGameSessionController
    {
        private readonly IGameLogger logger;
        private readonly Func<SessionStartRequest, IReadOnlyList<IGameService>> serviceFactory;
        private readonly AsyncGate gate = new AsyncGate();

        private GameHost host;
        private IReadOnlyList<IGameService> services;
        private GameSessionStatus status = GameSessionStatus.Idle;

        public GameSessionController(
            IGameLogger logger,
            Func<SessionStartRequest, IReadOnlyList<IGameService>> serviceFactory = null)
        {
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
                .CreateFor(nameof(GameSessionController));
            this.serviceFactory = serviceFactory
                ?? (request => GameServiceComposer.Compose(request.LaunchOptions, logger));
        }

        public GameSessionStatus Status => status;

        public event Action<GameSessionStatus> StatusChanged;

        public GameHostState HostState => host?.State ?? GameHostState.Created;

        //lets the composition root surface session-scoped services without owning their lifetime
        public T FindService<T>() where T : class
        {
            IReadOnlyList<IGameService> current = services;
            if (current == null)
            {
                return null;
            }

            for (int index = 0; index < current.Count; index++)
            {
                if (current[index] is T typed)
                {
                    return typed;
                }
            }

            return null;
        }

        public async UniTask<GameSessionStatus> StartSessionAsync(
            SessionStartRequest request,
            CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (host != null)
                {
                    //repeated play requests must never produce a second session
                    logger.Log("A session is already active; ignoring the start request.", GameLogLevel.Warning);
                    return status;
                }

                SetStatus(GameSessionPhase.Starting, "Creating session...");

                try
                {
                    services = serviceFactory(request);
                    host = new GameHost(services, logger);

                    SetStatus(GameSessionPhase.Starting, "Starting session services...");
                    await host.StartAsync(cancellationToken);

                    await WaitForReadinessAsync(cancellationToken);

                    SetStatus(GameSessionPhase.Ready, $"World '{request.WorldId}' is ready.");
                }
                catch (OperationCanceledException)
                {
                    await TearDownAsync(CancellationToken.None);
                    SetStatus(GameSessionPhase.Idle, "Session startup was cancelled.");
                    throw;
                }
                catch (Exception exception)
                {
                    logger.Log("Session startup failed.", GameLogLevel.Error, exception);
                    await TearDownAsync(CancellationToken.None);
                    SetStatus(GameSessionPhase.Failed, exception.Message, exception);
                }

                return status;
            }
        }

        public async UniTask StopSessionAsync(CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (host == null)
                {
                    if (status.Phase != GameSessionPhase.Idle)
                    {
                        SetStatus(GameSessionPhase.Idle, "No session.");
                    }

                    return;
                }

                SetStatus(GameSessionPhase.Stopping, "Stopping session...");
                await TearDownAsync(cancellationToken);
                SetStatus(GameSessionPhase.Idle, "No session.");
            }
        }

        public async UniTask SetSimulationPausedAsync(bool paused, CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (services == null || status.Phase != GameSessionPhase.Ready)
                {
                    return;
                }

                int sinkCount = 0;
                for (int index = 0; index < services.Count; index++)
                {
                    if (services[index] is ISimulationPauseSink sink)
                    {
                        sinkCount++;
                        await sink.SetSimulationPausedAsync(paused, cancellationToken);
                    }
                }

                if (sinkCount == 0)
                {
                    //no runtime can freeze itself yet; the request is recorded rather than silently dropped
                    logger.Log(
                        $"Simulation pause was requested ({paused}) but no session service can apply it.",
                        GameLogLevel.Debug);
                }
            }
        }

        //a session is not playable until every readiness source reports it explicitly
        private async UniTask WaitForReadinessAsync(CancellationToken cancellationToken)
        {
            if (services == null)
            {
                return;
            }

            for (int index = 0; index < services.Count; index++)
            {
                if (services[index] is not ISessionReadinessSource source)
                {
                    continue;
                }

                SetStatus(GameSessionPhase.Starting, source.ReadinessDescription);
                await source.WaitUntilReadyAsync(cancellationToken);
            }
        }

        private async UniTask TearDownAsync(CancellationToken cancellationToken)
        {
            if (host == null)
            {
                services = null;
                return;
            }

            try
            {
                await host.StopAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.Log("Session shutdown reported errors.", GameLogLevel.Error, exception);
            }

            host = null;
            services = null;
        }

        private void SetStatus(GameSessionPhase phase, string description, Exception failure = null)
        {
            status = new GameSessionStatus(phase, description, failure);
            StatusChanged?.Invoke(status);
        }
    }
}
