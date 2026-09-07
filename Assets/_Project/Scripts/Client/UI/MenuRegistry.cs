using System;
using System.Collections.Generic;

namespace DigBlocks.Client.UI
{
    public sealed class MenuRegistry : IMenuRegistry
    {
        private readonly Dictionary<MenuId, MenuRegistration> registrations =
            new Dictionary<MenuId, MenuRegistration>();

        public IEnumerable<MenuId> RegisteredIds => registrations.Keys;

        public void Register(MenuRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (registrations.ContainsKey(registration.Id))
            {
                throw new InvalidOperationException($"Menu '{registration.Id}' is already registered.");
            }

            registrations.Add(registration.Id, registration);
        }

        public bool Contains(MenuId id)
        {
            return id.IsValid && registrations.ContainsKey(id);
        }

        public bool TryGetRegistration(MenuId id, out MenuRegistration registration)
        {
            if (!id.IsValid)
            {
                registration = null;
                return false;
            }

            return registrations.TryGetValue(id, out registration);
        }
    }
}
