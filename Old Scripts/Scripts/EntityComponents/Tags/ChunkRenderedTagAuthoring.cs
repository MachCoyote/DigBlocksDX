using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct ChunkRenderedTag : IComponentData
{}

//authoring component
public class ChunkRenderedTagAuthoring : MonoBehaviour
{
    public class Baker : Baker<ChunkRenderedTagAuthoring>
    {
        public override void Bake(ChunkRenderedTagAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new ChunkRenderedTag());
        }
    }
}