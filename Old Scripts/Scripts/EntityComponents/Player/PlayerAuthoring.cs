using Unity.Burst;
using Unity.Entities;
using UnityEngine;

[BurstCompile]
public struct Player : IComponentData
{
    
}

//baker
public class PlayerAuthoring : MonoBehaviour
{
    public class Baker : Baker<PlayerAuthoring>
    {
        public override void Bake(PlayerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new Player());
        }
    }
}