using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct PhysicsPrefabSingleton : IComponentData
{
    public Entity chunkCollider;
}

//authoring component
public class PhysicsPrefabSingletonAuthoring : MonoBehaviour
{
    public GameObject chunkCollider;

    public class Baker : Baker<PhysicsPrefabSingletonAuthoring>
    {
        public override void Bake(PhysicsPrefabSingletonAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new PhysicsPrefabSingleton
            {
                chunkCollider = GetEntity(authoring.chunkCollider, TransformUsageFlags.Dynamic),
            });
        }
    }
}
