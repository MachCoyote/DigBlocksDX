using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace DigBlocks.Client.Rendering.Editor
{
    //where a GPU completion token may be issued from. URP has already submitted a camera's context by the
    //time endCameraRendering runs, so a token queued into that context never reaches the GPU unless some
    //later camera submits the context for it. Graphics.ExecuteCommandBuffer does not depend on that, which
    //is why staging uploads always completed while frame snapshots did not.
    public static class TerrainCompletionProbe
    {
        private enum Issue { Context, Graphics }

        [MenuItem("DigBlocks/Verification/Terrain completion token path")]
        public static void Run()
        {
            Directory.CreateDirectory(".utmp");
            string path = ".utmp/terrain-completion-probe.txt";
            var report = new StringBuilder();
            report.AppendLine("Unity " + Application.unityVersion);
            report.AppendLine(SystemInfo.graphicsDeviceType + ": " + SystemInfo.graphicsDeviceName);
            report.AppendLine("sceneViews=" + SceneView.sceneViews.Count + ", allCameras=" + Camera.allCamerasCount);
            try
            {
                bool contextAlone = Probe(Issue.Context, false, out int callsA);
                bool graphicsAlone = Probe(Issue.Graphics, false, out int callsB);
                bool contextThenSecond = Probe(Issue.Context, true, out int callsC);

                report.AppendLine("context, one camera    -> completed=" + contextAlone + " (endCameraRendering x" + callsA + ")");
                report.AppendLine("graphics, one camera   -> completed=" + graphicsAlone + " (endCameraRendering x" + callsB + ")");
                report.AppendLine("context, second camera -> completed=" + contextThenSecond + " (endCameraRendering x" + callsC + ")");

                Check(graphicsAlone, "Graphics.ExecuteCommandBuffer issues the token with one camera");
                Check(!contextAlone, "ScriptableRenderContext.ExecuteCommandBuffer does not issue the token with one camera");
                //measured, not assumed: a later camera does not rescue the queued token, so the work is
                //dropped outright rather than deferred until something else submits.
                Check(!contextThenSecond, "A later camera does not flush a token queued into an already submitted context");
                report.AppendLine("PASS: a token queued into the context is dropped outright; the Graphics path issues it.");
                Debug.Log("Terrain completion token probe PASS. Evidence: " + Path.GetFullPath(path));
            }
            catch (Exception error)
            {
                report.AppendLine("FAIL: " + error);
                Debug.LogException(error);
            }
            finally
            {
                File.WriteAllText(path, report.ToString());
            }
        }

        //renders one camera with a readback token queued from its own endCameraRendering, optionally
        //rendering a second camera afterwards, then waits for every request the GPU actually received.
        private static bool Probe(Issue issue, bool renderSecond, out int endCameraCalls)
        {
            bool completed = false, queued = false;
            int calls = 0;
            var marker = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            var command = new CommandBuffer { name = "Terrain completion probe" };
            var target = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var primaryObject = new GameObject("Completion probe primary") { hideFlags = HideFlags.HideAndDontSave };
            var secondObject = new GameObject("Completion probe second") { hideFlags = HideFlags.HideAndDontSave };
            var primary = Configure(primaryObject);
            var second = Configure(secondObject);

            void Handler(ScriptableRenderContext context, Camera camera)
            {
                calls++;
                if (camera != primary || queued) return;
                queued = true;
                command.Clear();
                command.RequestAsyncReadback(marker, request => completed = !request.hasError);
                if (issue == Issue.Context) context.ExecuteCommandBuffer(command);
                else Graphics.ExecuteCommandBuffer(command);
            }

            RenderPipelineManager.endCameraRendering += Handler;
            try
            {
                marker.SetData(new uint[] { 0 });
                target.Create();
                RenderPipeline.SubmitRenderRequest(primary, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                if (renderSecond) RenderPipeline.SubmitRenderRequest(second, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                //only waits on requests the GPU was actually given; one that never reached it is not pending.
                AsyncGPUReadback.WaitAllRequests();
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= Handler;
                command.Dispose();
                marker.Dispose();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(primaryObject);
                UnityEngine.Object.DestroyImmediate(secondObject);
            }
            endCameraCalls = calls;
            return completed;
        }

        private static Camera Configure(GameObject owner)
        {
            var camera = owner.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 0;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.volumeLayerMask = 0;
            return camera;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
