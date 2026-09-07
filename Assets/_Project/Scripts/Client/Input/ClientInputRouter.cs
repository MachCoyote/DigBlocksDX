using System;
using DigBlocks.Client.Flow;
using DigBlocks.Client.UI;
using DigBlocks.Core.Hosting;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DigBlocks.Client.Input
{
    //applies concrete input and cursor state from the combination of application state and active UI
    public sealed class ClientInputRouter : IDisposable
    {
        private const string GameplayActionMapName = "Player";
        private const string UIActionMapName = "UI";
        private const string CancelActionPath = "UI/Cancel";

        private readonly IGameLogger logger;
        private readonly InputActionMap gameplayMap;
        private readonly InputActionMap uiMap;
        private readonly InputAction cancelAction;

        private ApplicationState applicationState = ApplicationState.Launching;
        private MenuInputRequirements menuRequirements = MenuInputRequirements.None;
        private bool disposed;

        public ClientInputRouter(IGameLogger logger)
        {
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
                .CreateFor(nameof(ClientInputRouter));

            InputActionAsset actions = InputSystem.actions;
            if (actions == null)
            {
                this.logger.Log(
                    "No project-wide input actions are assigned; gameplay and cursor routing are inactive.",
                    GameLogLevel.Warning);
            }
            else
            {
                gameplayMap = actions.FindActionMap(GameplayActionMapName, false);
                if (gameplayMap == null)
                {
                    this.logger.Log(
                        $"Input action map '{GameplayActionMapName}' was not found; gameplay input cannot be gated.",
                        GameLogLevel.Warning);
                }

                uiMap = actions.FindActionMap(UIActionMapName, false);
                cancelAction = actions.FindAction(CancelActionPath, false);
                if (cancelAction != null)
                {
                    cancelAction.performed += OnCancelPerformed;
                }
                else
                {
                    this.logger.Log(
                        $"Input action '{CancelActionPath}' was not found; back and pause input is unavailable.",
                        GameLogLevel.Warning);
                }
            }

            Apply();
        }

        public event Action BackRequested;

        public bool IsGameplayInputAvailable { get; private set; }

        public void SetApplicationState(ApplicationState state)
        {
            if (applicationState == state)
            {
                return;
            }

            applicationState = state;
            Apply();
        }

        public void SetMenuInputRequirements(MenuInputRequirements requirements)
        {
            if (menuRequirements.Equals(requirements))
            {
                return;
            }

            menuRequirements = requirements;
            Apply();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            BackRequested = null;

            if (cancelAction != null)
            {
                cancelAction.performed -= OnCancelPerformed;
            }

            if (gameplayMap != null && gameplayMap.enabled)
            {
                gameplayMap.Disable();
            }
        }

        private void Apply()
        {
            bool gameplayAvailable = applicationState == ApplicationState.Playing
                && !menuRequirements.BlocksGameplayInput;
            IsGameplayInputAvailable = gameplayAvailable;

            if (gameplayMap != null)
            {
                if (gameplayAvailable && !gameplayMap.enabled)
                {
                    gameplayMap.Enable();
                }
                else if (!gameplayAvailable && gameplayMap.enabled)
                {
                    gameplayMap.Disable();
                }
            }

            //UI actions stay enabled because cancel drives back and pause; covered menus are made
            //non-interactive by the menu coordinator rather than by switching the map off
            if (uiMap != null && !uiMap.enabled)
            {
                uiMap.Enable();
            }

            bool cursorReleased = !gameplayAvailable || menuRequirements.ShowsCursor;
            Cursor.lockState = cursorReleased ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = cursorReleased;
        }

        private void OnCancelPerformed(InputAction.CallbackContext context)
        {
            if (disposed)
            {
                return;
            }

            BackRequested?.Invoke();
        }
    }
}
