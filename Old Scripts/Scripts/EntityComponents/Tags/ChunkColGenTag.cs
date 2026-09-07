using Unity.Burst;
using Unity.Entities;

[BurstCompile]
public struct ChunkColGenTag : IComponentData
{
}

[BurstCompile]
public struct ChunkColNotGenTag : IComponentData
{
}

[BurstCompile]
public struct ChunkColQueuedTag : IComponentData
{
}
