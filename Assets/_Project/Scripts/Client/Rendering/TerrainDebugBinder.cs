using System;
using DigBlocks.Core.Diagnostics;
using UnityEngine;

namespace DigBlocks.Client.Rendering
{
    public enum TerrainWireframeMode
    {
        Off = 0,

        //wires drawn over the shaded surface
        Overlay = 1,

        //only the wires survive; the surface between them is clipped in every terrain pass
        Only = 2
    }

    public enum TerrainOverdrawMode
    {
        Off = 0,

        //every geometry layer counts, including faces hidden behind the surface in front of them
        AllLayers = 1,

        //only fragments that survived the depth test, which is the shading the GPU actually paid for
        ShadedOnly = 2
    }

    public enum TerrainSecondaryCameraCulling
    {
        //every camera culls for its own view, so each viewport shows the terrain it can actually see
        OwnView = 0,

        //secondary cameras reuse the main camera's visible set, which shows what the main camera culled away
        MirrorMain = 1
    }

    //translates the shared debug switchboard into the terrain shader's globals. Rendering owns its
    //shader state; the debug menu only knows it flipped a numbered toggle.
    //Overdraw needs blend and depth state as well, which comes from material properties rather than
    //globals, so TerrainRenderService applies that half against the session's material clones.
    public sealed class TerrainDebugBinder : IDisposable
    {
        private static readonly int WireframeModeId = Shader.PropertyToID("_DigBlocksWireframeMode");
        private static readonly int WireframeThicknessId = Shader.PropertyToID("_DigBlocksWireframeThickness");
        private static readonly int WireframeColorId = Shader.PropertyToID("_DigBlocksWireframeColor");
        private static readonly int FullbrightId = Shader.PropertyToID("_DigBlocksFullbright");
        private static readonly int OverdrawModeId = Shader.PropertyToID("_DigBlocksOverdrawMode");
        private static readonly int OverdrawStepId = Shader.PropertyToID("_DigBlocksOverdrawStep");

        private readonly IDebugOptions options;
        private float wireframeThickness;
        private Color wireframeColor;
        private Color overdrawStep;
        private bool disposed;

        public TerrainDebugBinder(IDebugOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            wireframeThickness = 1.1f;
            wireframeColor = new Color(0.85f, 0.95f, 1f, 1f);

            //linear increment per counted layer. Red saturates on the first layer and green then blue
            //follow, so stacked layers read red, orange, yellow, white as the sum saturates on display
            overdrawStep = new Color(1f, 0.09f, 0.02f, 0f);

            this.options.StateChanged += OnStateChanged;
            Apply();
        }

        public TerrainWireframeMode WireframeMode { get; private set; }

        public bool Fullbright { get; private set; }

        public TerrainOverdrawMode OverdrawMode { get; private set; }

        //line half-width in fragments; wires stay one screen-space line thick at any distance
        public float WireframeThickness
        {
            get => wireframeThickness;
            set
            {
                wireframeThickness = Mathf.Max(value, 0.01f);
                Apply();
            }
        }

        public Color WireframeColor
        {
            get => wireframeColor;
            set
            {
                wireframeColor = value;
                Apply();
            }
        }

        //how fast the overdraw ramp climbs; lower values spread the colours over more layers
        public Color OverdrawStep
        {
            get => overdrawStep;
            set
            {
                overdrawStep = value;
                Apply();
            }
        }

        public static TerrainOverdrawMode ReadOverdrawMode(IDebugOptions options)
        {
            int state = options != null ? options.GetState(DebugToggleIds.Overdraw) : 0;
            return (TerrainOverdrawMode)Mathf.Clamp(state, 0, (int)TerrainOverdrawMode.ShadedOnly);
        }

        //the culling mode changes no shader state, so the renderer reads it without a binder instance
        public static TerrainSecondaryCameraCulling ReadSecondaryCameraCulling(IDebugOptions options)
        {
            int state = options != null ? options.GetState(DebugToggleIds.SecondaryCameraCulling) : 0;
            return (TerrainSecondaryCameraCulling)Mathf.Clamp(state, 0, (int)TerrainSecondaryCameraCulling.MirrorMain);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            options.StateChanged -= OnStateChanged;

            //globals outlive the session, so leaving them set would debug-render the next one
            WireframeMode = TerrainWireframeMode.Off;
            Fullbright = false;
            OverdrawMode = TerrainOverdrawMode.Off;
            Shader.SetGlobalFloat(WireframeModeId, 0f);
            Shader.SetGlobalFloat(FullbrightId, 0f);
            Shader.SetGlobalFloat(OverdrawModeId, 0f);
        }

        private void OnStateChanged(DebugToggleId changed, int state)
        {
            if (changed != DebugToggleIds.Wireframe
                && changed != DebugToggleIds.Fullbright
                && changed != DebugToggleIds.Overdraw)
            {
                return;
            }

            Apply();
        }

        private void Apply()
        {
            if (disposed)
            {
                return;
            }

            WireframeMode = (TerrainWireframeMode)Mathf.Clamp(
                options.GetState(DebugToggleIds.Wireframe),
                0,
                (int)TerrainWireframeMode.Only);
            Fullbright = options.IsEnabled(DebugToggleIds.Fullbright);
            OverdrawMode = ReadOverdrawMode(options);

            Shader.SetGlobalFloat(WireframeModeId, (int)WireframeMode);
            Shader.SetGlobalFloat(WireframeThicknessId, wireframeThickness);
            Shader.SetGlobalColor(WireframeColorId, wireframeColor);
            Shader.SetGlobalFloat(FullbrightId, Fullbright ? 1f : 0f);
            Shader.SetGlobalFloat(OverdrawModeId, (int)OverdrawMode);
            Shader.SetGlobalColor(OverdrawStepId, overdrawStep);
        }
    }
}
