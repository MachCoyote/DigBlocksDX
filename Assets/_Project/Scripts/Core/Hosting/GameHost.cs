using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DigBlocks.Core.Hosting
{
    public sealed class GameHost
    {
        private readonly IGameLogger logger;
        private readonly IGameService[] services;
        private readonly List<IGameService> startedServices = new List<IGameService>();
        private int lifecycleTransition;
        private int state = (int)GameHostState.Created;

        public GameHost(IEnumerable<IGameService> services, IGameLogger logger)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var copiedServices = new List<IGameService>();
            foreach (IGameService service in services)
            {
                if (service == null)
                {
                    throw new ArgumentException("The service collection cannot contain null.", nameof(services));
                }

                copiedServices.Add(service);
            }

            this.services = copiedServices.ToArray();
        }

        public GameHostState State => (GameHostState)Volatile.Read(ref state);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            EnterLifecycleTransition();
            try
            {
                if (State != GameHostState.Created)
                {
                    throw new InvalidOperationException($"A game host cannot start from state {State}.");
                }

                SetState(GameHostState.Starting);
                try
                {
                    for (int index = 0; index < services.Length; index++)
                    {
                        IGameService service = services[index];
                        logger.Log(GameLogLevel.Information, $"Starting game service '{service.Name}'.");
                        await service.StartAsync(cancellationToken);
                        startedServices.Add(service);
                    }

                    SetState(GameHostState.Running);
                }
                catch (Exception startupException)
                {
                    List<Exception> rollbackErrors = await RollBackStartupAsync();
                    SetState(GameHostState.Faulted);

                    if (rollbackErrors.Count > 0)
                    {
                        throw new GameHostStartException(
                            "Game host startup failed and one or more services could not be rolled back.",
                            startupException,
                            rollbackErrors);
                    }

                    throw;
                }
            }
            finally
            {
                ExitLifecycleTransition();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            EnterLifecycleTransition();
            try
            {
                GameHostState currentState = State;
                if (currentState == GameHostState.Stopped)
                {
                    return;
                }

                if (currentState == GameHostState.Created)
                {
                    SetState(GameHostState.Stopped);
                    return;
                }

                if (currentState != GameHostState.Running && currentState != GameHostState.Faulted)
                {
                    throw new InvalidOperationException($"A game host cannot stop from state {currentState}.");
                }

                SetState(GameHostState.Stopping);
                var errors = new List<Exception>();
                for (int index = startedServices.Count - 1; index >= 0; index--)
                {
                    IGameService service = startedServices[index];
                    try
                    {
                        logger.Log(GameLogLevel.Information, $"Stopping game service '{service.Name}'.");
                        await service.StopAsync(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }

                startedServices.Clear();
                SetState(GameHostState.Stopped);

                if (errors.Count > 0)
                {
                    throw new AggregateException("One or more game services failed to stop.", errors);
                }
            }
            finally
            {
                ExitLifecycleTransition();
            }
        }

        private async Task<List<Exception>> RollBackStartupAsync()
        {
            var rollbackErrors = new List<Exception>();
            var retainedServices = new List<IGameService>();

            for (int index = startedServices.Count - 1; index >= 0; index--)
            {
                IGameService service = startedServices[index];
                try
                {
                    logger.Log(GameLogLevel.Information, $"Rolling back game service '{service.Name}'.");
                    await service.StopAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(exception);
                    retainedServices.Add(service);
                }
            }

            retainedServices.Reverse();
            startedServices.Clear();
            startedServices.AddRange(retainedServices);
            return rollbackErrors;
        }

        private void EnterLifecycleTransition()
        {
            if (Interlocked.CompareExchange(ref lifecycleTransition, 1, 0) != 0)
            {
                throw new InvalidOperationException("Another game host lifecycle transition is already active.");
            }
        }

        private void ExitLifecycleTransition()
        {
            Volatile.Write(ref lifecycleTransition, 0);
        }

        private void SetState(GameHostState value)
        {
            Volatile.Write(ref state, (int)value);
        }
    }
}
