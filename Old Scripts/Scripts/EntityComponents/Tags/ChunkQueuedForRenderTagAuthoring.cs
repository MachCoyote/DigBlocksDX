using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct ChunkQueuedForRenderTag : IComponentData
{ public int chunkIndexInRender; }

//authoring component
public class ChunkQueuedForRenderTagAuthoring : MonoBehaviour
{

    public class Baker : Baker<ChunkQueuedForRenderTagAuthoring>
    {
        public override void Bake(ChunkQueuedForRenderTagAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new ChunkQueuedForRenderTag
            {
            });
        }
    }
}