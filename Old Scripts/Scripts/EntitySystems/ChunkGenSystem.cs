using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using System;
using Unity.VisualScripting;
using Unity.Rendering;
using Unity.Collections.LowLevel.Unsafe;

[BurstCompile]
[CreateAfter(typeof(BootstrapSystem))]
[UpdateAfter(typeof(BootstrapSystem))]
public partial struct ChunkGenSystem : ISystem
{
    public EntityQuery prefabHolderQuery;

    Entity chunkPrefab;

    SceneSection subScene;
    SceneTag subSceneTag;

    int generateStep;

    //chunk settings
    byte chunkSize;
    float2 noiseScale;
    float2 noiseOffset;

    public int terrainHeightOffset;
    public int terrainHeightIntensity;
    public float2 periodOfRepetion;

    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        PrefabSingleton prefabSingleton = SystemAPI.GetSingleton<PrefabSingleton>();

        chunkPrefab = prefabSingleton.chunk;

        generateStep = 0;

        //initialize noise settings
        chunkSize = GenerationSettings.chunkSize;
        noiseScale = new float2(1f, 1f);
        noiseOffset = new float2(0f, 0f);

        terrainHeightOffset = 60;
        terrainHeightIntensity = 5;

        //set to highest possible float2
        periodOfRepetion = new float2(float.MaxValue, float.MaxValue);

        //------------------

        int chunkGenRadius = 32;

        //make chunks according to the chunk stack radius and heighy
        //height will be iterated over ***LAST*** as each stack has to be genereated together
        for (int x = -chunkGenRadius; x < chunkGenRadius; x++)
        {
            for (int z = -chunkGenRadius; z < chunkGenRadius; z++)
            {
                bool hitTopYet = false;
                int y = 0;
                int ttl = 99;
                while (!hitTopYet)
                {
                    if (ttl <= 0)
                    {
                        Debug.LogError("Chunk generation took too long, breaking loop");
                        break;
                    }

                    if (x == 0 && y == 0 && z == 0) Debug.Log("Generating chunk at 0,0,0");

                    hitTopYet = GenerateChunk(ref state, x, y, z);

                    if (x == 0 && y == 0 && z == 0) Debug.Log($"hittop {hitTopYet}");

                    y++;
                    ttl--;
                }
            }
        }

        //GenerateChunk(ref state, 0, 0, 0);

        randomSeed = 1;

