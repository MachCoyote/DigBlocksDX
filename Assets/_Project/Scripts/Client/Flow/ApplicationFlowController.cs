using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.UI;
using DigBlocks.Client.UI.Views;
using DigBlocks.Core.Async;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Core.Session;

namespace DigBlocks.Client.Flow
{
    //owns application-level state; views emit intent and this decides what it means for the game
    public sealed class ApplicationFlowController : IMenuViewBinder, IDisposable
    {
        private const float FailureDisplaySeconds = 2.5f;

        private readonly IMenuCoordinator menus;
        private readonly IGameSessionController sessions;
        private readonly IApplicationLifetime lifetime;
        private readonly IGameLogger logger;
        private readonly LaunchOptions launchOptions;
        private readonly string developmentWorldId;
        private readonly AsyncGate gate = new AsyncGate();
        private readonly CancellationTokenSource cancellation;

        private LoadingView loadingView;
        private bool simulationPaused;
        private bool disposed;

        public ApplicationFlowController(
            IMenuCoordinator menus,
            IGameSessionController sessions,
            IApplicationLifetime lifetime,
            LaunchOptions launchOptions,
            string developmentWorldId,
            IGameLogger logger,
            CancellationToken applicationLifetime)
        {
            this.menus = menus ?? throw new ArgumentNullException(nameof(menus));
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            this.launchOptions = launchOptions;
            this.developmentWorldId = developmentWorldId ?? string.Empty;
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
                .CreateFor(nameof(ApplicationFlowController));

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);
            this.sessions.StatusChanged += OnSessionStatusChanged;
        }

        public ApplicationState State { get; private set; } = ApplicationState.Launching;

        public event Action<ApplicationState> StateChanged;

