Shader "DigBlocks/Terrain"
{
    Properties
    {
        _BlockTextures("Block textures", 2DArray) = "" {}
        [MainColor] _BaseColor("Base color", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.15
        _Metallic("Metallic", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "TerrainGeometry.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
            half _Metallic;
        CBUFFER_END
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            half4 Fragment(TerrainVaryings i) : SV_Target
            {
                InputData input = (InputData)0;
                input.positionWS = i.positionWS; input.positionCS = i.positionCS;
                input.normalWS = i.normalWS; input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                input.bakedGI = SampleSH(i.normalWS); input.shadowMask = 1;
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.vertexLighting = VertexLighting(i.positionWS, i.normalWS);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = SAMPLE_TEXTURE2D_ARRAY(_BlockTextures, sampler_BlockTextures, i.uv, i.layer).rgb * _Tints[i.tint].rgb * _BaseColor.rgb;
                surface.alpha = 1; surface.normalTS = half3(0,0,1); surface.occlusion = 1;
                surface.smoothness = _Smoothness; surface.metallic = _Metallic;
                half4 color = UniversalFragmentPBR(input, surface);
                color.rgb = MixFog(color.rgb, i.fog);
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment EmptyFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;
            float4 ShadowVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID) : SV_POSITION
            {
                float3 position, normal; float2 uv; uint layer, tint;
                DecodeTerrain(vertex, instance, position, normal, uv, layer, tint);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 direction = normalize(_LightPosition - position);
                #else
                float3 direction = _LightDirection;
                #endif
                return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(position, normal, direction)));
            }
            half4 EmptyFragment() : SV_Target { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment DepthFragment
            half4 DepthFragment(TerrainVaryings i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex TerrainVertex
            #pragma fragment NormalFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 NormalFragment(TerrainVaryings i) : SV_Target
            {
                #if defined(_GBUFFER_NORMALS_OCT)
                float2 packed = PackNormalOctQuadEncode(i.normalWS);
                return half4(PackFloat2To888(saturate(packed * 0.5 + 0.5)), 0);
                #else
                return half4(i.normalWS, 0);
                #endif
            }
            ENDHLSL
        }
    }
}
