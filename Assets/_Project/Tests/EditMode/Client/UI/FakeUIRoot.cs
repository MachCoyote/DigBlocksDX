using DigBlocks.Client.UI;
using UnityEngine;

namespace DigBlocks.Client.Tests.UI
{
    internal sealed class FakeUIRoot : IUIRoot
    {
        private readonly Transform screenLayer;
        private readonly Transform overlayLayer;
        private readonly Transform modalLayer;

        public FakeUIRoot()
        {
            Root = new GameObject("Fake UI Root");
            screenLayer = CreateLayer("Screen");
            overlayLayer = CreateLayer("Overlay");
            modalLayer = CreateLayer("Modal");
        }

        public GameObject Root { get; }

        public Transform GetLayer(MenuLayer layer)
        {
            switch (layer)
            {
                case MenuLayer.Overlay:
                    return overlayLayer;

                case MenuLayer.Modal:
                    return modalLayer;

                default:
                    return screenLayer;
            }
        }

        public void Destroy()
        {
            if (Root != null)
            {
                Object.DestroyImmediate(Root);
            }
        }

        private Transform CreateLayer(string name)
        {
            var layerObject = new GameObject(name);
            layerObject.transform.SetParent(Root.transform, false);
            return layerObject.transform;
        }
    }
}
