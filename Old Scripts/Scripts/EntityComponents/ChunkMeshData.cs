using Unity.Entities;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;
using UnityEngine;
using UnityEngine.Rendering;

[BurstCompile]
public struct ChunkMeshData : IComponentData
{
    public UnsafeList<float3> vertices;
    public UnsafeList<int> triangles;
    public UnsafeList<float2> uvs;

    public int texWidth;
    public int texHeight;

    public BatchMaterialID materialID;
}

public struct ChunkMeshHolder : IComponentData
{
    public Mesh.MeshData mesh;
}
