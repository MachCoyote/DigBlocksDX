using DigBlocks.Core.Hosting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace DigBlocks.Client.UI
{
    //builds the persistent UI root; an authored prefab takes precedence over the generated hierarchy
    public static class UIRootFactory
    {
        private const string RootName = "DigBlocks UI Root";
        private const string EventSystemName = "DigBlocks UI Event System";
        private const int OverlaySortingOrder = 100;
        private const int ModalSortingOrder = 200;

        public static UIRoot Create(UIRoot prefab, IGameLogger logger)
        {
            UIRoot root;
            if (prefab != null)
            {
                root = Object.Instantiate(prefab);
                if (!root.HasAllLayers)
                {
                    logger?.Log(
                        "The UI root prefab is missing one or more layer references; menus will attach to its root.",
                        GameLogLevel.Warning);
                }
            }
            else
            {
                root = BuildDefault();
            }

            root.gameObject.name = RootName;
            Object.DontDestroyOnLoad(root.gameObject);

            GameObject createdEventSystem = EnsureEventSystem(logger);
            if (createdEventSystem != null)
            {
                root.SetOwnedEventSystem(createdEventSystem);
            }

            return root;
        }

        private static UIRoot BuildDefault()
        {
            var rootObject = new GameObject(
                RootName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));

            var canvas = rootObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = rootObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            //nested canvases keep overlay and modal rebuilds off the screen layer and fix draw order
            Transform screenLayer = CreateLayer(rootObject.transform, "Screen Layer", null);
            Transform overlayLayer = CreateLayer(rootObject.transform, "Overlay Layer", OverlaySortingOrder);
            Transform modalLayer = CreateLayer(rootObject.transform, "Modal Layer", ModalSortingOrder);

            var root = rootObject.AddComponent<UIRoot>();
            root.SetLayers(screenLayer, overlayLayer, modalLayer);
            return root;
        }

        private static Transform CreateLayer(Transform parent, string name, int? sortingOrder)
        {
            var layerObject = new GameObject(name, typeof(RectTransform));
            var rectTransform = (RectTransform)layerObject.transform;
            rectTransform.SetParent(parent, false);
            Stretch(rectTransform);

            if (sortingOrder.HasValue)
            {
                var canvas = layerObject.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = sortingOrder.Value;
                layerObject.AddComponent<GraphicRaycaster>();
            }

            return rectTransform;
        }

        private static void Stretch(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.localScale = Vector3.one;
        }

        private static GameObject EnsureEventSystem(IGameLogger logger)
        {
            EventSystem existing = EventSystem.current;
            if (existing == null)
            {
                EventSystem[] found = Object.FindObjectsByType<EventSystem>();
                if (found.Length > 0)
                {
                    existing = found[0];
                }
            }

            GameObject created = null;
            if (existing == null)
            {
                created = new GameObject(EventSystemName, typeof(EventSystem));
                Object.DontDestroyOnLoad(created);
                existing = created.GetComponent<EventSystem>();
            }

            ConfigureInputModule(existing, logger);
            return created;
        }

        private static void ConfigureInputModule(EventSystem eventSystem, IGameLogger logger)
        {
            var module = eventSystem.GetComponent<BaseInputModule>();
            if (module == null)
            {
                //adding the module assigns the package default UI actions during OnEnable
                module = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }

            if (module is not InputSystemUIInputModule inputModule)
            {
                logger?.Log(
                    $"The event system uses {module.GetType().Name}; UI input routing expects the input system module.",
                    GameLogLevel.Warning);
                return;
            }

            InputActionAsset projectActions = InputSystem.actions;
            if (projectActions == null)
            {
                logger?.Log(
                    "No project-wide input actions are assigned; UI input falls back to the package defaults.",
                    GameLogLevel.Warning);
                return;
            }

            if (inputModule.actionsAsset != projectActions)
            {
                //remaps the module onto the game's own UI action map by action name
                inputModule.actionsAsset = projectActions;
            }
        }
    }
}
