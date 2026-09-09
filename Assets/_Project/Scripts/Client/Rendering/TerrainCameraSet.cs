using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DigBlocks.Client.Rendering
{
    //which cameras terrain submits to besides the one the session drives. An indirect draw is addressed
    //to a single camera, so every viewport that should show terrain has to be named here.
    public static class TerrainCameraSet
    {
        //an indirect submission carries no GameObject, so it answers to the layer named on the draw itself
        public const int TerrainLayer = 0;

        private static Camera[] buffer = new Camera[8];

        //the primary is excluded; it is submitted by the caller before the rest of the set is collected
        public static void Collect(Camera primary, List<Camera> into)
        {
            if (into == null) return;
            int count = Camera.allCamerasCount;
            if (buffer.Length < count) buffer = new Camera[Mathf.NextPowerOfTwo(count)];
            Camera.GetAllCameras(buffer);
            for (int i = 0; i < count; i++)
            {
                var camera = buffer[i];
                buffer[i] = null;
                if (IsEligible(camera, primary)) into.Add(camera);
            }
#if UNITY_EDITOR
            //scene views are not enabled cameras and never appear in the global list
            foreach (UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
                if (view != null && IsEligible(view.camera, primary)) into.Add(view.camera);
#endif
        }

        public static bool IsEligible(Camera camera, Camera primary)
        {
            if (camera == null || camera == primary) return false;

            //a scene view camera renders on demand and reports itself disabled, so type alone decides it
            if (camera.cameraType == CameraType.SceneView) return true;

            //preview and reflection cameras render outside the frame loop and would strand their submissions
            if (camera.cameraType != CameraType.Game || !camera.isActiveAndEnabled) return false;
            if ((camera.cullingMask & (1 << TerrainLayer)) == 0) return false;

            //an overlay camera draws into the base camera stacking it; submitting again would draw terrain twice
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            return data == null || data.renderType == CameraRenderType.Base;
        }
    }
}
