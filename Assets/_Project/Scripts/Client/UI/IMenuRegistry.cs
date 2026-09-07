namespace DigBlocks.Client.UI
{
    //maps menu identity to a factory and metadata, keeping the coordinator free of authoring details
    public interface IMenuRegistry
    {
        bool Contains(MenuId id);

        bool TryGetRegistration(MenuId id, out MenuRegistration registration);
    }
}
