Shader "Hidden/DigBlocks/TerrainBackendProbe"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            StructuredBuffer<uint3> _Quads;
            StructuredBuffer<uint> _Visible;
            TEXTURE2D_ARRAY(_Tiles);
            SAMPLER(sampler_Tiles);
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; nointerpolation uint layer : TEXCOORD1; };
            Varyings Vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                static const float2 corners[6] = { float2(0,0), float2(0,1), float2(1,1), float2(0,0), float2(1,1), float2(1,0) };
                uint3 q = _Quads[_Visible[instance]];
                float2 anchor = float2(q.x & 63u, (q.x >> 6) & 63u);
                float2 size = float2(((q.x >> 18) & 31u) + 1u, ((q.x >> 23) & 31u) + 1u);
                float2 uv = corners[vertex] * size;
                Varyings o;
                o.position = float4((anchor + uv) / float2(8,2) * 2 - 1, 0.5, 1);
                uint rotation = (q.y >> 24) & 3u;
                if (rotation == 1) uv = float2(-uv.y, uv.x);
                if (rotation == 2) uv = -uv;
                if (rotation == 3) uv = float2(uv.y, -uv.x);
                o.uv = uv;
                o.layer = q.y & 65535u;
                return o;
            }
            half4 Frag(Varyings i) : SV_Target { return SAMPLE_TEXTURE2D_ARRAY(_Tiles, sampler_Tiles, i.uv, i.layer); }
            ENDHLSL
        }
    }
}
