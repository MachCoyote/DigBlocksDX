using System;
using System.Collections.Generic;

namespace DigBlocks.Client.UI
{
    //the coordinator holds one binder, so several presenters share it through this fan-out
    public sealed class CompositeMenuViewBinder : IMenuViewBinder
    {
        private readonly IMenuViewBinder[] binders;

        public CompositeMenuViewBinder(params IMenuViewBinder[] binders)
        {
            if (binders == null)
            {
                throw new ArgumentNullException(nameof(binders));
            }

            var accepted = new List<IMenuViewBinder>(binders.Length);
            for (int index = 0; index < binders.Length; index++)
            {
                if (binders[index] != null)
                {
                    accepted.Add(binders[index]);
                }
            }

            this.binders = accepted.ToArray();
        }

        public void Bind(MenuId id, IMenuView view)
        {
            for (int index = 0; index < binders.Length; index++)
            {
                binders[index].Bind(id, view);
            }
        }

        public void Unbind(MenuId id, IMenuView view)
        {
            for (int index = 0; index < binders.Length; index++)
            {
                binders[index].Unbind(id, view);
            }
        }
    }
}
