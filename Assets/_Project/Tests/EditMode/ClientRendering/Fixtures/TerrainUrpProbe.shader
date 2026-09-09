Shader "Hidden/DigBlocks/TerrainUrpProbe"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        StructuredBuffer<uint3> _Quads;
        void Geometry(uint vertex, uint instance, out float3 position, out float3 normal)
        {
            static const float2 corners[6] = { float2(0,0), float2(0,1), float2(1,1), float2(0,0), float2(1,1), float2(1,0) };
            uint3 q = _Quads[instance];
            position = float3(q.x & 63u, (q.x >> 6) & 63u, (q.x >> 12) & 63u);
            float2 size = float2(((q.x >> 18) & 31u) + 1u, ((q.x >> 23) & 31u) + 1u);
            float2 uv = corners[vertex] * size;
            if (((q.x >> 28) & 7u) == 1u) { position += float3(uv.x, 0, uv.y); normal = float3(0,1,0); }
            else { position += float3(uv.x, uv.y, 0); normal = float3(0,0,-1); }
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normal : TEXCOORD1; };
            Varyings Vert(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                Varyings o;
                Geometry(vertex, instance, o.positionWS, o.normal);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                return half4(0.03 + light.color * saturate(dot(i.normal, light.direction)) * light.shadowAttenuation * light.distanceAttenuation, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Off ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            float3 _LightDirection;
            float4 ShadowVert(uint vertex : SV_VertexID, uint instance : SV_InstanceID) : SV_POSITION
            {
                float3 position, normal;
                Geometry(vertex, instance, position, normal);
                float4 clip = TransformWorldToHClip(ApplyShadowBias(position, normal, _LightDirection));
                #if UNITY_REVERSED_Z
                clip.z = min(clip.z, UNITY_NEAR_CLIP_VALUE * clip.w);
                #else
                clip.z = max(clip.z, UNITY_NEAR_CLIP_VALUE * clip.w);
                #endif
                return clip;
            }
            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
