namespace DigBlocks.Client.UI
{
    //binds and unbinds view intent events during the coordinator-driven menu lifecycle
    public interface IMenuViewBinder
    {
        void Bind(MenuId id, IMenuView view);

        void Unbind(MenuId id, IMenuView view);
    }
}
