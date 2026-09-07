using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Burst;
using Unity.Entities;
using System;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

[BurstCompile]
public struct Chunk : IComponentData
{
    public UInt64 id;
    public Entity selfEntity;
    public long posFromWOX; //the real chunk position in the world, not taking into account the floating origin. the world space position of the chunk will be in it's localtransform
    public long posFromWOY;
    public long posFromWOZ;
    public UnsafeList<ushort> blocks; //flattened 3D array of block ids
    public byte chunkSize; 

    [BurstCompile]
    public int ChunkRelativePosToNeighborDataIndex(int3 pos)
    {
        int chunkSize = GenerationSettings.chunkSize;
        return pos.x + pos.y * chunkSize + pos.z * chunkSize * chunkSize;
    }

    [BurstCompile]
    public int PosToFlattenedIndex(int x, int y, int z)
    {
        return x + (y * chunkSize) + (z * chunkSize * chunkSize);
    }

    [BurstCompile]
    public int3 FlattenedIndexToPos(int index)
    {
        int3 pos = new int3();
        pos.x = index % chunkSize;
        pos.y = (index / chunkSize) % chunkSize;
        pos.z = index / (chunkSize * chunkSize);
        return pos;
    }
}

[BurstCompile]
public struct ChunkPrefab : IComponentData
{
    public UInt64 id;
    public Entity selfEntity;
    public long posFromWOX; //the real chunk position in the world, not taking into account the floating origin. the world space position of the chunk will be in it's localtransform
    public long posFromWOY;
    public long posFromWOZ;
    public UnsafeList<ushort> blocks; //flattened 3D array of block ids
    public byte chunkSize;

    [BurstCompile]
    public int ChunkRelativePosToNeighborDataIndex(int3 pos)
    {
        int chunkSize = GenerationSettings.chunkSize;
        return pos.x + pos.y * chunkSize + pos.z * chunkSize * chunkSize;
    }

    [BurstCompile]
    public int PosToFlattenedIndex(int x, int y, int z)
    {
        return x + (y * chunkSize) + (z * chunkSize * chunkSize);
    }

    [BurstCompile]
    public int3 FlattenedIndexToPos(int index)
    {
        int3 pos = new int3();
        pos.x = index % chunkSize;
        pos.y = (index / chunkSize) % chunkSize;
        pos.z = index / (chunkSize * chunkSize);
        return pos;
    }
}