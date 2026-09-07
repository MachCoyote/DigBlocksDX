using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.UI;
using DigBlocks.Core.Hosting;
using NUnit.Framework;

namespace DigBlocks.Client.Tests.UI
{
    public sealed class MenuCoordinatorTests
    {
        private static readonly MenuId TitleId = new MenuId("Title");
        private static readonly MenuId WorldsId = new MenuId("Worlds");
        private static readonly MenuId PauseId = new MenuId("Pause");
        private static readonly MenuId DialogId = new MenuId("Dialog");

        private FakeUIRoot uiRoot;
        private MenuRegistry registry;
        private MenuCoordinator coordinator;
        private List<FakeMenuView> createdViews;

        [SetUp]
        public void SetUp()
        {
            uiRoot = new FakeUIRoot();
            registry = new MenuRegistry();
            createdViews = new List<FakeMenuView>();
            coordinator = new MenuCoordinator(registry, uiRoot, new NullLogger());
        }

        [TearDown]
        public void TearDown()
        {
            coordinator.Dispose();
            uiRoot.Destroy();
        }

        [Test]
        public void ShowScreen_ReplacesThePreviousScreen()
        {
            FakeMenuView title = RegisterScreen(TitleId, MenuRetentionPolicy.Destroy);
            FakeMenuView worlds = RegisterScreen(WorldsId, MenuRetentionPolicy.Destroy);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.ShowScreenAsync(WorldsId, CancellationToken.None));

            Assert.That(title.CloseCount, Is.EqualTo(1));
            Assert.That(title.Disposed, Is.True);
            Assert.That(worlds.OpenCount, Is.EqualTo(1));
            Assert.That(coordinator.IsOpen(TitleId), Is.False);
            Assert.That(coordinator.IsOpen(WorldsId), Is.True);
        }

