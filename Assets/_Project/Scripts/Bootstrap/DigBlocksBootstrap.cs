using System;
using System.Threading;
using System.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    [DefaultExecutionOrder(-10000)]
    public sealed class DigBlocksBootstrap : MonoBehaviour
    {
        private static DigBlocksBootstrap instance;

        [SerializeField]
        private LaunchMode defaultLaunchMode = LaunchMode.SinglePlayer;

        private CancellationTokenSource lifetimeCancellation;
        private GameHost host;
        private IGameLogger logger;
        private GameHostState state = GameHostState.Created;
        private Task shutdownTask;
        private Task startupTask;
        private bool shutdownComplete;
        private bool quitContinuationStarted;

        public GameHostState HostState => host?.State ?? state;

        public LaunchOptions LaunchOptions { get; private set; }

        public Exception LastFailure { get; private set; }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
            lifetimeCancellation = new CancellationTokenSource();
            logger = new UnityGameLogger();
            Application.wantsToQuit += WantsToQuit;
        }

        private void Start()
        {
            if (instance == this)
            {
                startupTask = StartHostAsync();
            }
        }

        private void OnApplicationQuit()
        {
            if (instance == this && !shutdownComplete)
            {
                lifetimeCancellation.Cancel();
            }
        }

        private async void OnDestroy()
        {
            Application.wantsToQuit -= WantsToQuit;
            if (instance != this)
            {
                return;
            }

            instance = null;
            await BeginShutdownAsync();
        }

        private bool WantsToQuit()
        {
            if (instance != this || shutdownComplete)
            {
                return true;
            }

            if (!quitContinuationStarted)
            {
                quitContinuationStarted = true;
                _ = CompleteQuitAfterShutdownAsync();
            }

            return false;
        }

        private async Task CompleteQuitAfterShutdownAsync()
        {
            await BeginShutdownAsync();
            await Task.Yield();
            Application.Quit();
        }

        private Task BeginShutdownAsync()
        {
            shutdownTask ??= ShutdownHostAsync();
            return shutdownTask;
        }

        private async Task ShutdownHostAsync()
        {
            lifetimeCancellation.Cancel();

            try
            {
                if (startupTask != null)
                {
                    await startupTask;
                }

                if (host != null)
                {
                    await host.StopAsync(CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                LastFailure ??= exception;
                logger.Log(GameLogLevel.Error, "DigBlocks shutdown failed.", exception);
            }

            lifetimeCancellation.Dispose();
            shutdownComplete = true;
        }

        private async Task StartHostAsync()
        {
            try
            {
                LaunchOptions = LaunchModeResolver.Resolve(
                    defaultLaunchMode,
                    Environment.GetCommandLineArgs(),
                    IsServerBuild());

                host = new GameHost(Array.Empty<IGameService>(), logger);
                await host.StartAsync(lifetimeCancellation.Token);
                logger.Log(
                    GameLogLevel.Information,
                    $"DigBlocks started in {LaunchOptions.Mode} mode.");
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                state = host?.State ?? GameHostState.Faulted;
                logger.Log(GameLogLevel.Information, "DigBlocks startup was cancelled during shutdown.");
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                state = host?.State ?? GameHostState.Faulted;
                logger.Log(GameLogLevel.Error, "DigBlocks startup failed.", exception);
            }
        }

        private static bool IsServerBuild()
        {
#if UNITY_SERVER
            return true;
#else
            return false;
#endif
        }
    }
}
