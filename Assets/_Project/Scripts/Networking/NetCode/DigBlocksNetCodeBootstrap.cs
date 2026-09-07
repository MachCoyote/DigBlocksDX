using Unity.NetCode;
using UnityEngine;
using UnityEngine.Scripting;

namespace DigBlocks.Networking.NetCode
{
    [Preserve]
    public sealed class DigBlocksNetCodeBootstrap : ClientServerBootstrap
    {
        public override bool Initialize(string defaultWorldName)
        {
            CreateLocalWorld(defaultWorldName);
            return true;
        }
    }
}
