using UnityEngine;

namespace DigBlocks.Client.UI
{
    //owns canvas configuration and the screen, overlay, and modal attachment points
    public sealed class UIRoot : MonoBehaviour, IUIRoot
    {
        [SerializeField]
        private Transform screenLayer;

        [SerializeField]
        private Transform overlayLayer;

        [SerializeField]
        private Transform modalLayer;

        private GameObject ownedEventSystem;

        public GameObject Root => gameObject;

        public Transform ScreenLayer => screenLayer;

        public Transform OverlayLayer => overlayLayer;

        public Transform ModalLayer => modalLayer;

        public Transform GetLayer(MenuLayer layer)
        {
            switch (layer)
            {
                case MenuLayer.Overlay:
                    return overlayLayer != null ? overlayLayer : transform;

                case MenuLayer.Modal:
                    return modalLayer != null ? modalLayer : transform;

                default:
                    return screenLayer != null ? screenLayer : transform;
            }
        }

        public bool HasAllLayers => screenLayer != null && overlayLayer != null && modalLayer != null;

        //destroys the root and anything it created for itself, such as a generated EventSystem
        public void DestroySelf()
        {
            if (ownedEventSystem != null)
            {
                Destroy(ownedEventSystem);
                ownedEventSystem = null;
            }

            if (gameObject != null)
            {
                Destroy(gameObject);
            }
        }

        internal void SetLayers(Transform screen, Transform overlay, Transform modal)
        {
            screenLayer = screen;
            overlayLayer = overlay;
            modalLayer = modal;
        }

        internal void SetOwnedEventSystem(GameObject eventSystemObject)
        {
            ownedEventSystem = eventSystemObject;
        }
    }
}
