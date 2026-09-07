using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities.UniversalDelegates;
using NUnit.Framework.Constraints;
using System.Linq;
using System.Collections.Generic;
using Unity.Rendering;
using UnityEngine.Rendering;
using JetBrains.Annotations;
using Unity.Jobs;


[CreateAfter(typeof(ChunkGenSystem))]
[UpdateAfter(typeof(ChunkGenSystem))]
public partial struct ChunkRenderSystem : ISystem
{
    [BurstCompile]
    private EntityCommandBuffer.ParallelWriter GetEntityCommandBuffer(ref SystemState state)
    {
        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);
        return ecb.AsParallelWriter();
    }

    [BurstCompile]
    private static int GetChunkDataIndex(int3 pos, int inChunkSize)
    {
        int chunkSize = GenerationSettings.chunkSize;
        //if out of bounds throw exception
        if (pos.x < 0 || pos.x >= inChunkSize || pos.y < 0 || pos.y >= inChunkSize || pos.z < 0 || pos.z >= inChunkSize)
        {
            return -1;
        }
        return pos.x + (pos.y * inChunkSize) + (pos.z * inChunkSize * inChunkSize);
    }

    public EntityCommandBuffer.ParallelWriter ecbP;

    EntityQuery unrenderedChunkQuery;
    EntityQuery chunkDataQuery;

    int renderPoolSize;
    byte chunkSize;

    UnsafeHashMap<int3, FaceData> faceDataMap;

    JobHandle renderHandle;

    UnsafeList<UnsafeList<ushort>> blockIdsCache;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheAbove;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheBelow;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheRight;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheLeft;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheForward;
    UnsafeList<UnsafeList<ushort>> blockIdsCacheBack;

    UnsafeList<bool> skipChunk;

    UnsafeList<UnsafeList<Vector3>> verticesCache;
    UnsafeList<UnsafeList<int>> indicesCache;
    UnsafeList<UnsafeList<Vector2>> uvsCache;

    bool isFirstRun;

    public void OnCreate(ref SystemState state)
    {
        isFirstRun = true;


        unrenderedChunkQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<ChunkUnrenderedTag>().WithAll<Chunk>().Build(ref state);
        chunkDataQuery = new EntityQueryBuilder(Allocator.Temp).WithAll<Chunk>().Build(ref state);

        renderPoolSize = GenerationSettings.cpuThreadCount;
        chunkSize = GenerationSettings.chunkSize;

        //initialize handle
        renderHandle = new JobHandle();


        //initialize caches
        blockIdsCache = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheAbove = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheBelow = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheRight = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheLeft = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheForward = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);
        blockIdsCacheBack = new UnsafeList<UnsafeList<ushort>>(renderPoolSize, Allocator.Persistent);

        skipChunk = new UnsafeList<bool>(renderPoolSize, Allocator.Persistent);

        verticesCache = new UnsafeList<UnsafeList<Vector3>>(renderPoolSize, Allocator.Persistent);
        indicesCache = new UnsafeList<UnsafeList<int>>(renderPoolSize, Allocator.Persistent);
        uvsCache = new UnsafeList<UnsafeList<Vector2>>(renderPoolSize, Allocator.Persistent);

        for (byte i = 0; i < renderPoolSize; i++)
        {
            blockIdsCache.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheAbove.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheBelow.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheRight.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheLeft.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheForward.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));
            blockIdsCacheBack.Add(new UnsafeList<ushort>(chunkSize * chunkSize * chunkSize, Allocator.Persistent));

            skipChunk.Add(false);

            verticesCache.Add(new UnsafeList<Vector3>(chunkSize * chunkSize * chunkSize * 24, Allocator.Persistent));
            indicesCache.Add(new UnsafeList<int>(chunkSize * chunkSize * chunkSize * 36, Allocator.Persistent));
            uvsCache.Add(new UnsafeList<Vector2>(chunkSize * chunkSize * chunkSize * 24, Allocator.Persistent));

            for (ushort j = 0; j < chunkSize * chunkSize * chunkSize; j++)
            {
                UnsafeList<ushort> blocks = blockIdsCache[i];
                blocks.Add(0);
                blockIdsCache[i] = blocks;

                //above
                UnsafeList<ushort> blocksAbove = blockIdsCacheAbove[i];
                blocksAbove.Add(0);
                blockIdsCacheAbove[i] = blocksAbove;

                //below
                UnsafeList<ushort> blocksBelow = blockIdsCacheBelow[i];
                blocksBelow.Add(0);
                blockIdsCacheBelow[i] = blocksBelow;

                //right
                UnsafeList<ushort> blocksRight = blockIdsCacheRight[i];
                blocksRight.Add(0);
                blockIdsCacheRight[i] = blocksRight;

                //left
                UnsafeList<ushort> blocksLeft = blockIdsCacheLeft[i];
                blocksLeft.Add(0);
                blockIdsCacheLeft[i] = blocksLeft;

                //forward
                UnsafeList<ushort> blocksForward = blockIdsCacheForward[i];
                blocksForward.Add(0);
                blockIdsCacheForward[i] = blocksForward;

                //back
                UnsafeList<ushort> blocksBack = blockIdsCacheBack[i];
                blocksBack.Add(0);
                blockIdsCacheBack[i] = blocksBack;
            }
        }

        InstantiateMeshHelpers(); //must be done
        DefineMeshHelpers(); //must be done

        //--------------fill faceDataMap
        faceDataMap = new UnsafeHashMap<int3, FaceData>(6, Allocator.Persistent);
        int3 up = new int3(0, 1, 0);
        int3 down = new int3(0, -1, 0);
        int3 forward = new int3(0, 0, 1);
        int3 back = new int3(0, 0, -1);
        int3 left = new int3(-1, 0, 0);
        int3 right = new int3(1, 0, 0);
        for (int i = 0; i < 6; i++)
        {
            if (CompareInt3(CheckDirections[i], up))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = UpFace, tris = UpTris, UVIndexOrder = YUVOrder });
            } else if (CompareInt3(CheckDirections[i], down))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = DownFace, tris = DownTris, UVIndexOrder = YUVOrder });
            }
            else if (CompareInt3(CheckDirections[i], forward))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = ForwardFace, tris = ForwardTris, UVIndexOrder = ZUVOrder });
            }
            else if (CompareInt3(CheckDirections[i], back))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = BackFace, tris = BackTris, UVIndexOrder = ZUVOrder });
            }
            else if (CompareInt3(CheckDirections[i], left))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = LeftFace, tris = LeftTris, UVIndexOrder = XUVOrder });
            }
            else if (CompareInt3(CheckDirections[i], right))
            {
                faceDataMap.Add(CheckDirections[i], new FaceData { verts = RightFace, tris = RightTris, UVIndexOrder = XUVOrder });
            }
        }
    }

    public void OnUpdate(ref SystemState state)
    {
        if (renderHandle.IsCompleted)
        {
            renderHandle.Complete();
            if (!isFirstRun)
            {
                //register chunk meshes first, then complete the jobhandle and reset and run it
                //Debug.Log("Registering chunk meshes");
                RegisterChunkMesh(ref state, renderHandle);
            }
            else
            {
                isFirstRun = false;
            }
            //queue up more chunks to be rendered
            QueueChunks(ref state, renderHandle);
        }
    }

    public void OnDestroy()
    {
        //vars and stuff
    }

    [BurstCompile]
    public void QueueChunks(ref SystemState state, JobHandle handle)
    {
        NativeArray<Entity> unrenderedChunkEntities = unrenderedChunkQuery.ToEntityArray(Allocator.Temp);
        NativeArray<Chunk> unrenderedChunkData = unrenderedChunkQuery.ToComponentDataArray<Chunk>(Allocator.Temp);

        //NativeArray<Entity> chunkDataEntities = chunkDataQuery.ToEntityArray(Allocator.TempJob);
        NativeArray<Chunk> chunkData = chunkDataQuery.ToComponentDataArray<Chunk>(Allocator.TempJob);

        int amountToRender = renderPoolSize;
        if (unrenderedChunkEntities.Length < renderPoolSize)
        {
            amountToRender = unrenderedChunkEntities.Length;
        }

        if (unrenderedChunkEntities.Length != 0)
        {
            UnsafeList<long> posXs = new UnsafeList<long>(amountToRender, Allocator.TempJob);
            UnsafeList<long> posYs = new UnsafeList<long>(amountToRender, Allocator.TempJob);
            UnsafeList<long> posZs = new UnsafeList<long>(amountToRender, Allocator.TempJob);

            UnsafeList<int> entityIds = new UnsafeList<int>(amountToRender, Allocator.TempJob);

            for (int i = 0; i < amountToRender; i++)
            {
                posXs.Add(unrenderedChunkData[i].posFromWOX);
                posYs.Add(unrenderedChunkData[i].posFromWOY);
                posZs.Add(unrenderedChunkData[i].posFromWOZ);

                entityIds.Add(unrenderedChunkEntities[i].Index);

                ChunkQueuedForRenderTag cqfrt = new ChunkQueuedForRenderTag { chunkIndexInRender = i };
                state.EntityManager.AddComponentData(unrenderedChunkEntities[i], cqfrt);

                verticesCache[i].Clear();
                indicesCache[i].Clear();
                uvsCache[i].Clear();

                skipChunk[i] = false;
            }

            PopulateNeighborsJob pnj = new PopulateNeighborsJob
            {
                chunkData = chunkData,
                posXs = posXs,
                posYs = posYs,
                posZs = posZs,
                entityIds = entityIds,
                chunkSize = chunkSize,
                blockIds = blockIdsCache,
                blockIdsAbove = blockIdsCacheAbove,
                blockIdsBelow = blockIdsCacheBelow,
                blockIdsRight = blockIdsCacheRight,
                blockIdsLeft = blockIdsCacheLeft,
                blockIdsForward = blockIdsCacheForward,
                blockIdsBack = blockIdsCacheBack,
                skipChunk = skipChunk
            };

            //36 15
            //Debug.Log($"Length1: {blockIdsCache[36].Length}");
            //Debug.Log($"Length2: {blockIdsCacheAbove[36].Length}");
            //Debug.Log($"Length3: {blockIdsCacheBelow[36].Length}");
            //Debug.Log($"Length4: {blockIdsCacheRight[36].Length}");
            //Debug.Log($"Length5: {blockIdsCacheLeft[36].Length}");
            //Debug.Log($"Length6: {blockIdsCacheForward[36].Length}");
            //Debug.Log($"Length7: {blockIdsCacheBack[36].Length}");


            //run and complete
            pnj.Schedule(amountToRender, 1).Complete();

            //dispose
            posXs.Dispose();
            posYs.Dispose();
            posZs.Dispose();
            //chunkData.Dispose();



            PopulateChunkDataJob rcj = new PopulateChunkDataJob
            {
                blockIds = blockIdsCache,
                blockIdsAbove = blockIdsCacheAbove,
                blockIdsBelow = blockIdsCacheBelow,
                blockIdsRight = blockIdsCacheRight,
                blockIdsLeft = blockIdsCacheLeft,
                blockIdsForward = blockIdsCacheForward,
                blockIdsBack = blockIdsCacheBack,

                entityIds = entityIds,

                playerPos = new float3(0, 0, 0),
                chunkSize = chunkSize,
                CheckDirections = CheckDirections,
                RightFace = RightFace,
                RightTris = RightTris,
                LeftFace = LeftFace,
                LeftTris = LeftTris,
                UpFace = UpFace,
                UpTris = UpTris,
                DownFace = DownFace,
                DownTris = DownTris,
                ForwardFace = ForwardFace,
                ForwardTris = ForwardTris,
                BackFace = BackFace,
                BackTris = BackTris,
                faceDataMap = faceDataMap,

                vertices = verticesCache,
                indices = indicesCache,
                uvs = uvsCache,

                skipChunk = skipChunk,

                textureSizeX = 128,
                textureSizeY = 48
            };

            //Debug.Log($"Chunks to render {chunkToRenderCount}");

            renderHandle = rcj.Schedule(amountToRender, 1);
        }
    }

    public void RegisterChunkMesh(ref SystemState state, JobHandle handle)
    {
        EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
        foreach ((RefRO<Chunk> chk, RefRO<ChunkUnrenderedTag> cut, RefRO<ChunkQueuedForRenderTag> cqfrt, RefRO<ChunkMeshData> CMD, RefRW<MaterialMeshInfo> mmi) in SystemAPI.Query<RefRO<Chunk>, RefRO<ChunkUnrenderedTag>, RefRO<ChunkQueuedForRenderTag>, RefRO<ChunkMeshData>, RefRW <MaterialMeshInfo>>())
        {
            ecb.RemoveComponent<ChunkUnrenderedTag>(chk.ValueRO.selfEntity);
            ecb.RemoveComponent<ChunkQueuedForRenderTag>(chk.ValueRO.selfEntity);

            if (!skipChunk[cqfrt.ValueRO.chunkIndexInRender])
            {
                Mesh mesh = new Mesh();

                mesh.vertices = Vector3UnsafeListToArray(verticesCache[cqfrt.ValueRO.chunkIndexInRender]);
                mesh.triangles = IntUnsafeListToArray(indicesCache[cqfrt.ValueRO.chunkIndexInRender]);
                mesh.uv = Vector2UnsafeListToArray(uvsCache[cqfrt.ValueRO.chunkIndexInRender]);

                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();

                EntitiesGraphicsSystem egs = state.World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
                BatchMeshID meshIndex = egs.RegisterMesh(mesh);

                mmi.ValueRW.MeshID = meshIndex;
                mmi.ValueRW.MaterialID = CMD.ValueRO.materialID;

                //render bounds
                Bounds bounds = mesh.bounds;
                ecb.SetComponent(chk.ValueRO.selfEntity, new RenderBounds { Value = new() { Center = bounds.center, Extents = bounds.extents } });
            }

            ecb.AddComponent<ChunkRenderedTag>(chk.ValueRO.selfEntity);
        }

        ecb.Playback(state.EntityManager);

        //Debug.Log($"Chunks rendered: {chunksRendered}");
        //Debug.Log($"Faces rendered: {facesRendered}");
        //Debug.Log($"Vertices rendered: {verticesRendered}");
    }

    //vector3
    [BurstCompile]
    public Vector3[] Vector3UnsafeListToArray(UnsafeList<Vector3> list)
    {
        Vector3[] array = new Vector3[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            array[i] = list[i];
        }
        return array;
    }

    //int
    [BurstCompile]
    public int[] IntUnsafeListToArray(UnsafeList<int> list)
    {
        int[] array = new int[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            array[i] = list[i];
        }
        return array;
    }

    //vector2
    [BurstCompile]
    public Vector2[] Vector2UnsafeListToArray(UnsafeList<Vector2> list)
    {
        Vector2[] array = new Vector2[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            array[i] = list[i];
        }
        return array;
    }

    [BurstCompile]
    public partial struct PopulateNeighborsJob : IJobParallelFor
    {
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<Chunk> chunkData;

        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<long> posXs;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<long> posYs;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<long> posZs;

        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> entityIds;


        [ReadOnly]
        public byte chunkSize;

        public UnsafeList<UnsafeList<ushort>> blockIds;
        public UnsafeList<UnsafeList<ushort>> blockIdsAbove;
        public UnsafeList<UnsafeList<ushort>> blockIdsBelow;
        public UnsafeList<UnsafeList<ushort>> blockIdsRight;
        public UnsafeList<UnsafeList<ushort>> blockIdsLeft;
        public UnsafeList<UnsafeList<ushort>> blockIdsForward;
        public UnsafeList<UnsafeList<ushort>> blockIdsBack;

        public UnsafeList<bool> skipChunk;

        [BurstCompile]
        public void Execute(int index)
        {
            byte ind = (byte)index;

            bool aboveBlocked = false;
            bool belowBlocked = false;
            bool rightBlocked = false;
            bool leftBlocked = false;
            bool forwardBlocked = false;
            bool backBlocked = false;

            foreach (Chunk chk in chunkData)
            {
                //above
                if (chk.posFromWOX == posXs[ind] && chk.posFromWOY == posYs[ind] + chunkSize && chk.posFromWOZ == posZs[ind])
                {
                    UnsafeList<ushort> blocks = blockIdsAbove[ind];
                    blocks = chk.blocks;
                    blockIdsAbove[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)(i + (0) + (j * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    aboveBlocked = block;
                }

                //below
                if (chk.posFromWOX == posXs[ind] && chk.posFromWOY == posYs[ind] - chunkSize && chk.posFromWOZ == posZs[ind])
                {
                    UnsafeList<ushort> blocks = blockIdsBelow[ind];
                    blocks = chk.blocks;
                    blockIdsBelow[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)(i + (chunkSize - 1) + (j * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    belowBlocked = block;
                }

                //right
                if (chk.posFromWOX == posXs[ind] + chunkSize && chk.posFromWOY == posYs[ind] && chk.posFromWOZ == posZs[ind])
                {
                    UnsafeList<ushort> blocks = blockIdsRight[ind];
                    blocks = chk.blocks;
                    blockIdsRight[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)(0 + (i * chunkSize) + (j * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    rightBlocked = block;
                }

                //left
                if (chk.posFromWOX == posXs[ind] - chunkSize && chk.posFromWOY == posYs[ind] && chk.posFromWOZ == posZs[ind])
                {
                    UnsafeList<ushort> blocks = blockIdsLeft[ind];
                    blocks = chk.blocks;
                    blockIdsLeft[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)((chunkSize - 1) + (i * chunkSize) + (j * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    leftBlocked = block;
                }

                //forward
                if (chk.posFromWOX == posXs[ind] && chk.posFromWOY == posYs[ind] && chk.posFromWOZ == posZs[ind] + chunkSize)
                {
                    UnsafeList<ushort> blocks = blockIdsForward[ind];
                    blocks = chk.blocks;
                    blockIdsForward[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)(i + (j * chunkSize) + (0 * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    forwardBlocked = block;
                }

                //back
                if (chk.posFromWOX == posXs[ind] && chk.posFromWOY == posYs[ind] && chk.posFromWOZ == posZs[ind] - chunkSize)
                {
                    UnsafeList<ushort> blocks = blockIdsBack[ind];
                    blocks = chk.blocks;
                    blockIdsBack[ind] = blocks;

                    bool block = true;
                    for (byte i = 0; i < chunkSize; i++)
                    {
                        for (byte j = 0; j < chunkSize; j++)
                        {
                            ushort indi = (ushort)(i + (j * chunkSize) + ((chunkSize - 1) * chunkSize * chunkSize));
                            if (chk.blocks[indi] == 0)
                            {
                                block = false;
                                i = chunkSize; //break out of loop
                                break;
                            }
                        }
                    }

                    backBlocked = block;
                }

                if (aboveBlocked && belowBlocked && rightBlocked && leftBlocked && forwardBlocked && backBlocked)
                {
                    skipChunk[ind] = true;
                    //Debug.Log("Skipped chunk");
                }

                if  (!skipChunk[ind])
                {
                    //current
                    if (chk.posFromWOX == posXs[ind] && chk.posFromWOY == posYs[ind] && chk.posFromWOZ == posZs[ind])
                    {
                        UnsafeList<ushort> blocks = blockIds[ind];
                        blocks = chk.blocks;
                        blockIds[ind] = blocks;
                    }
                }
            }
        }
    }

    [BurstCompile]
    public partial struct PopulateChunkDataJob : IJobParallelFor
    {
        [ReadOnly]
        public float3 playerPos;
        [ReadOnly]
        public byte chunkSize;
        [ReadOnly]
        public int textureSizeX;
        [ReadOnly]
        public int textureSizeY;

        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeHashMap<int, int3> CheckDirections;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> RightFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> RightTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> LeftFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> LeftTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> UpFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> UpTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> DownFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> DownTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> ForwardFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> ForwardTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<Vector3> BackFace;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> BackTris;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeHashMap<int3, FaceData> faceDataMap;

        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIds;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsAbove;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsBelow;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsRight;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsLeft;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsForward;
        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<UnsafeList<ushort>> blockIdsBack;

        [ReadOnly]
        [NativeDisableParallelForRestriction]
        public UnsafeList<int> entityIds;

        public UnsafeList<UnsafeList<Vector3>> vertices;
        public UnsafeList<UnsafeList<int>> indices;
        public UnsafeList<UnsafeList<Vector2>> uvs;

        public UnsafeList<bool> skipChunk;

        [BurstCompile]
        public void Execute(int index)
        {
            //debug print to determinewhat is inside of blockIds
            //foreach (KVPair<int, int> pair in blockIds[index])
            //{
            //    Debug.Log($"Block id: {pair.Key} Value: {pair.Value} Index: {index}");
            //}

            if (skipChunk[index])
            {
                //Debug.Log("Skipped chunk");
                return;
            }


            int faceCount = 0;

            byte ind = (byte)index;

            //Debug.Log("Ran renderer");
            //remove cut and cqfrt
            int texBlockWidth = textureSizeX / 16;
            int texBlockHeight = textureSizeY / 16;

            UnsafeList<Vector3> verticesList = new UnsafeList<Vector3>(chunkSize * chunkSize * chunkSize * 24, Allocator.Persistent);
            int verticeCount = 0;
            UnsafeList<int> indicesList = new UnsafeList<int>(chunkSize * chunkSize * chunkSize * 36, Allocator.Persistent);
            int indiceCount = 0;
            UnsafeList<Vector2> uvsList = new UnsafeList<Vector2>(chunkSize * chunkSize * chunkSize * 24, Allocator.Persistent);
            int uvCount = 0;

            //for xyz chunksize
            for (byte x = 0; x < chunkSize; x++)
            {
                for (byte y = 0; y < chunkSize; y++)
                {
                    for (byte z = 0; z < chunkSize; z++)
                    {
                        int3 currentPos = new int3(x, y, z);
                        //for each check direction
                        for (int i = 0; i < 6; i++) //hardcoded direction count, order is right, left, up, down, forward, back
                        {
                            int3 blockToCheck = currentPos + CheckDirections[i];
                            int flattenedCheckIndex = GetChunkDataIndex(blockToCheck, chunkSize);
                            int flattenedCurrentIndex = GetChunkDataIndex(currentPos, chunkSize);
                            if (flattenedCheckIndex != -1)
                            {
                                if (blockIds[ind][(ushort)flattenedCheckIndex] == 0)
                                {
                                    if (blockIds[ind][(ushort)flattenedCurrentIndex] != 0)
                                    {
                                        FaceData faceToApply = faceDataMap[CheckDirections[i]];

                                        byte iter = 0;
                                        foreach (var vert in faceToApply.verts)
                                        {
                                            verticesList.Add(vert + new Vector3(x, y, z));
                                            verticeCount++;
                                            uvsList.Add(GetBlockUV(i /*direction*/, iter, blockIds[ind][(ushort)flattenedCurrentIndex], new int2(texBlockWidth, texBlockHeight)));
                                            uvCount++;
                                            iter++;
                                        }

                                        foreach (var tri in faceToApply.tris)
                                        {
                                            indicesList.Add(verticesList.Length - 4 + tri);
                                            indiceCount++;
                                        }

                                        faceCount++;
                                    }
                                }
                            }
                            else
                            {
                                //if out of bounds, we need to check the adjacent chunk
                                //we check the block in the adjacent chunk as normal and process face data as normal
                                int dir = -1; //0 is above, 1 is below, 2 is right, 3 is left, 4 is forward, 5 is back
                                if (CheckDirections[i].x == 1)
                                {
                                    dir = 2;
                                }
                                else if (CheckDirections[i].x == -1)
                                {
                                    dir = 3;
                                }
                                else if (CheckDirections[i].y == 1)
                                {
                                    dir = 0;
                                }
                                else if (CheckDirections[i].y == -1)
                                {
                                    dir = 1;
                                }
                                else if (CheckDirections[i].z == 1)
                                {
                                    dir = 4;
                                }
                                else if (CheckDirections[i].z == -1)
                                {
                                    dir = 5;
                                }

                                int blockId = -1;

                                UnsafeList<ushort> blocksAbove = blockIdsAbove[ind];
                                UnsafeList<ushort> blocksBelow = blockIdsBelow[ind];
                                UnsafeList<ushort> blocksRight = blockIdsRight[ind];
                                UnsafeList<ushort> blocksLeft = blockIdsLeft[ind];
                                UnsafeList<ushort> blocksForward = blockIdsForward[ind];
                                UnsafeList<ushort> blocksBack = blockIdsBack[ind];
                                UnsafeList<ushort> blocks = blockIds[ind];

                                switch (dir)
                                {
                                    case 0: //above
                                        int3 newCheckCoord = new int3();
                                        newCheckCoord.x = x;
                                        newCheckCoord.y = 0;
                                        newCheckCoord.z = z;
                                        blockId = blocksAbove[(ushort)GetChunkDataIndex(newCheckCoord, chunkSize)];
                                        break;
                                    case 1: //below
                                        int3 newCheckCoord1 = new int3();
                                        newCheckCoord1.x = x;
                                        newCheckCoord1.y = chunkSize - 1;
                                        newCheckCoord1.z = z;
                                        blockId = blocksBelow[(ushort)GetChunkDataIndex(newCheckCoord1, chunkSize)];
                                        break;
                                    case 2: //right
                                        int3 newCheckCoord2 = new int3();
                                        newCheckCoord2.x = 0;
                                        newCheckCoord2.y = y;
                                        newCheckCoord2.z = z;
                                        blockId = blocksRight[(ushort)GetChunkDataIndex(newCheckCoord2, chunkSize)];
                                        break;
                                    case 3: //left
                                        int3 newCheckCoord3 = new int3();
                                        newCheckCoord3.x = chunkSize - 1;
                                        newCheckCoord3.y = y;
                                        newCheckCoord3.z = z;
                                        blockId = blocksLeft[(ushort)GetChunkDataIndex(newCheckCoord3, chunkSize)];
                                        break;
                                    case 4: //forward
                                        int3 newCheckCoord4 = new int3();
                                        newCheckCoord4.x = x;
                                        newCheckCoord4.y = y;
                                        newCheckCoord4.z = 0;
                                        blockId = blocksForward[(ushort)GetChunkDataIndex(newCheckCoord4, chunkSize)];
                                        break;
                                    case 5: //back
                                        int3 newCheckCoord5 = new int3();
                                        newCheckCoord5.x = x;
                                        newCheckCoord5.y = y;
                                        newCheckCoord5.z = chunkSize - 1;
                                        blockId = blocksBack[(ushort)GetChunkDataIndex(newCheckCoord5, chunkSize)];
                                        break;
                                    case -1: //error
                                        Debug.LogError("Error in chunk render system, dir is -1");
                                        break;

                                }

                                if (blocks[(ushort)flattenedCurrentIndex] != 0)
                                {
                                    if (blockId == 0)
                                    {
                                        FaceData faceToApply = faceDataMap[CheckDirections[i]];

                                        int iter = 0;
                                        foreach (var vert in faceToApply.verts)
                                        {
                                            verticesList.Add(vert + new Vector3(x, y, z));
                                            verticeCount++;
                                            uvsList.Add(GetBlockUV(i /*direction*/, iter, blockIds[ind][(ushort)flattenedCurrentIndex], new int2(texBlockWidth, texBlockHeight)));
                                            uvCount++;
                                            iter++;
                                        }

                                        foreach (var tri in faceToApply.tris)
                                        {
                                            indicesList.Add(verticesList.Length - 4 + tri);
                                            indiceCount++;
                                        }

                                        faceCount++;
                                    }
                                    
                                }

                            }
                        }
                    }
                }
            }

            UnsafeList<Vector3> verticesCacheList = vertices[index];
            UnsafeList<int> indicesCacheList = indices[index];
            UnsafeList<Vector2> uvsCacheList = uvs[index];

            verticesCacheList.Clear();
            indicesCacheList.Clear();
            uvsCacheList.Clear();

            for (int i = 0; i < verticesList.Length; i++)
            {
                verticesCacheList.Add(verticesList[i]);
            }

            for (int i = 0; i < indicesList.Length; i++)
            {
                indicesCacheList.Add(indicesList[i]);
            }

            for (int i = 0; i < uvsList.Length; i++)
            {
                uvsCacheList.Add(uvsList[i]);
            }

            vertices[index] = verticesCacheList;
            indices[index] = indicesCacheList;
            uvs[index] = uvsCacheList;

            verticesList.Dispose();
            indicesList.Dispose();
            uvsList.Dispose();

            //Debug.Log($"Vertices: {verticeCount} Indices: {indiceCount}");
        }
    }

    public static Vector3[] Float3ArrayToVector3Array(float3[] float3Array)
    {
        Vector3[] vector3Array = new Vector3[float3Array.Length];
        for (int i = 0; i < float3Array.Length; i++)
        {
            vector3Array[i] = new Vector3(float3Array[i].x, float3Array[i].y, float3Array[i].z);
        }
        return vector3Array;
    }

    [BurstCompile]
    public bool CompareInt3(int3 a, int3 b)
    {
        if (a.x == b.x && a.y == b.y && a.z == b.z)
        {
            return true;
        }
        return false;
    }

    [BurstCompile]
    public void InstantiateMeshHelpers()
    {
        //face data
        CheckDirections = new UnsafeHashMap<int,int3>(6, Allocator.Persistent);
        /*RightFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        RightTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);
        LeftFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        LeftTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);
        UpFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        UpTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);
        DownFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        DownTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);
        ForwardFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        ForwardTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);
        BackFace = new UnsafeHashMap<int, float3>(4, Allocator.Persistent);
        BackTris = new UnsafeHashMap<int, int>(6, Allocator.Persistent);

        //uv orders
        XUVOrder = new UnsafeHashMap<int, int>(4, Allocator.Persistent);
        YUVOrder = new UnsafeHashMap<int, int>(4, Allocator.Persistent);
        ZUVOrder = new UnsafeHashMap<int, int>(4, Allocator.Persistent);*/
    }

    [BurstCompile]
    public void DefineMeshHelpers()
    {
        //this is janky, but predefining native arrays isnt possible, so we have to do it here manually

        //face data
        //check directions
        for (int i = 0; i < CheckDirections.Capacity; i++)
        {
            switch (i)
            {
                case 0:
                    CheckDirections[i] = new int3(1, 0, 0); //right
                    break;
                case 1:
                    CheckDirections[i] = new int3(-1, 0, 0); //left
                    break;
                case 2:
                    CheckDirections[i] = new int3(0, 1, 0); //up
                    break;
                case 3:
                    CheckDirections[i] = new int3(0, -1, 0); //down
                    break;
                case 4:
                    CheckDirections[i] = new int3(0, 0, 1); //forward
                    break;
                case 5:
                    CheckDirections[i] = new int3(0, 0, -1); //back
                    break;
            }
        }

        // Right face vertices
        RightFace = new UnsafeList<Vector3>(4, Allocator.Persistent) 
        {
            new float3(.5f, -.5f, -.5f),
            new float3(.5f, -.5f, .5f),
            new float3(.5f, .5f, .5f),
            new float3(.5f, .5f, -.5f)
        };

        // Right face triangles
        RightTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 2, 1, 0, 3, 2 };


        // Left face vertices
        LeftFace = new UnsafeList<Vector3>(4, Allocator.Persistent)
        {
            new float3(-.5f, -.5f, -.5f),
            new float3(-.5f, -.5f, .5f),
            new float3(-.5f, .5f, .5f),
            new float3(-.5f, .5f, -.5f)
        };

        // Left face triangles
        LeftTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 1, 2, 0, 2, 3 };


        // Up face vertices
        UpFace = new UnsafeList<Vector3>(4, Allocator.Persistent)
        {
            new float3(-.5f, .5f, -.5f),
            new float3(-.5f, .5f, .5f),
            new float3(.5f, .5f, .5f),
            new float3(.5f, .5f, -.5f)
        };

        // Up face triangles
        UpTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 1, 2, 0, 2, 3 };


        // Down face vertices
        DownFace = new UnsafeList<Vector3>(4, Allocator.Persistent)
        {
            new float3(-.5f, -.5f, -.5f),
            new float3(-.5f, -.5f, .5f),
            new float3(.5f, -.5f, .5f),
            new float3(.5f, -.5f, -.5f)
        };

        // Down face triangles
        DownTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 2, 1, 0, 3, 2 };


        // Forward face vertices
        ForwardFace = new UnsafeList<Vector3>(4, Allocator.Persistent)
        {
            new float3(-.5f, -.5f, .5f),
            new float3(-.5f, .5f, .5f),
            new float3(.5f, .5f, .5f),
            new float3(.5f, -.5f, .5f)
        };

        // Forward face triangles
        ForwardTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 2, 1, 0, 3, 2 };


        // Back face vertices
        BackFace = new UnsafeList<Vector3>(4, Allocator.Persistent)
        {
            new float3(-.5f, -.5f, -.5f),
            new float3(-.5f, .5f, -.5f),
            new float3(.5f, .5f, -.5f),
            new float3(.5f, -.5f, -.5f)
        };

        // Back face triangles
        BackTris = new UnsafeList<int>(6, Allocator.Persistent) { 0, 1, 2, 0, 2, 3 };


        // XUVOrder
        XUVOrder = new UnsafeList<int>(4, Allocator.Persistent)
        {
            2, 3, 1, 0
        };

        // YUVOrder
        YUVOrder = new UnsafeList<int>(4, Allocator.Persistent)
        {
            0, 1, 3, 2
        };

        // ZUVOrder
        ZUVOrder = new UnsafeList<int>(4, Allocator.Persistent)
        {
            3, 1, 0, 2
        };
    }

    [BurstCompile]
    public struct FaceData
    {
        public UnsafeList<Vector3> verts;
        public UnsafeList<int> tris;
        public UnsafeList<int> UVIndexOrder;

    }

    //face data
    public UnsafeHashMap<int,int3> CheckDirections;
    public UnsafeList<Vector3> RightFace;
    public UnsafeList<int> RightTris;
    public UnsafeList<Vector3> LeftFace;
    public UnsafeList<int> LeftTris;
    public UnsafeList<Vector3> UpFace;
    public UnsafeList<int> UpTris;
    public UnsafeList<Vector3> DownFace;
    public UnsafeList<int> DownTris;
    public UnsafeList<Vector3> ForwardFace;
    public UnsafeList<int> ForwardTris;
    public UnsafeList<Vector3> BackFace;
    public UnsafeList<int> BackTris;

    //uv orders
    public UnsafeList<int> XUVOrder;
    public UnsafeList<int> YUVOrder;
    public UnsafeList<int> ZUVOrder;

    [BurstCompile]
    public static bool AreInt3sEqual(int3 a, int3 b)
    {
        if (a.x == b.x && a.y == b.y && a.z == b.z)
        {
            return true;
        }
        return false;
    }

    [BurstCompile]
    public static float2 GetUVC(int2 size, int2 position, int uvNum, int dir)
    {
        float2 sizeF = new float2(size.x, size.y);
        float2 positionF = new float2(position.x, position.y);

        switch (dir) //order is right, left, up, down, forward, back
        {
            case 0: //right
                switch (uvNum)
                {
                    case 0:

                        positionF.y += 1;
                        break;
                    case 1:

                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                    case 2:
                        positionF.x += 1;
                        break;
                    case 3:
                        break;
                }
                break;
            case 1: //left
                switch (uvNum)
                {
                    case 0:

                        positionF.y += 1;
                        break;
                    case 1:

                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                    case 2:
                        positionF.x += 1;
                        break;
                    case 3:
                        break;
                }
                break;
            case 2: //up
                switch (uvNum)
                {
                    case 0:
                        positionF.y += 1;
                        break;
                    case 1:
                        break;
                    case 2:
                        positionF.x += 1;
                        break;
                    case 3:
                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                }
                break;
            case 3: //down
                switch (uvNum)
                {
                    case 0:
                        break;
                    case 1:
                        positionF.y += 1;
                        break;
                    case 2:
                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                    case 3:
                        positionF.x += 1;
                        break;
                }
                break;
            case 4: //forward
                switch (uvNum)
                {
                    case 0:
                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                    case 1:
                        positionF.x += 1;
                        break;
                    case 2:
                        break;
                    case 3:
                        positionF.y += 1;
                        break;
                }
                break;
            case 5: //back
                switch (uvNum)
                {
                    case 0:
                        positionF.x += 1;
                        positionF.y += 1;
                        break;
                    case 1:
                        positionF.x += 1;
                        break;
                    case 2:
                        break;
                    case 3:
                        positionF.y += 1;
                        break;
                }
                break;
        }

        

        float uvcx = (1f / sizeF.x) * positionF.x;
        float uvcy = 1f - ((1f / sizeF.y) * positionF.y);
        return new float2(uvcx, uvcy);
    }

    [BurstCompile]
    public static float2 GetBlockUV(int dir, int uvNum, int blockID, int2 size)
    {
        switch (blockID)
        {
            default:
                return GetUVC(size, new int2(0, 2), uvNum, dir); //error
            case 0: //air
                //do something
                switch (dir) //order is right, left, up, down, forward, back
                {
                    case 0: //right
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 1: //left
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 2: //up
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 3: //down
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 4: //forward
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 5: //back
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    default:
                        return GetUVC(size, new int2(0, 2), uvNum, dir); //error
                }
                break;
            case 1: //grass
                //do something
                switch (dir) //order is right, left, up, down, forward, back
                {
                    case 0: //right
                        return GetUVC(size, new int2(1, 0), uvNum, dir);
                    case 1: //left
                        return GetUVC(size, new int2(1, 0), uvNum, dir);
                    case 2: //up
                        return GetUVC(size, new int2(0, 0), uvNum, dir);
                    case 3: //down
                        return GetUVC(size, new int2(3, 0), uvNum, dir);
                    case 4: //forward
                        return GetUVC(size, new int2(1, 0), uvNum, dir);
                    case 5: //back
                        return GetUVC(size, new int2(1, 0), uvNum, dir);
                    default:
                        return GetUVC(size, new int2(0, 2), uvNum, dir); //error
                }
                break;
            case 2: //dirt
                //do something
                switch (dir) //order is right, left, up, down, forward, back
                {
                    case 0: //right
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    case 1: //left
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    case 2: //up
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    case 3: //down
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    case 4: //forward
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    case 5: //back
                        return GetUVC(size, new int2(2, 0), uvNum, dir);
                    default:
                        return GetUVC(size, new int2(0, 2), uvNum, dir); //error
                }
                break;
            case 3: //stone
                //do something
                switch (dir) //order is right, left, up, down, forward, back
                {
                    case 0: //right
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    case 1: //left
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    case 2: //up
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    case 3: //down
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    case 4: //forward
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    case 5: //back
                        return GetUVC(size, new int2(0, 1), uvNum, dir);
                    default:
                        return GetUVC(size, new int2(0, 2), uvNum, dir); //error
                }
                break;
            case 4: //bedrock
                //do something
                switch (dir) //order is right, left, up, down, forward, back
                {
                    case 0: //right
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    case 1: //left
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    case 2: //up
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    case 3: //down
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    case 4: //forward
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    case 5: //back
                        return GetUVC(size, new int2(1, 1), uvNum, dir);
                    default:
                        return GetUVC(size, new int2(0, 2), uvNum, dir); //error
                }
                break;
        }
    }
}