using System;
using DigBlocks.Client.Debugging;
using DigBlocks.Client.Flow;
using DigBlocks.Client.Input;
using DigBlocks.Client.Rendering;
using DigBlocks.Client.UI;
using DigBlocks.Core.Diagnostics;

namespace DigBlocks.Bootstrap
{
    //application-lifetime client presentation: UI root, navigation, input routing, and application flow
    internal sealed class ClientPresentation : IDisposable
    {
        private readonly UIRoot uiRoot;
        private readonly MenuCoordinator menus;
        private readonly ClientInputRouter input;
        private readonly DebugMenuController debugMenu;
        private readonly TerrainDebugBinder terrainDebug;
        private bool disposed;

        public ClientPresentation(
            UIRoot uiRoot,
            MenuRegistry registry,
            MenuCoordinator menus,
            ClientInputRouter input,
            ApplicationFlowController flow,
            DebugMenuController debugMenu,
            DebugOptions debugOptions,
            TerrainDebugBinder terrainDebug)
        {
            this.uiRoot = uiRoot ?? throw new ArgumentNullException(nameof(uiRoot));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.menus = menus ?? throw new ArgumentNullException(nameof(menus));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            Flow = flow ?? throw new ArgumentNullException(nameof(flow));
            this.debugMenu = debugMenu ?? throw new ArgumentNullException(nameof(debugMenu));
            DebugOptions = debugOptions ?? throw new ArgumentNullException(nameof(debugOptions));
            this.terrainDebug = terrainDebug ?? throw new ArgumentNullException(nameof(terrainDebug));

            //flow owns game intent, the debug controller owns the debug overlay; both need view binding
            menus.SetViewBinder(new CompositeMenuViewBinder(flow, debugMenu));
            menus.InputRequirementsChanged += input.SetMenuInputRequirements;
            flow.StateChanged += input.SetApplicationState;
            input.BackRequested += flow.RequestBack;

            input.SetApplicationState(flow.State);
            input.SetMenuInputRequirements(menus.InputRequirements);
        }

        public MenuRegistry Registry { get; }

        public ApplicationFlowController Flow { get; }

        //session services observe this too, for debug state that shader globals cannot carry
        public IDebugOptions DebugOptions { get; }
        public bool GameplayInputAvailable => input.IsGameplayInputAvailable;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            menus.InputRequirementsChanged -= input.SetMenuInputRequirements;
            Flow.StateChanged -= input.SetApplicationState;
            input.BackRequested -= Flow.RequestBack;

            debugMenu.Dispose();
            terrainDebug.Dispose();
            Flow.Dispose();
            input.Dispose();
            menus.SetViewBinder(null);
            menus.Dispose();

            if (uiRoot != null)
            {
                uiRoot.DestroySelf();
            }
        }
    }
}
