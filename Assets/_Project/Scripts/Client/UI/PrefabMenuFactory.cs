using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace DigBlocks.Client.UI
{
    public sealed class PrefabMenuFactory : IMenuFactory
    {
        private readonly GameObject prefab;
        private readonly bool stretchRootToLayer;

        public PrefabMenuFactory(GameObject prefab, bool stretchRootToLayer = true)
        {
            this.prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
            this.stretchRootToLayer = stretchRootToLayer;

            if (prefab.GetComponent<IMenuView>() == null)
            {
                throw new ArgumentException(
                    $"Menu prefab '{prefab.name}' needs a component implementing {nameof(IMenuView)}.",
                    nameof(prefab));
            }
        }

        public UniTask<IMenuView> CreateAsync(Transform parent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GameObject instance = UnityEngine.Object.Instantiate(prefab, parent, false);
            instance.name = prefab.name;
            instance.SetActive(false);

            if (stretchRootToLayer && instance.transform is RectTransform rectTransform)
            {
                Stretch(rectTransform);
            }

            var view = instance.GetComponent<IMenuView>();
            if (view == null)
            {
                UnityEngine.Object.Destroy(instance);
                throw new InvalidOperationException(
                    $"Menu prefab '{prefab.name}' produced an instance without an {nameof(IMenuView)} component.");
            }

            return UniTask.FromResult(view);
        }

        private static void Stretch(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }
    }
}
