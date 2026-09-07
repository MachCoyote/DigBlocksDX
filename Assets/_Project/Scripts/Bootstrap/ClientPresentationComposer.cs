using System.Threading;
using DigBlocks.Client.Flow;
using DigBlocks.Client.Input;
using DigBlocks.Client.UI;
using DigBlocks.Client.UI.Placeholders;
using DigBlocks.Core.Hosting;
using DigBlocks.Core.Launch;
using DigBlocks.Core.Session;

namespace DigBlocks.Bootstrap
{
    internal static class ClientPresentationComposer
    {
        public static ClientPresentation Compose(
            LaunchOptions launchOptions,
            MenuCatalog menuCatalog,
            UIRoot uiRootPrefab,
            string developmentWorldId,
            IGameSessionController sessions,
            IApplicationLifetime lifetime,
            IGameLogger logger,
            CancellationToken applicationLifetime)
        {
            UIRoot uiRoot = UIRootFactory.Create(uiRootPrefab, logger);

            var registry = new MenuRegistry();
            if (menuCatalog != null)
            {
                menuCatalog.RegisterInto(registry, logger);
            }
            else
            {
                logger.Log(
                    "No menu catalog is assigned on the bootstrap object; only placeholder menus are available.",
                    GameLogLevel.Warning);
            }

            PlaceholderMenus.RegisterMissing(registry, logger);

            var menus = new MenuCoordinator(registry, uiRoot, logger);
            var input = new ClientInputRouter(logger);
            var flow = new ApplicationFlowController(
                menus,
                sessions,
                lifetime,
                launchOptions,
                developmentWorldId,
                logger,
                applicationLifetime);

            return new ClientPresentation(uiRoot, registry, menus, input, flow);
        }
    }
}
