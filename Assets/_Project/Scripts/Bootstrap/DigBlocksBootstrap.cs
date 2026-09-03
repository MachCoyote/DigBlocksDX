using Cysharp.Threading.Tasks;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using System;
using System.Collections.Generic;
using System.Threading;
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
        private UniTask shutdownTask;
        private UniTask startupTask;
        private bool shutdownComplete;
        private bool shutdownStarted;
        private bool startupStarted;
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
            logger = new UnityGameLogger().CreateFor(nameof(DigBlocksBootstrap));
            Application.wantsToQuit += WantsToQuit;
        }

        private void Start()
        {
            if (instance == this)
            {
                startupStarted = true;
                startupTask = StartHostAsync().Preserve();
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

        private async UniTaskVoid CompleteQuitAfterShutdownAsync()
        {
            await BeginShutdownAsync();
            await UniTask.Yield();
            Application.Quit();
        }

        private UniTask BeginShutdownAsync()
        {
            if (!shutdownStarted)
            {
                shutdownStarted = true;
                shutdownTask = ShutdownHostAsync().Preserve();
            }

            return shutdownTask;
        }

        private async UniTask ShutdownHostAsync()
        {
            lifetimeCancellation.Cancel();

            try
            {
                if (startupStarted)
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
                logger.Log("Shutdown failed.", GameLogLevel.Error, exception);
            }

            lifetimeCancellation.Dispose();
            shutdownComplete = true;
        }

        private async UniTask StartHostAsync()
        {
            try
            {
                LaunchOptions = LaunchModeResolver.Resolve(
                    defaultLaunchMode,
                    Environment.GetCommandLineArgs(),
                    IsServerBuild());


                IReadOnlyList<IGameService> services = GameServiceComposer.Compose(LaunchOptions, logger);
                host = new GameHost(services, logger);


                await host.StartAsync(lifetimeCancellation.Token);
                logger.Log(
                    $"Started in {LaunchOptions.Mode} mode.",
                    GameLogLevel.Information);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                state = host?.State ?? GameHostState.Faulted;
                logger.Log("Startup was cancelled during shutdown.", GameLogLevel.Information);
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                state = host?.State ?? GameHostState.Faulted;
                logger.Log("Startup failed.", GameLogLevel.Error, exception);
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
