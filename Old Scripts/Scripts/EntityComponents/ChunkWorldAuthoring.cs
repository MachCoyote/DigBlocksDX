using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct ChunkWorld : IComponentData
{
    public Entity selfEntity;
    public Entity facePrefab;
    public Entity chunkPrefab;
    public Entity blockPrefab;
}

public class ChunkWorldAuthoring : MonoBehaviour
{
    public GameObject chunkPrefab;

    private class Baker : Baker<ChunkWorldAuthoring>
    {
        public override void Bake(ChunkWorldAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ChunkWorld
            {
                chunkPrefab = GetEntity(authoring.chunkPrefab, TransformUsageFlags.Dynamic),
                selfEntity = entity
            });
        }
    }
}