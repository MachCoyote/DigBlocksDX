using System.Collections.Generic;
using DigBlocks.Core.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class TerrainCameraSetTests
    {
        private readonly List<GameObject> created = new();

        private Camera Make(string name)
        {
            var host = new GameObject(name);
            created.Add(host);
            var camera = host.AddComponent<Camera>();
            camera.cullingMask = ~0;
            return camera;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in created) Object.DestroyImmediate(host);
            created.Clear();
        }

        [Test]
        public void EveryOrdinaryGameCameraExceptTheSessionOwnIsCollected()
        {
            var primary = Make("primary");
            var second = Make("second");

            var collected = new List<Camera>();
            TerrainCameraSet.Collect(primary, collected);

            Assert.That(collected, Contains.Item(second));
            Assert.That(collected, Has.No.Member(primary));
        }

        [Test]
        public void DisabledAndOverlayAndMaskedCamerasAreRejected()
        {
            var primary = Make("primary");

            var disabled = Make("disabled");
            disabled.enabled = false;

            var overlay = Make("overlay");
            overlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;

            var masked = Make("masked");
            masked.cullingMask = ~(1 << TerrainCameraSet.TerrainLayer);

            Assert.That(TerrainCameraSet.IsEligible(disabled, primary), Is.False, "A disabled camera never renders.");
            Assert.That(TerrainCameraSet.IsEligible(overlay, primary), Is.False, "An overlay camera draws into the base camera stacking it.");
            Assert.That(TerrainCameraSet.IsEligible(masked, primary), Is.False, "The terrain layer is masked out.");
            Assert.That(TerrainCameraSet.IsEligible(primary, primary), Is.False, "The session camera is submitted by the renderer itself.");
            Assert.That(TerrainCameraSet.IsEligible(Make("plain"), primary), Is.True);
        }

#if UNITY_EDITOR
        //a scene view camera is not enabled and never appears in Camera.allCameras, so it has to be found
        //through the editor window list or terrain stays invisible in the viewport people actually inspect.
        //Scene views exist only in the editor, so this one case cannot run in a player.
        [Test]
        public void OpenSceneViewsAreCollectedDespiteNotBeingEnabledCameras()
        {
            if (UnityEditor.SceneView.sceneViews.Count == 0) Assert.Ignore("This editor session has no scene view open.");
            var primary = Make("primary");

            var collected = new List<Camera>();
            TerrainCameraSet.Collect(primary, collected);

            foreach (UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
            {
                if (view == null || view.camera == null) continue;
                Assert.That(view.camera.isActiveAndEnabled, Is.False, "The quirk this covers is that a scene view camera reports itself disabled.");
                Assert.That(collected, Contains.Item(view.camera));
            }
        }
#endif

        [Test]
        public void SecondaryCullingModeClampsToTheStatesTheMenuOffers()
        {
            var options = new DebugOptions();
            Assert.That(TerrainDebugBinder.ReadSecondaryCameraCulling(options), Is.EqualTo(TerrainSecondaryCameraCulling.OwnView));

            options.SetState(DebugToggleIds.SecondaryCameraCulling, 1);
            Assert.That(TerrainDebugBinder.ReadSecondaryCameraCulling(options), Is.EqualTo(TerrainSecondaryCameraCulling.MirrorMain));

            options.SetState(DebugToggleIds.SecondaryCameraCulling, 7);
            Assert.That(TerrainDebugBinder.ReadSecondaryCameraCulling(options), Is.EqualTo(TerrainSecondaryCameraCulling.MirrorMain));
            Assert.That(TerrainDebugBinder.ReadSecondaryCameraCulling(null), Is.EqualTo(TerrainSecondaryCameraCulling.OwnView));
        }
    }
}
