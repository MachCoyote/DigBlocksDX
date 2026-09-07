using System;
using DigBlocks.Client.Flow;
using DigBlocks.Client.Input;
using DigBlocks.Client.UI;

namespace DigBlocks.Bootstrap
{
    //application-lifetime client presentation: UI root, navigation, input routing, and application flow
    internal sealed class ClientPresentation : IDisposable
    {
        private readonly UIRoot uiRoot;
        private readonly MenuCoordinator menus;
        private readonly ClientInputRouter input;
        private bool disposed;

        public ClientPresentation(
            UIRoot uiRoot,
            MenuRegistry registry,
            MenuCoordinator menus,
            ClientInputRouter input,
            ApplicationFlowController flow)
        {
            this.uiRoot = uiRoot ?? throw new ArgumentNullException(nameof(uiRoot));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.menus = menus ?? throw new ArgumentNullException(nameof(menus));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            Flow = flow ?? throw new ArgumentNullException(nameof(flow));

            menus.SetViewBinder(flow);
            menus.InputRequirementsChanged += input.SetMenuInputRequirements;
            flow.StateChanged += input.SetApplicationState;
            input.BackRequested += flow.RequestBack;

            input.SetApplicationState(flow.State);
            input.SetMenuInputRequirements(menus.InputRequirements);
        }

        public MenuRegistry Registry { get; }

        public ApplicationFlowController Flow { get; }

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
