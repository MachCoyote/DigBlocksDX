using Cysharp.Threading.Tasks;
using DigBlocks.Bootstrap.Session;
using DigBlocks.Client.Flow;
using DigBlocks.Client.UI;
using DigBlocks.Core.Diagnostics;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Core.Session;
using DigBlocks.Networking;
using DigBlocks.Networking.NetCode;
using System;
using System.Threading;
using UnityEngine;
using DigBlocks.Client.Rendering;
using DigBlocks.Client.Runtime;
using System.Collections.Generic;
using DigBlocks.Voxels.Generation;

namespace DigBlocks.Bootstrap
{
    //application composition root: it wires application-lifetime services and owns process shutdown
    [DefaultExecutionOrder(-10000)]
    public sealed class DigBlocksBootstrap : MonoBehaviour
    {
        private static DigBlocksBootstrap instance;

        [SerializeField]
        private LaunchMode defaultLaunchMode = LaunchMode.SinglePlayer;

        [SerializeField]
        [Tooltip("Authored menu registrations. Placeholder menus are used for anything missing.")]
        private MenuCatalog menuCatalog;

        [SerializeField]
        [Tooltip("Optional authored UI root prefab. When empty the UI root is built in code.")]
        private UIRoot uiRootPrefab;

        [SerializeField]
        [Tooltip("World the play action enters until world selection exists.")]
        private string developmentWorldId = "dev";

        [SerializeField]
        [Tooltip("World type the play action generates until world creation exists.")]
        private string worldType = "digblocks:classic";

        [SerializeField]
        [Tooltip("World seed. Empty or 0 picks one at random; text that is not a number is hashed.")]
        private string worldSeed = string.Empty;

        [SerializeField]
        [Tooltip("Generate terrain for the development world. Off leaves every chunk empty.")]
        private bool generateTerrain = true;

        private CancellationTokenSource lifetimeCancellation;
        private ClientPresentation presentation;
        private GameSessionController sessions;
        private IGameLogger logger;
        private UniTaskCompletionSource shutdownCompletion;
        private UniTask startupTask;
        private bool shutdownComplete;
        private bool shutdownStarted;
        private bool startupStarted;
        private bool quitContinuationStarted;

        public LaunchOptions LaunchOptions { get; private set; }

        public Exception LastFailure { get; private set; }

        //the session's host state; a client has no host until a session is started
        public GameHostState HostState => sessions?.HostState ?? GameHostState.Created;

        public GameSessionStatus SessionStatus => sessions?.Status ?? GameSessionStatus.Idle;

        public ApplicationState? CurrentApplicationState => presentation?.Flow.State;

        public INetworkSession NetworkSession => sessions?.FindService<INetworkSession>();

        public ChunkCompanionService ChunkCompanion => sessions?.FindService<ChunkCompanionService>();
        public TerrainRenderService Terrain => sessions?.FindService<TerrainRenderService>();

        //application-level entry points for debug tooling and tests; UI reaches flow through view intent
        public void RequestPlay()
        {
            presentation?.Flow.RequestPlay();
        }

        public void RequestReturnToTitle()
        {
            presentation?.Flow.RequestReturnToTitle();
        }

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
                startupTask = StartApplicationAsync().Preserve();
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
                shutdownCompletion = new UniTaskCompletionSource();
                CompleteShutdownAsync().Forget();
            }

