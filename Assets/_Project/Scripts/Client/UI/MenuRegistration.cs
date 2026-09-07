using System;

namespace DigBlocks.Client.UI
{
    public sealed class MenuRegistration
    {
        public MenuRegistration(MenuId id, IMenuFactory factory, MenuMetadata metadata)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("A menu registration requires a valid identifier.", nameof(id));
            }

            Id = id;
            Factory = factory ?? throw new ArgumentNullException(nameof(factory));
            Metadata = metadata;
        }

        public MenuId Id { get; }

        public IMenuFactory Factory { get; }

        public MenuMetadata Metadata { get; }
    }
}
