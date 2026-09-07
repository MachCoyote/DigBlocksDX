using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

[CreateAfter(typeof(BootstrapSystem))]
partial class CameraTrackerSystem : SystemBase
{
    protected override void OnCreate()
    {
        
    }

    protected override void OnUpdate()
    {
        Entity player;
        bool singleTon = SystemAPI.TryGetSingletonEntity<Player>(out player);
        if (singleTon)
        {
            LocalTransform playerTransform = EntityManager.GetComponentData<LocalTransform>(player);
            float3 playerPos = playerTransform.Position;
            quaternion playerRot = playerTransform.Rotation;

            Vector3 playerPosV3 = new Vector3(playerPos.x, playerPos.y, playerPos.z);
            Quaternion playerRotV3 = new Quaternion(playerRot.value.x, playerRot.value.y, playerRot.value.z, playerRot.value.w);

            Camera.main.transform.position = playerPosV3;
            Camera.main.transform.rotation = playerRotV3;
        }
    }

    protected override void OnDestroy()
    {
        
    }
}
