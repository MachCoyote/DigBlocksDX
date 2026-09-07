using Unity.Burst;
using Unity.Entities;
using Unity.Physics;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Collections;
using System;


partial struct ChunksColliderSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {

    }

    EntityCommandBuffer.ParallelWriter ecb;

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        int colDistance = 16;
        Entity playerEntity;
        bool playerBool = SystemAPI.TryGetSingletonEntity<Player>(out playerEntity);
        if (playerBool)
        {
            PhysicsPrefabSingleton physPrefabSingleton;
            bool found = SystemAPI.TryGetSingleton<PhysicsPrefabSingleton>(out physPrefabSingleton);

            if (!found)
            {
                return;
            }

            Entity colPrefab = physPrefabSingleton.chunkCollider;

            //CONTINUE FROM HERE LATER

            ecb = GetEntityCommandBuffer(ref state);
            LocalTransform playerTransform = state.EntityManager.GetComponentData<LocalTransform>(playerEntity);
            float3 playerPos = playerTransform.Position;

            EntityCommandBuffer ecbt = new EntityCommandBuffer(Allocator.Temp);
            foreach ((RefRO<Chunk> chk, RefRO<ChunkColNotGenTag> CCNGT, RefRO<ChunkRenderedTag> CRT) in SystemAPI.Query<RefRO<Chunk>, RefRO<ChunkColNotGenTag>, RefRO<ChunkRenderedTag>>())
            {
                if (math.distance(playerPos, new float3(chk.ValueRO.posFromWOX, chk.ValueRO.posFromWOY, chk.ValueRO.posFromWOZ)) < colDistance)
                {
                    ecbt.AddComponent<ChunkColQueuedTag>(chk.ValueRO.selfEntity);
                }
            }
            ecbt.Playback(state.EntityManager);
        }
    }

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {

    }

    [BurstCompile]
    public partial struct AssignMeshJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter ecb;

        [BurstCompile]
        public void Execute(in Chunk chk, in ChunkMeshData CMD, in ChunkColNotGenTag CCNGT, in ChunkColQueuedTag CCQT, ref PhysicsCollider phys)
        {
            //remove the tag
            ecb.RemoveComponent<ChunkColNotGenTag>(1, chk.selfEntity);

            NativeArray<float3> vertices = new NativeArray<float3>(CMD.vertices.Length, Allocator.Temp);
            NativeArray<int3> triangles = new NativeArray<int3>((CMD.triangles.Length / 3), Allocator.Temp);

            for (int i = 0; i < CMD.vertices.Length; i++)
            {
                vertices[i] = CMD.vertices[i];
            }

            for (int i = 0; i < CMD.triangles.Length; i++)
            {
                int3 values = new int3();
                values.x = CMD.triangles[i];
                values.y = CMD.triangles[i + 1];
                values.z = CMD.triangles[i + 2];
                triangles[i] = values;
            }

            BlobAssetReference<Collider> col = MeshCollider.Create(vertices, triangles);
            phys.Value = col;
        }
    }

    [BurstCompile]
    private EntityCommandBuffer.ParallelWriter GetEntityCommandBuffer(ref SystemState state)
    {
        var ecbSingleton = SystemAPI.GetSingleton<BeginSimulationEntityCommandBufferSystem.Singleton>();
        var ecb = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged);
        return ecb.AsParallelWriter();
    }
}
