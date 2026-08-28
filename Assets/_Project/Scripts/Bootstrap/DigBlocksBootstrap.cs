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
        private Task startupTask;

        public GameHostState HostState { get; private set; } = GameHostState.Created;

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
            if (instance == this)
            {
                lifetimeCancellation.Cancel();
            }
        }

        private async void OnDestroy()
        {
            if (instance != this)
            {
                return;
            }

            instance = null;
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
                    HostState = host.State;
                }
            }
            catch (Exception exception)
            {
                if (LastFailure == null)
                {
                    LastFailure = exception;
                }

                logger.Log(GameLogLevel.Error, "DigBlocks shutdown failed.", exception);
            }
            finally
            {
                lifetimeCancellation.Dispose();
            }
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
                HostState = host.State;
                logger.Log(
                    GameLogLevel.Information,
                    $"DigBlocks started in {LaunchOptions.Mode} mode.");
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                HostState = host?.State ?? GameHostState.Faulted;
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