        public async UniTask StartAsync(CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (State != ApplicationState.Launching)
                {
                    return;
                }

                if (menus.IsRegistered(MenuIds.Intro))
                {
                    SetState(ApplicationState.Intro);
                    await menus.ShowScreenAsync(MenuIds.Intro, cancellationToken);
                    return;
                }

                await EnterTitleAsync(cancellationToken);
            }
        }

        public void RequestPlay()
        {
            Enqueue(HandlePlayAsync, nameof(RequestPlay));
        }

        public void RequestQuit()
        {
            Enqueue(HandleQuitAsync, nameof(RequestQuit));
        }

        public void RequestPause()
        {
            Enqueue(HandlePauseAsync, nameof(RequestPause));
        }

        public void RequestResume()
        {
            Enqueue(HandleResumeAsync, nameof(RequestResume));
        }

        public void RequestReturnToTitle()
        {
            Enqueue(HandleReturnToTitleAsync, nameof(RequestReturnToTitle));
        }

        public void RequestBack()
        {
            Enqueue(HandleBackAsync, nameof(RequestBack));
        }

        public void RequestSkipIntro()
        {
            Enqueue(HandleIntroFinishedAsync, nameof(RequestSkipIntro));
        }

        public void RequestIntroComplete()
        {
            Enqueue(HandleIntroFinishedAsync, nameof(RequestIntroComplete));
        }

        public void Bind(MenuId id, IMenuView view)
        {
            switch (view)
            {
                case TitleMenuView title:
                    title.PlayRequested += RequestPlay;
                    title.QuitRequested += RequestQuit;
                    break;

                case PauseMenuView pause:
                    pause.ResumeRequested += RequestResume;
                    pause.ReturnToTitleRequested += RequestReturnToTitle;
                    break;

                case IntroView intro:
                    intro.SkipRequested += RequestSkipIntro;
                    intro.PresentationCompleted += RequestIntroComplete;
                    break;

                case LoadingView loading:
                    loadingView = loading;
                    break;
            }
        }

        public void Unbind(MenuId id, IMenuView view)
        {
            switch (view)
            {
                case TitleMenuView title:
                    title.PlayRequested -= RequestPlay;
                    title.QuitRequested -= RequestQuit;
                    break;

                case PauseMenuView pause:
                    pause.ResumeRequested -= RequestResume;
                    pause.ReturnToTitleRequested -= RequestReturnToTitle;
                    break;

                case IntroView intro:
                    intro.SkipRequested -= RequestSkipIntro;
                    intro.PresentationCompleted -= RequestIntroComplete;
                    break;

                case LoadingView loading:
                    if (loadingView == loading)
                    {
                        loadingView = null;
                    }

                    break;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sessions.StatusChanged -= OnSessionStatusChanged;
            StateChanged = null;

            cancellation.Cancel();
            cancellation.Dispose();
        }

        private async UniTask HandlePlayAsync(CancellationToken cancellationToken)
        {
            if (State != ApplicationState.Title)
            {
                //repeated play requests cannot start a second session
                logger.Log($"Play was requested while {State}; ignoring it.", GameLogLevel.Debug);
                return;
            }

            SetState(ApplicationState.Loading);
            await menus.ShowScreenAsync(MenuIds.Loading, cancellationToken);
            loadingView?.SetStatus("Preparing session...");

            var request = new SessionStartRequest(launchOptions, developmentWorldId);
            GameSessionStatus status = await sessions.StartSessionAsync(request, cancellationToken);

            if (status.IsReady)
            {
                SetState(ApplicationState.Playing);
                await menus.ClearScreenAsync(cancellationToken);
                return;
            }

            logger.Log($"Session startup failed: {status}", GameLogLevel.Error, status.Failure);
            loadingView?.SetError($"Could not enter the world.\n{status.Description}");

            await DelayAsync(FailureDisplaySeconds, cancellationToken);
            await EnterTitleAsync(cancellationToken);
        }

        private async UniTask HandlePauseAsync(CancellationToken cancellationToken)
        {
            await OpenPauseAsync(cancellationToken);
        }

        private async UniTask HandleResumeAsync(CancellationToken cancellationToken)
        {
            if (State != ApplicationState.Playing || !menus.IsOpen(MenuIds.Pause))
            {
                return;
            }

            await ApplyPausePolicyAsync(false, cancellationToken);
            await menus.CloseAsync(MenuIds.Pause, cancellationToken);
        }

        private async UniTask HandleReturnToTitleAsync(CancellationToken cancellationToken)
        {
            if (State != ApplicationState.Playing)
            {
                return;
            }

            SetState(ApplicationState.Leaving);
            await ApplyPausePolicyAsync(false, cancellationToken);

            //showing the loading screen also clears the pause overlay and any modal above it
            await menus.ShowScreenAsync(MenuIds.Loading, cancellationToken);
            loadingView?.SetStatus("Leaving world...");

            await sessions.StopSessionAsync(cancellationToken);
            await EnterTitleAsync(cancellationToken);
        }

        private async UniTask HandleBackAsync(CancellationToken cancellationToken)
        {
            if (await menus.BackAsync(cancellationToken))
            {
                return;
            }

            await OpenPauseAsync(cancellationToken);
        }

        private async UniTask HandleIntroFinishedAsync(CancellationToken cancellationToken)
        {
            if (State != ApplicationState.Intro)
            {
                return;
            }

            await EnterTitleAsync(cancellationToken);
        }

        private UniTask HandleQuitAsync(CancellationToken cancellationToken)
        {
            SetState(ApplicationState.ShuttingDown);
            logger.Log("Quit requested.");
            lifetime.RequestQuit();
            return UniTask.CompletedTask;
        }

        private async UniTask OpenPauseAsync(CancellationToken cancellationToken)
        {
            if (State != ApplicationState.Playing || menus.IsOpen(MenuIds.Pause))
            {
                return;
            }

            await menus.PushOverlayAsync(MenuIds.Pause, cancellationToken);
            await ApplyPausePolicyAsync(true, cancellationToken);
        }

        private async UniTask EnterTitleAsync(CancellationToken cancellationToken)
        {
            SetState(ApplicationState.Title);
            await menus.ShowScreenAsync(MenuIds.Title, cancellationToken);
        }

        //application flow owns pause policy: a local session freezes, a remote one keeps running
        private async UniTask ApplyPausePolicyAsync(bool paused, CancellationToken cancellationToken)
        {
            if (launchOptions.Mode != LaunchMode.SinglePlayer || simulationPaused == paused)
            {
                return;
            }

            simulationPaused = paused;
            await sessions.SetSimulationPausedAsync(paused, cancellationToken);
        }

        private void OnSessionStatusChanged(GameSessionStatus status)
        {
            if (State == ApplicationState.Loading || State == ApplicationState.Leaving)
            {
                loadingView?.SetStatus(status.Description);
            }
        }

        private void SetState(ApplicationState next)
        {
            if (State == next)
            {
                return;
            }

            logger.Log($"{State} -> {next}");
            State = next;
            StateChanged?.Invoke(next);
        }

        private static UniTask DelayAsync(float seconds, CancellationToken cancellationToken)
        {
            //unscaled so UI timing survives a frozen simulation clock
            return UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                DelayType.UnscaledDeltaTime,
                PlayerLoopTiming.Update,
                cancellationToken);
        }

        private void Enqueue(Func<CancellationToken, UniTask> operation, string requestName)
        {
            if (disposed)
            {
                return;
            }

            RunAsync(operation, requestName).Forget();
        }

        //navigation and session transitions are serialized so duplicate requests cannot interleave
        private async UniTaskVoid RunAsync(Func<CancellationToken, UniTask> operation, string requestName)
        {
            try
            {
                CancellationToken cancellationToken = cancellation.Token;
                using (await gate.EnterAsync(cancellationToken))
                {
                    if (disposed)
                    {
                        return;
                    }

                    await operation(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                logger.Log($"{requestName} failed.", GameLogLevel.Error, exception);
            }
        }
    }
}