            return shutdownCompletion.Task;
        }

        private async UniTask CompleteShutdownAsync()
        {
            //quit and destruction may both await the same in-flight cleanup.
            try { await ShutdownApplicationAsync(); shutdownCompletion.TrySetResult(); }
            catch (Exception exception) { shutdownCompletion.TrySetException(exception); }
        }

        private async UniTask StartApplicationAsync()
        {
            try
            {
                LaunchOptions = LaunchModeResolver.Resolve(
                    defaultLaunchMode,
                    Environment.GetCommandLineArgs(),
                    IsServerBuild());

                //network settings live for the whole application; services are created per session
                NetworkLaunchSettings network = NetworkLaunchSettings.Parse(
                    Environment.GetCommandLineArgs(),
                    LaunchOptions.Mode,
                    Application.persistentDataPath);

                sessions = new GameSessionController(
                    logger,
                    request => ComposeSession(request.LaunchOptions, network));

                if (LaunchOptions.Mode == LaunchMode.DedicatedServer)
                {
                    await StartDedicatedServerAsync();
                    return;
                }

                presentation = ClientPresentationComposer.Compose(
                    LaunchOptions,
                    menuCatalog,
                    uiRootPrefab,
                    developmentWorldId,
                    sessions,
                    new UnityApplicationLifetime(),
                    logger,
                    lifetimeCancellation.Token);

                await presentation.Flow.StartAsync(lifetimeCancellation.Token);

                logger.Log($"Client application started in {LaunchOptions.Mode} mode.");
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                logger.Log("Startup was cancelled during shutdown.");
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                logger.Log("Startup failed.", GameLogLevel.Error, exception);
            }
        }

        private IReadOnlyList<IGameService> ComposeSession(LaunchOptions launch, NetworkLaunchSettings network)
        {
            var content = BlockContentProvider.Load();
            var streamingSettings = Resources.Load<ChunkStreamingSettings>("ChunkStreamingSettings");
            var streamingOptions = streamingSettings != null ? streamingSettings.CreateOptions() : new ChunkStreamingOptions();
            GeneratedTerrainChunkSource terrain = null;
            if (generateTerrain)
            {
                var definition = BuiltInGenerators.ResolveOrDefault(worldType);
                var seed = GenSeed.Parse(worldSeed);
                terrain = new GeneratedTerrainChunkSource(definition, seed,
                    GeneratorSettings.Defaults(definition.Schema), content.Registry, streamingOptions.WorldId);
                logger.Log($"World '{definition.DisplayName}' generating from seed {seed}.");
            }
            var services = new List<IGameService>(GameServiceComposer.Compose(launch, logger, network, terrain));
            //the generator owns native memory, so it has to be stopped with the session. It goes ahead
            //of the companion that pulls from it, and services stop in reverse.
            if (terrain != null) services.Insert(0, terrain);
            ClientRuntime client = null;
            ChunkCompanionService companion = null;
            foreach (var service in services)
            {
                if (service is ClientRuntime c) client = c;
                if (service is ChunkCompanionService chunkCompanion) companion = chunkCompanion;
            }
            //single player only, and off until the debug menu turns it on; the lambda reads the switch
            //each time a chunk is offered, so flipping it takes effect on the next chunk.
            if (companion != null)
                companion.DirectChunkDelivery = () =>
                    presentation?.DebugOptions?.IsEnabled(DebugToggleIds.DirectChunkDelivery) ?? false;
            if (client != null && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                services.Add(new TerrainRenderService(() => client.World, content, Resources.Load<TerrainRenderSettings>("TerrainRenderSettings"),
                    () => presentation?.GameplayInputAvailable ?? false, presentation?.DebugOptions,
                    companion == null ? null : companion.RequestClientInterest, companion?.WorldId ?? 1));
            return services;
        }

        //dedicated servers bypass client UI and application-menu behaviour entirely
        private async UniTask StartDedicatedServerAsync()
        {
            var request = new SessionStartRequest(LaunchOptions, developmentWorldId);
            GameSessionStatus status = await sessions.StartSessionAsync(request, lifetimeCancellation.Token);

            if (status.IsReady)
            {
                logger.Log($"Dedicated server session ready for world '{developmentWorldId}'.");
                return;
            }

            LastFailure = status.Failure ?? new InvalidOperationException(status.Description);
            logger.Log("The dedicated server session did not become ready.", GameLogLevel.Error, status.Failure);
        }

        private async UniTask ShutdownApplicationAsync()
        {
            lifetimeCancellation.Cancel();

            try
            {
                if (startupStarted)
                {
                    await startupTask;
                }

                presentation?.Dispose();
                presentation = null;

                if (sessions != null)
                {
                    await sessions.StopSessionAsync(CancellationToken.None);
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
