using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct ChunkUnrenderedTag : IComponentData
{}

//authoring component
public class ChunkUnrenderedTagAuthoring : MonoBehaviour
{
    public class Baker : Baker<ChunkUnrenderedTagAuthoring>
    {
        public override void Bake(ChunkUnrenderedTagAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new ChunkUnrenderedTag());
        }
    }
}