#ifndef DIGBLOCKS_TERRAIN_GEOMETRY
#define DIGBLOCKS_TERRAIN_GEOMETRY
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
struct ChunkData { float3 origin; uint start; uint count; uint active; uint reserved0; uint reserved1; };
StructuredBuffer<uint3> _Geometry;
StructuredBuffer<ChunkData> _Chunks;
StructuredBuffer<uint> _Visible;
StructuredBuffer<float4> _Tints;
TEXTURE2D_ARRAY(_BlockTextures);
SAMPLER(sampler_BlockTextures);
void DecodeTerrain(uint vertex, uint instance, out float3 position, out float3 normal, out float2 uv, out uint layer, out uint tint)
{
    static const float2 corners[6] = { float2(0,0), float2(0,1), float2(1,1), float2(0,0), float2(1,1), float2(1,0) };
    uint3 quad = _Geometry[_Visible[instance]];
    uint direction = (quad.x >> 28) & 7u;
    float3 u, v;
    if (direction == 0) { normal = float3(0,-1,0); u = float3(1,0,0); v = float3(0,0,-1); }
    else if (direction == 1) { normal = float3(0,1,0); u = float3(1,0,0); v = float3(0,0,1); }
    else if (direction == 2) { normal = float3(0,0,1); u = float3(-1,0,0); v = float3(0,1,0); }
    else if (direction == 3) { normal = float3(0,0,-1); u = float3(1,0,0); v = float3(0,1,0); }
    else if (direction == 4) { normal = float3(-1,0,0); u = float3(0,0,-1); v = float3(0,1,0); }
    else { normal = float3(1,0,0); u = float3(0,0,1); v = float3(0,1,0); }
    uv = corners[vertex] * float2(((quad.x >> 18) & 31u) + 1u, ((quad.x >> 23) & 31u) + 1u);
    position = _Chunks[quad.z & 0xffffffu].origin + float3(quad.x & 63u, (quad.x >> 6) & 63u, (quad.x >> 12) & 63u) + u * uv.x + v * uv.y;
    uint rotation = (quad.y >> 24) & 3u;
    if (rotation == 1) uv = float2(-uv.y, uv.x);
    else if (rotation == 2) uv = -uv;
    else if (rotation == 3) uv = float2(uv.y, -uv.x);
    layer = quad.y & 65535u; tint = (quad.y >> 16) & 255u;
}
struct TerrainVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    float2 uv : TEXCOORD2;
    nointerpolation uint layer : TEXCOORD3;
    nointerpolation uint tint : TEXCOORD4;
    float fog : TEXCOORD5;
};
TerrainVaryings TerrainVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    TerrainVaryings o;
    DecodeTerrain(vertex, instance, o.positionWS, o.normalWS, o.uv, o.layer, o.tint);
    o.positionCS = TransformWorldToHClip(o.positionWS);
    o.fog = ComputeFogFactor(o.positionCS.z);
    return o;
}
#endif
