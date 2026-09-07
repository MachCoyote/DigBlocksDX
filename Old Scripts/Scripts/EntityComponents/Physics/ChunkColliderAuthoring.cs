using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public struct ChunkCollider : IComponentData
{
    public long posFromWOX;
    public long posFromWOY;
    public long posFromWOZ;

}