        [Test]
        public void ShowScreen_WhenAlreadyShowing_DoesNotReopen()
        {
            FakeMenuView title = RegisterScreen(TitleId, MenuRetentionPolicy.Retain);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));

            Assert.That(title.OpenCount, Is.EqualTo(1));
            Assert.That(title.CloseCount, Is.EqualTo(0));
        }

        [Test]
        public void PushOverlay_Twice_OpensOneInstance()
        {
            RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            FakeMenuView pause = RegisterOverlay(PauseId, MenuBackBehavior.Close);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));
            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));

            Assert.That(pause.OpenCount, Is.EqualTo(1));
            Assert.That(createdViews.FindAll(view => view == pause).Count, Is.EqualTo(1));
        }

        [Test]
        public void CoveredScreen_LosesInteractionAndRegainsItWhenUncovered()
        {
            FakeMenuView title = RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            FakeMenuView pause = RegisterOverlay(PauseId, MenuBackBehavior.Close);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Assert.That(title.IsInteractable, Is.True);

            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));
            Assert.That(title.IsInteractable, Is.False);
            Assert.That(pause.IsInteractable, Is.True);

            Run(coordinator.CloseAsync(PauseId, CancellationToken.None));
            Assert.That(title.IsInteractable, Is.True);
        }

        [Test]
        public void Back_ClosesTheTopOverlayAndIsAbsorbedByTheScreen()
        {
            FakeMenuView title = RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            FakeMenuView pause = RegisterOverlay(PauseId, MenuBackBehavior.Close);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));

            Assert.That(Run(coordinator.BackAsync(CancellationToken.None)), Is.True);
            Assert.That(pause.CloseCount, Is.EqualTo(1));

            //back on a screen whose behavior is None is absorbed rather than leaking or closing it
            Assert.That(Run(coordinator.BackAsync(CancellationToken.None)), Is.True);
            Assert.That(title.CloseCount, Is.EqualTo(0));
        }

        [Test]
        public void Back_WithNoMenus_ReportsUnhandled()
        {
            Assert.That(Run(coordinator.BackAsync(CancellationToken.None)), Is.False);
        }

        [Test]
        public void Back_WithViewHandledBehavior_InvokesTheView()
        {
            RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            FakeMenuView pause = RegisterOverlay(PauseId, MenuBackBehavior.ViewHandled);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));

            Assert.That(Run(coordinator.BackAsync(CancellationToken.None)), Is.True);
            Assert.That(pause.BackCount, Is.EqualTo(1));
            Assert.That(pause.CloseCount, Is.EqualTo(0));
        }

        [Test]
        public void ShowScreen_ClearsOverlaysAndModals()
        {
            RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            RegisterScreen(WorldsId, MenuRetentionPolicy.Retain);
            FakeMenuView pause = RegisterOverlay(PauseId, MenuBackBehavior.Close);
            FakeMenuView dialog = RegisterModal(DialogId);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.PushOverlayAsync(PauseId, CancellationToken.None));
            Run(coordinator.ShowModalAsync(DialogId, CancellationToken.None));
            Run(coordinator.ShowScreenAsync(WorldsId, CancellationToken.None));

            Assert.That(pause.CloseCount, Is.EqualTo(1));
            Assert.That(dialog.CloseCount, Is.EqualTo(1));
            Assert.That(coordinator.IsOpen(PauseId), Is.False);
            Assert.That(coordinator.IsOpen(DialogId), Is.False);
        }

        [Test]
        public void RetainedMenu_ReusesTheSameInstance()
        {
            FakeMenuView title = RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            RegisterScreen(WorldsId, MenuRetentionPolicy.Retain);

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Run(coordinator.ShowScreenAsync(WorldsId, CancellationToken.None));
            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));

            Assert.That(title.Disposed, Is.False);
            Assert.That(title.OpenCount, Is.EqualTo(2));
            Assert.That(createdViews.FindAll(view => view == title).Count, Is.EqualTo(1));
        }

        [Test]
        public void InputRequirements_ReflectTheOpenMenus()
        {
            RegisterScreen(TitleId, MenuRetentionPolicy.Retain);
            RegisterOverlay(PauseId, MenuBackBehavior.Close);

            var reported = new List<MenuInputRequirements>();
            coordinator.InputRequirementsChanged += reported.Add;

            Run(coordinator.ShowScreenAsync(TitleId, CancellationToken.None));
            Assert.That(coordinator.InputRequirements.AnyMenuOpen, Is.True);
            Assert.That(coordinator.InputRequirements.BlocksGameplayInput, Is.True);

            Run(coordinator.ResetAsync(CancellationToken.None));
            Assert.That(coordinator.InputRequirements.AnyMenuOpen, Is.False);
            Assert.That(reported, Is.Not.Empty);
        }

        [Test]
        public void UnregisteredMenu_IsReportedAndDoesNotChangeNavigation()
        {
            var logger = new RecordingLogger();
            var isolated = new MenuCoordinator(registry, uiRoot, logger);

            Run(isolated.ShowScreenAsync(new MenuId("Missing"), CancellationToken.None));

            Assert.That(logger.Errors, Is.Not.Empty);
            Assert.That(isolated.IsOpen(new MenuId("Missing")), Is.False);
            isolated.Dispose();
        }

        private FakeMenuView RegisterScreen(MenuId id, MenuRetentionPolicy retention)
        {
            var metadata = new MenuMetadata(
                MenuLayer.Screen,
                blocksInteractionUnderneath: true,
                blocksGameplayInput: true,
                showsCursor: true,
                MenuBackBehavior.None,
                retention);
            return Register(id, metadata);
        }

        private FakeMenuView RegisterOverlay(MenuId id, MenuBackBehavior backBehavior)
        {
            var metadata = new MenuMetadata(
                MenuLayer.Overlay,
                blocksInteractionUnderneath: true,
                blocksGameplayInput: true,
                showsCursor: true,
                backBehavior,
                MenuRetentionPolicy.Retain);
            return Register(id, metadata);
        }

        private FakeMenuView RegisterModal(MenuId id)
        {
            var metadata = new MenuMetadata(
                MenuLayer.Modal,
                blocksInteractionUnderneath: true,
                blocksGameplayInput: true,
                showsCursor: true,
                MenuBackBehavior.Close,
                MenuRetentionPolicy.Destroy);
            return Register(id, metadata);
        }

        private FakeMenuView Register(MenuId id, MenuMetadata metadata)
        {
            var view = new FakeMenuView(id.Value);
            var factory = new DelegateMenuFactory(_ =>
            {
                createdViews.Add(view);
                return view;
            });

            registry.Register(new MenuRegistration(id, factory, metadata));
            return view;
        }

        private static void Run(UniTask task)
        {
            task.GetAwaiter().GetResult();
        }

        private static T Run<T>(UniTask<T> task)
        {
            return task.GetAwaiter().GetResult();
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

        private sealed class RecordingLogger : IGameLogger
        {
            public List<string> Errors { get; } = new List<string>();

            public IGameLogger CreateFor(string sourceName)
            {
                return this;
            }

            public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null)
            {
                if (level == GameLogLevel.Error)
                {
                    Errors.Add(message);
                }
            }
        }
    }
}