        state.EntityManager.DestroyEntity(chunkPrefab);
    }

    uint randomSeed;

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
    }


    [BurstCompile]
    public bool GenerateChunk(ref SystemState state, long x, long y, long z) //returns true if hit top of terrain
    {
        bool hitTopYet = false;
        float3 chunkPos = new float3(x * chunkSize, y * chunkSize, z * chunkSize);
        Entity chunk = CreateChunk(ref state, x * chunkSize, y * chunkSize, z * chunkSize);
        Chunk chunkCopy = state.EntityManager.GetComponentData<Chunk>(chunk);
        //fill chunk with blocks
        for (byte i = 0; i < chunkSize; i++)
        {
            for (byte j = 0; j < chunkSize; j++)
            {
                float perlinCoordX = noiseOffset.x + (chunkPos.x + i) / (float)chunkSize * noiseScale.x;
                float perlinCoordY = noiseOffset.y + (chunkPos.z + j) / (float)chunkSize * noiseScale.y;
                float2 perlinCoord = new float2(perlinCoordX, perlinCoordY);
                float secondPerlinCoordX = noiseOffset.x + (chunkPos.x + i) / (float)chunkSize * noiseScale.x;
                float secondPerlinCoordY = noiseOffset.y + (chunkPos.z + j) / (float)chunkSize * noiseScale.y;
                secondPerlinCoordX *= 0.2f;
                secondPerlinCoordY *= 0.2f;
                float2 secondPerlinCoord = new float2(secondPerlinCoordX, secondPerlinCoordY);
                float thirdPerlinCoordX = noiseOffset.x + (chunkPos.x + i) / (float)chunkSize * noiseScale.x;
                float thirdPerlinCoordY = noiseOffset.y + (chunkPos.z + j) / (float)chunkSize * noiseScale.y;
                thirdPerlinCoordX *= 0.08f;
                thirdPerlinCoordY *= 0.08f;
                float2 thirdPerlinCoord = new float2(thirdPerlinCoordX, thirdPerlinCoordY);
                float fourthPerlinCoordX = noiseOffset.x + (chunkPos.x + i) / (float)chunkSize * noiseScale.x;
                float fourthPerlinCoordY = noiseOffset.y + (chunkPos.z + j) / (float)chunkSize * noiseScale.y;
                fourthPerlinCoordX *= 0.03f;
                fourthPerlinCoordY *= 0.03f;
                float2 fourthPerlinCoord = new float2(fourthPerlinCoordX, fourthPerlinCoordY);

                int blockStackHeight = (int)math.ceil(((noise.cnoise(perlinCoord) * 1.5f) + (noise.cnoise(secondPerlinCoord) * 8) + (noise.cnoise(thirdPerlinCoord) * 6) + (noise.cnoise(fourthPerlinCoord) * 10)) * terrainHeightIntensity + terrainHeightOffset);
                //generate top to bottom
                for (byte k = (byte)(chunkSize - 1); k >= -0; k--)
                {
                    if (k == 255) break;

                    //figure out what exact block y coordinate we are at in the chunk stack
                    long blockY = y * chunkSize + k;

                    //set top layer to grass
                    if (blockY == blockStackHeight) chunkCopy.blocks[(ushort)GetChunkIndex(i, k, j, chunkSize)] = 1;

                    //set next 16 layers to dirt
                    if (blockY < blockStackHeight && blockY >= blockStackHeight - 16) chunkCopy.blocks[(ushort)GetChunkIndex(i, k, j, chunkSize)] = 2;

                    //set everything below to stone
                    if (blockY < blockStackHeight - 16) chunkCopy.blocks[(ushort)GetChunkIndex(i, k, j, chunkSize)] = 3;

                    //set bottom layer to bedrock
                    if (blockY == 0) chunkCopy.blocks[(ushort)GetChunkIndex(i, k, j, chunkSize)] = 4;
                    //anything above top layer is air
                    if (blockY > blockStackHeight) chunkCopy.blocks[(ushort)GetChunkIndex(i, k, j, chunkSize)] = 0;
                }
            }
        }
        state.EntityManager.SetComponentData(chunk, chunkCopy);
        //if the entire chunk bottom layer of the chunk is air, we have reached the top of the terrain
        bool notAirFlag = false;
        for (byte i = 0; i < chunkSize; i++)
        {
            for (byte j = 0; j < chunkSize; j++)
            {
                if (chunkCopy.blocks[(ushort)GetChunkIndex(i, 0, j, chunkSize)] != 0)
                {
                    notAirFlag = true;
                    break;
                }
            }
        }

        if (!notAirFlag)
        {
            hitTopYet = true;
        }
        return hitTopYet;
    }

    [BurstCompile]
    public Entity CreateChunk(ref SystemState state, long x, long y, long z)
    {
        if (x == 0 && y == 0 && z == 0) Debug.Log("Creating chunk at ORIGIN");

        //instantiate entity, already marked as unrendered by default
        Entity chunkEntity = state.EntityManager.Instantiate(chunkPrefab);

        //get origin offset
        float3 originOffset = new float3(GenerationSettings.tempWorldOriginOffsetX, GenerationSettings.tempWorldOriginOffsetY, GenerationSettings.tempWorldOriginOffsetZ);

        //initialize localtransform
        state.EntityManager.SetComponentData(chunkEntity, new LocalTransform { Position = new float3(x, y, z) + originOffset, Rotation = quaternion.identity, Scale = 1 });

        //set the chunk position in the world
        Chunk chunk = state.EntityManager.GetComponentData<Chunk>(chunkEntity);
        chunk.posFromWOX = x;
        chunk.posFromWOY = y;
        chunk.posFromWOZ = z;
        chunk.blocks = new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent);

        //set whole chunk to 0
        for (ushort i = 0; i < chunkSize * chunkSize * chunkSize; i++)
        {
            chunk.blocks.Add(0);
        }

        chunk.selfEntity = chunkEntity;
        state.EntityManager.SetComponentData(chunkEntity, chunk);

        return chunkEntity;
    }

    [BurstCompile]
    private int GetChunkIndex(byte x, byte y, byte z, byte inChunkSize)
    {
        byte chunkSize = GenerationSettings.chunkSize;
        //if out of bounds throw exception
        if (x < 0 || x >= inChunkSize || y < 0 || y >= inChunkSize || z < 0 || z >= inChunkSize)
        {
            return -1;
        }
        return (int)(x + (y * chunkSize) + (z * chunkSize * chunkSize));
    }
}
