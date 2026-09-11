using DigBlocks.Voxels.Runtime;
using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Raises <see cref="WorldDataReady"/> the first time the client's chunk replicas match the
    /// interest the server declared, which is the point at which there is a world to stand in.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class WorldDataReadySystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<SessionActive>();

        protected override void OnUpdate()
        {
            //a world with no chunk store streams nothing, so it never becomes ready and never enters
            //the game. That is the honest answer for a session that owns no world data.
            var store = World.GetExistingSystemManaged<ChunkWorldSystem>()?.Store;
            if (store is not { DataReady: true }) return;

            EntityManager.CreateSingleton<WorldDataReady>();
            //readiness latches, so stop asking. The store drops back out of DataReady every time
            //interest moves, and none of that should reach the session gate.
            Enabled = false;
        }
    }
}
