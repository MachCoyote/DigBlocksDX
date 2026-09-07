using Unity.Burst;
using UnityEngine;

[BurstCompile]
public struct GenerationSettings
{
    public const byte chunkSize = 16;
    public const int cpuThreadCount = 64;

    public const long tempWorldOriginOffsetX = 0;
    public const long tempWorldOriginOffsetY = 0;
    public const long tempWorldOriginOffsetZ = 0;
}
