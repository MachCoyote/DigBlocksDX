using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    //creates a menu from a supplied builder; the seam a future code-authored menu factory plugs into
    public sealed class DelegateMenuFactory : IMenuFactory
    {
        private readonly Func<Transform, IMenuView> build;

        public DelegateMenuFactory(Func<Transform, IMenuView> build)
        {
            this.build = build ?? throw new ArgumentNullException(nameof(build));
        }

        public UniTask<IMenuView> CreateAsync(Transform parent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IMenuView view = build(parent);
            if (view == null)
            {
                throw new InvalidOperationException("A menu builder returned no view.");
            }

            return UniTask.FromResult(view);
        }
    }
}
