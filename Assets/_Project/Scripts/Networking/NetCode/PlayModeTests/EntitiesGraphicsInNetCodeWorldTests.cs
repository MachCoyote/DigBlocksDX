using System.Collections;
using Cysharp.Threading.Tasks;
using DigBlocks.Client.Rendering;
using DigBlocks.Simulation;
using DigBlocks.Simulation.Content;
using DigBlocks.Simulation.Definitions;
using NUnit.Framework;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.TestTools;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //The one assumption in the entity foundation that could not be settled by reading code: whether
    //Entities Graphics actually runs inside a client world created by ClientServerBootstrap rather
    //than as the default world. If it does not, the presentation seam is what absorbs the change.
    public sealed class EntitiesGraphicsInNetCodeWorldTests
    {
        private const string Cube = @"{ ""key"": ""digblocks:models/cube"", ""texture"": ""digblocks:entity/debug"",
            ""boxes"": [ { ""name"": ""body"", ""origin"": [-8, 0, -8], ""size"": [16, 16, 16] } ] }";

        [UnityTest]
        public IEnumerator AGhostGetsRenderComponentsInsideANetCodeClientWorld() => UniTask.ToCoroutine(async () =>
        {
            World world = NetCodeWorldFactory.CreateClientWorld(true);
            try
            {
                //the presentation-side systems have to exist here, not only in the default world.
                Assert.That(world.GetExistingSystemManaged<EntitiesGraphicsSystem>(), Is.Not.Null,
                    "Entities Graphics should be present in a NetCode client world");

                var content = EntityContentLoader.Load(new MemoryEntityContentSource()
                    .Add(EntityContentCategory.Models, "model.json", Cube)
                    .Add(EntityContentCategory.Types, "type.json",
                        @"{ ""key"": ""digblocks:pig"", ""model"": ""digblocks:models/cube"" }"));

                var backend = new EntitiesGraphicsPresentationBackend();
                var views = world.GetOrCreateSystemManaged<EntityViewSystem>();
                views.Configure(backend, content);

                Entity entity = world.EntityManager.CreateEntity(
                    typeof(EntityTypeId), typeof(LocalTransform), typeof(LocalToWorld));
                world.EntityManager.SetComponentData(entity, new EntityTypeId { Value = content.Registry.GetId("digblocks:pig") });
                world.EntityManager.SetComponentData(entity, LocalTransform.Identity);

                views.Update();

                Assert.That(world.EntityManager.HasComponent<EntityViewAttached>(entity), Is.True);
                //what Entities Graphics actually renders from.
                Assert.That(world.EntityManager.HasComponent<MaterialMeshInfo>(entity), Is.True,
                    "the backend should have given the entity something to draw");
                Assert.That(world.EntityManager.HasComponent<RenderBounds>(entity), Is.True);
                await UniTask.Yield();
            }
            finally
            {
                if (world.IsCreated)
                {
                    ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                    world.Dispose();
                }
            }
        });
    }
}
