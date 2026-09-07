using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using DigBlocks.Core.Async;
using DigBlocks.Core.Hosting;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DigBlocks.Client.UI
{
    public sealed class MenuCoordinator : IMenuCoordinator, IDisposable
    {
        private readonly IMenuRegistry registry;
        private readonly IUIRoot uiRoot;
        private readonly IGameLogger logger;
        private readonly AsyncGate gate = new AsyncGate();
        private readonly List<MenuEntry> overlays = new List<MenuEntry>();
        private readonly List<MenuEntry> modals = new List<MenuEntry>();
        private readonly List<MenuEntry> activeStack = new List<MenuEntry>();
        private readonly Dictionary<MenuId, IMenuView> retainedViews = new Dictionary<MenuId, IMenuView>();

        private MenuEntry screen;
        private IMenuViewBinder binder;
        private MenuInputRequirements inputRequirements = MenuInputRequirements.None;
        private bool disposed;

        public MenuCoordinator(IMenuRegistry registry, IUIRoot uiRoot, IGameLogger logger)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.uiRoot = uiRoot ?? throw new ArgumentNullException(nameof(uiRoot));
            this.logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
                .CreateFor(nameof(MenuCoordinator));
        }

        public MenuInputRequirements InputRequirements => inputRequirements;

        public event Action<MenuInputRequirements> InputRequirementsChanged;

        public bool IsRegistered(MenuId id)
        {
            return registry.Contains(id);
        }

        public bool IsOpen(MenuId id)
        {
            return FindEntry(id) != null;
        }

        public void SetViewBinder(IMenuViewBinder binder)
        {
            this.binder = binder;
        }

        public async UniTask ShowScreenAsync(MenuId id, CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (!TryResolve(id, MenuLayer.Screen, out MenuRegistration registration))
                {
                    return;
                }

                if (screen != null && screen.Id == id)
                {
                    RefreshInteraction();
                    return;
                }

                //changing the primary screen clears the navigation that belonged to the old one
                await CloseLayerAsync(modals, cancellationToken);
                await CloseLayerAsync(overlays, cancellationToken);

                if (screen != null)
                {
                    await CloseEntryAsync(screen, cancellationToken);
                }

                await OpenEntryAsync(registration, cancellationToken);
                RefreshInteraction();
            }
        }

        public async UniTask ClearScreenAsync(CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (screen == null)
                {
                    return;
                }

                await CloseEntryAsync(screen, cancellationToken);
                RefreshInteraction();
            }
        }

        public async UniTask PushOverlayAsync(MenuId id, CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (!TryResolve(id, MenuLayer.Overlay, out MenuRegistration registration))
                {
                    return;
                }

                if (FindEntry(id) != null)
                {
                    logger.Log($"Menu '{id}' is already open; ignoring the overlay request.", GameLogLevel.Debug);
                    return;
                }

                await OpenEntryAsync(registration, cancellationToken);
                RefreshInteraction();
            }
        }

        public async UniTask ShowModalAsync(MenuId id, CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                if (!TryResolve(id, MenuLayer.Modal, out MenuRegistration registration))
                {
                    return;
                }

                if (FindEntry(id) != null)
                {
                    logger.Log($"Menu '{id}' is already open; ignoring the modal request.", GameLogLevel.Debug);
                    return;
                }

                await OpenEntryAsync(registration, cancellationToken);
                RefreshInteraction();
            }
        }

        public async UniTask CloseAsync(MenuId id, CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                MenuEntry entry = FindEntry(id);
                if (entry == null)
                {
                    return;
                }

                await CloseEntryAsync(entry, cancellationToken);
                RefreshInteraction();
            }
        }

        public async UniTask<bool> BackAsync(CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                MenuEntry target = TopInteractiveEntry();
                if (target == null)
                {
                    return false;
                }

                switch (target.Metadata.BackBehavior)
                {
                    case MenuBackBehavior.Close:
                        await CloseEntryAsync(target, cancellationToken);
                        RefreshInteraction();
                        return true;

                    case MenuBackBehavior.ViewHandled:
                        if (target.View is IMenuBackHandler handler)
                        {
                            await handler.OnBackAsync(cancellationToken);
                        }
                        else
                        {
                            logger.Log(
                                $"Menu '{target.Id}' declares view-handled back but implements no {nameof(IMenuBackHandler)}.",
                                GameLogLevel.Warning);
                        }

                        //back is absorbed either way so it cannot leak to covered menus
                        return true;

                    default:
                        return true;
                }
            }
        }

        public async UniTask ResetAsync(CancellationToken cancellationToken)
        {
            using (await gate.EnterAsync(cancellationToken))
            {
                await CloseLayerAsync(modals, cancellationToken);
                await CloseLayerAsync(overlays, cancellationToken);

                if (screen != null)
                {
                    await CloseEntryAsync(screen, cancellationToken);
                }

                RefreshInteraction();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            DisposeEntries(modals);
            DisposeEntries(overlays);

            if (screen != null)
            {
                DisposeEntry(screen);
                screen = null;
            }

            foreach (KeyValuePair<MenuId, IMenuView> retained in retainedViews)
            {
                retained.Value?.Dispose();
            }

            retainedViews.Clear();
            binder = null;
            InputRequirementsChanged = null;
        }

        private bool TryResolve(MenuId id, MenuLayer expectedLayer, out MenuRegistration registration)
        {
            if (!registry.TryGetRegistration(id, out registration))
            {
                logger.Log($"Menu '{id}' is not registered.", GameLogLevel.Error);
                return false;
            }

            if (registration.Metadata.Layer != expectedLayer)
            {
                logger.Log(
                    $"Menu '{id}' is registered for the {registration.Metadata.Layer} layer but was requested as {expectedLayer}.",
                    GameLogLevel.Error);
                registration = null;
                return false;
            }

            return true;
        }

        private async UniTask OpenEntryAsync(MenuRegistration registration, CancellationToken cancellationToken)
        {
            IMenuView view = await ResolveViewAsync(registration, cancellationToken);
            var entry = new MenuEntry(registration, view);
            Attach(entry);
            binder?.Bind(entry.Id, view);

            try
            {
                //covered menus stop interacting before the new menu finishes its opening presentation
                RefreshInteraction();
                await view.OpenAsync(cancellationToken);
                entry.State = MenuLifecycleState.Open;
            }
            catch (Exception exception)
            {
                logger.Log($"Menu '{entry.Id}' failed to open.", GameLogLevel.Error, exception);
                binder?.Unbind(entry.Id, view);
                Detach(entry);
                view.Dispose();
                RefreshInteraction();
                throw;
            }
        }

        private async UniTask<IMenuView> ResolveViewAsync(
            MenuRegistration registration,
            CancellationToken cancellationToken)
        {
            Transform parent = uiRoot.GetLayer(registration.Metadata.Layer);

            if (retainedViews.TryGetValue(registration.Id, out IMenuView retained))
            {
                retainedViews.Remove(registration.Id);
                if (retained != null && retained.Root != null)
                {
                    Reparent(retained, parent);
                    return retained;
                }
            }

            IMenuView view = await registration.Factory.CreateAsync(parent, cancellationToken);
            if (view == null || view.Root == null)
            {
                throw new InvalidOperationException($"The factory for menu '{registration.Id}' produced no view.");
            }

            Reparent(view, parent);
            return view;
        }

        private static void Reparent(IMenuView view, Transform parent)
        {
            Transform viewTransform = view.Root.transform;
            if (viewTransform.parent != parent)
            {
                viewTransform.SetParent(parent, false);
            }

            //newest menu in a layer draws and receives input above its siblings
            viewTransform.SetAsLastSibling();
        }

        private async UniTask CloseEntryAsync(MenuEntry entry, CancellationToken cancellationToken)
        {
            entry.State = MenuLifecycleState.Closing;
            CaptureSelection(entry);
            RefreshInteraction();

            try
            {
                await entry.View.CloseAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.Log($"Menu '{entry.Id}' failed to close cleanly.", GameLogLevel.Error, exception);
            }

            entry.State = MenuLifecycleState.Closed;
            binder?.Unbind(entry.Id, entry.View);
            Detach(entry);

            if (entry.Metadata.Retention == MenuRetentionPolicy.Retain && entry.View.Root != null)
            {
                retainedViews[entry.Id] = entry.View;
            }
            else
            {
                entry.View.Dispose();
            }
        }

        private async UniTask CloseLayerAsync(List<MenuEntry> layer, CancellationToken cancellationToken)
        {
            while (layer.Count > 0)
            {
                MenuEntry top = layer[layer.Count - 1];
                await CloseEntryAsync(top, cancellationToken);
            }
        }

        private void Attach(MenuEntry entry)
        {
            switch (entry.Layer)
            {
                case MenuLayer.Overlay:
                    overlays.Add(entry);
                    break;

                case MenuLayer.Modal:
                    modals.Add(entry);
                    break;

                default:
                    screen = entry;
                    break;
            }
        }

        private void Detach(MenuEntry entry)
        {
            switch (entry.Layer)
            {
                case MenuLayer.Overlay:
                    overlays.Remove(entry);
                    break;

                case MenuLayer.Modal:
                    modals.Remove(entry);
                    break;

                default:
                    if (screen == entry)
                    {
                        screen = null;
                    }

                    break;
            }
        }

        private MenuEntry FindEntry(MenuId id)
        {
            if (screen != null && screen.Id == id && screen.State != MenuLifecycleState.Closing)
            {
                return screen;
            }

            for (int index = 0; index < overlays.Count; index++)
            {
                if (overlays[index].Id == id && overlays[index].State != MenuLifecycleState.Closing)
                {
                    return overlays[index];
                }
            }

            for (int index = 0; index < modals.Count; index++)
            {
                if (modals[index].Id == id && modals[index].State != MenuLifecycleState.Closing)
                {
                    return modals[index];
                }
            }

            return null;
        }

        private void BuildActiveStack()
        {
            activeStack.Clear();

            if (screen != null)
            {
                activeStack.Add(screen);
            }

            activeStack.AddRange(overlays);
            activeStack.AddRange(modals);
        }

        private MenuEntry TopInteractiveEntry()
        {
            BuildActiveStack();

            for (int index = activeStack.Count - 1; index >= 0; index--)
            {
                MenuEntry entry = activeStack[index];
                if (entry.State != MenuLifecycleState.Closing)
                {
                    return entry;
                }
            }

            return null;
        }

        //applies focus and interaction rules, then reports what the visible UI needs from input
        private void RefreshInteraction()
        {
            BuildActiveStack();
            CaptureCurrentSelection();

            bool allowInteraction = true;
            MenuEntry focusTarget = null;

            for (int index = activeStack.Count - 1; index >= 0; index--)
            {
                MenuEntry entry = activeStack[index];
                if (entry.State == MenuLifecycleState.Closing)
                {
                    //a closing menu already disabled its own interaction and must not block the menu below it
                    continue;
                }

                entry.View.SetInteractable(allowInteraction);

                if (allowInteraction && focusTarget == null)
                {
                    focusTarget = entry;
                }

                allowInteraction = allowInteraction && !entry.Metadata.BlocksInteractionUnderneath;
            }

            ApplyFocus(focusTarget);
            PublishInputRequirements();
        }

        private void CaptureCurrentSelection()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null)
            {
                return;
            }

            for (int index = 0; index < activeStack.Count; index++)
            {
                MenuEntry entry = activeStack[index];
                if (entry.View.Root != null && selected.transform.IsChildOf(entry.View.Root.transform))
                {
                    entry.PreviousSelection = selected;
                    return;
                }
            }
        }

        private void CaptureSelection(MenuEntry entry)
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null || entry.View.Root == null)
            {
                return;
            }

            if (selected.transform.IsChildOf(entry.View.Root.transform))
            {
                entry.PreviousSelection = selected;
            }
        }

        private void ApplyFocus(MenuEntry focusTarget)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return;
            }

            if (focusTarget == null)
            {
                eventSystem.SetSelectedGameObject(null);
                return;
            }

            GameObject target = null;

            if (focusTarget.PreviousSelection != null && focusTarget.PreviousSelection.activeInHierarchy)
            {
                target = focusTarget.PreviousSelection;
            }
            else if (focusTarget.View.InitialSelection != null
                && focusTarget.View.InitialSelection.gameObject.activeInHierarchy)
            {
                target = focusTarget.View.InitialSelection.gameObject;
            }

            if (target != null)
            {
                eventSystem.SetSelectedGameObject(target);
                return;
            }

            GameObject selected = eventSystem.currentSelectedGameObject;
            if (selected != null
                && focusTarget.View.Root != null
                && !selected.transform.IsChildOf(focusTarget.View.Root.transform))
            {
                eventSystem.SetSelectedGameObject(null);
            }
        }

        private void PublishInputRequirements()
        {
            bool anyMenuOpen = false;
            bool blocksGameplayInput = false;
            bool showsCursor = false;

            for (int index = 0; index < activeStack.Count; index++)
            {
                MenuEntry entry = activeStack[index];
                if (entry.State == MenuLifecycleState.Closing)
                {
                    continue;
                }

                anyMenuOpen = true;
                blocksGameplayInput |= entry.Metadata.BlocksGameplayInput;
                showsCursor |= entry.Metadata.ShowsCursor;
            }

            var next = new MenuInputRequirements(anyMenuOpen, blocksGameplayInput, showsCursor);
            if (next.Equals(inputRequirements))
            {
                return;
            }

            inputRequirements = next;
            InputRequirementsChanged?.Invoke(next);
        }

        private void DisposeEntries(List<MenuEntry> layer)
        {
            for (int index = layer.Count - 1; index >= 0; index--)
            {
                DisposeEntry(layer[index]);
            }

            layer.Clear();
        }

        private void DisposeEntry(MenuEntry entry)
        {
            binder?.Unbind(entry.Id, entry.View);
            entry.View.Dispose();
        }
    }
}
