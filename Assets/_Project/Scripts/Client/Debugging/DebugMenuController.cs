using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.UI;
using DigBlocks.Client.UI.Views;
using DigBlocks.Core.Async;
using DigBlocks.Core.Diagnostics;
using DigBlocks.Core.Hosting;
using UnityEngine.InputSystem;

namespace DigBlocks.Client.Debugging
{
    //owns the debug overlay and its keybinds. The menu key works at any time; the toggles are bound
    //to the overlay's own lifetime, so they only respond while the debug menu is on screen
    public sealed class DebugMenuController : IMenuViewBinder, IDisposable
    {
        private readonly IMenuCoordinator menus;
        private readonly DebugOptions options;
        private readonly IReadOnlyList<DebugToggle> toggles;
        private readonly IGameLogger logger;
        private readonly AsyncGate gate = new AsyncGate();
        private readonly CancellationTokenSource cancellation;
        private readonly InputAction menuAction;
        private readonly List<InputAction> toggleActions = new List<InputAction>();
        private readonly List<DebugMenuEntry> entries = new List<DebugMenuEntry>();

        private DebugMenuView view;
        private bool disposed;

        public DebugMenuController(
            IMenuCoordinator menus,
            DebugOptions options,
            IReadOnlyList<DebugToggle> toggles,
            IGameLogger logger,
            CancellationToken applicationLifetime)
        {
            this.menus = menus ?? throw new ArgumentNullException(nameof(menus));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.toggles = toggles ?? Array.Empty<DebugToggle>();
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
                .CreateFor(nameof(DebugMenuController));

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime);

            for (int index = 0; index < this.toggles.Count; index++)
            {
                DebugToggle toggle = this.toggles[index];
                entries.Add(new DebugMenuEntry(toggle.Label, toggle.BindDisplay));

                var action = new InputAction($"Debug/{toggle.Id}", InputActionType.Button, toggle.BindingPath);
                action.performed += _ => Cycle(toggle);
                toggleActions.Add(action);
            }

            //standalone actions rather than an action map entry, so debug keys stay live regardless of
            //which gameplay or UI maps the input router has enabled
            menuAction = new InputAction(
                "Debug/Menu",
                InputActionType.Button,
                "<Keyboard>/" + DebugToggleCatalog.MenuKey);
            menuAction.performed += _ => RequestToggleMenu();
            menuAction.Enable();

            if (!this.menus.IsRegistered(MenuIds.Debug))
            {
                this.logger.Log(
                    $"Menu '{MenuIds.Debug}' is not registered, so {DebugToggleCatalog.MenuKey} opens nothing.",
                    GameLogLevel.Warning);
            }
        }

        public IReadOnlyList<DebugMenuEntry> Entries => entries;

        public bool IsMenuOpen => menus.IsOpen(MenuIds.Debug);

        public void Bind(MenuId id, IMenuView menuView)
        {
            if (id != MenuIds.Debug || menuView is not DebugMenuView debugView)
            {
                return;
            }

            view = debugView;
            view.SetToggles(entries);
            SetToggleActionsEnabled(true);
        }

        public void Unbind(MenuId id, IMenuView menuView)
        {
            if (id != MenuIds.Debug)
            {
                return;
            }

            if (menuView is DebugMenuView debugView && view == debugView)
            {
                view = null;
            }

            SetToggleActionsEnabled(false);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            view = null;

            menuAction.Disable();
            menuAction.Dispose();

            for (int index = 0; index < toggleActions.Count; index++)
            {
                toggleActions[index].Disable();
                toggleActions[index].Dispose();
            }

            toggleActions.Clear();

            cancellation.Cancel();
            cancellation.Dispose();
        }

        private void Cycle(DebugToggle toggle)
        {
            if (disposed)
            {
                return;
            }

            int state = options.Cycle(toggle.Id, toggle.StateCount);
            logger.Log($"{toggle.Label}: {toggle.DescribeState(state)}", GameLogLevel.Debug);
        }

        private void RequestToggleMenu()
        {
            if (disposed)
            {
                return;
            }

            ToggleMenuAsync().Forget();
        }

        //serialized so repeated menu-key presses cannot interleave an open with a close
        private async UniTaskVoid ToggleMenuAsync()
        {
            try
            {
                CancellationToken cancellationToken = cancellation.Token;
                using (await gate.EnterAsync(cancellationToken))
                {
                    if (disposed || !menus.IsRegistered(MenuIds.Debug))
                    {
                        return;
                    }

                    if (menus.IsOpen(MenuIds.Debug))
                    {
                        await menus.CloseAsync(MenuIds.Debug, cancellationToken);
                    }
                    else
                    {
                        await menus.PushOverlayAsync(MenuIds.Debug, cancellationToken);
                    }
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
                logger.Log("Toggling the debug menu failed.", GameLogLevel.Error, exception);
            }
        }

        private void SetToggleActionsEnabled(bool enabled)
        {
            for (int index = 0; index < toggleActions.Count; index++)
            {
                InputAction action = toggleActions[index];
                if (enabled && !action.enabled)
                {
                    action.Enable();
                }
                else if (!enabled && action.enabled)
                {
                    action.Disable();
                }
            }
        }
    }
}
