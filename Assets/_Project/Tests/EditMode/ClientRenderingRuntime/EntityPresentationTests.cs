using System.Collections.Generic;
using DigBlocks.Client.Rendering;
using DigBlocks.Simulation;
using DigBlocks.Simulation.Content;
using DigBlocks.Simulation.Definitions;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Tests
{
    //Presentation is attached after spawn rather than baked into the ghost prefab, so the prefab
    //stays simulation-only and identical in both worlds. These tests cover that seam: the one place
    //a renderer is chosen, and the geometry a box model compiles to.
    public sealed class EntityPresentationTests
    {
        private const string Cube = @"{ ""key"": ""digblocks:models/cube"", ""texture"": ""digblocks:entity/debug"",
            ""boxes"": [ { ""name"": ""body"", ""origin"": [-8, 0, -8], ""size"": [16, 16, 16] } ] }";

        private static CompiledEntityContent Content(string types) =>
            EntityContentLoader.Load(new MemoryEntityContentSource()
                .Add(EntityContentCategory.Models, "model.json", Cube)
                .Add(EntityContentCategory.Types, "type.json", types));

        [Test]
        public void ABoxModelCompilesToOneCubePerBox()
        {
            var content = Content(@"{ ""key"": ""digblocks:pig"", ""model"": ""digblocks:models/cube"" }");
            var mesh = EntityModelMesh.Build(content.ModelOf("digblocks:models/cube"));
            try
            {
                //six faces of four vertices, and two triangles per face.
                Assert.That(mesh.vertexCount, Is.EqualTo(24));
                Assert.That(mesh.triangles.Length, Is.EqualTo(36));
                //sixteen model units is one block, so the authored cube is one block across.
                Assert.That(mesh.bounds.size.x, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(mesh.bounds.size.y, Is.EqualTo(1f).Within(1e-4f));
                //the box sits on its origin rather than being centred on it, which is what puts a
                //mob's feet on the ground rather than half a body through it.
                Assert.That(mesh.bounds.min.y, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(mesh.bounds.min.x, Is.EqualTo(-0.5f).Within(1e-4f));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void EveryEntityGetsAViewExactlyOnce()
        {
            var content = Content(@"{ ""key"": ""digblocks:pig"", ""model"": ""digblocks:models/cube"" }");
            var backend = new RecordingBackend();
            using var world = new World("presentation-test");
            var system = world.GetOrCreateSystemManaged<EntityViewSystem>();
            system.Configure(backend, content);

            ushort pig = content.Registry.GetId("digblocks:pig");
            Entity first = world.EntityManager.CreateEntity(typeof(EntityTypeId));
            world.EntityManager.SetComponentData(first, new EntityTypeId { Value = pig });

            system.Update();
            Assert.That(backend.Attached, Has.Count.EqualTo(1));
            Assert.That(world.EntityManager.HasComponent<EntityViewAttached>(first), Is.True);

            //running again must not attach a second view to the same entity.
            system.Update();
            Assert.That(backend.Attached, Has.Count.EqualTo(1));

            //a newly replicated entity is picked up on the next pass.
            Entity second = world.EntityManager.CreateEntity(typeof(EntityTypeId));
            world.EntityManager.SetComponentData(second, new EntityTypeId { Value = pig });
            system.Update();
            Assert.That(backend.Attached, Has.Count.EqualTo(2));
        }

        //a type with no model is a legitimate thing to be: a marker or trigger still replicates and
        //simulates, it just draws nothing. It must not be revisited every frame looking for one.
        [Test]
        public void ATypeWithNoModelIsMarkedRatherThanRetried()
        {
            var content = Content(@"{ ""key"": ""digblocks:trigger"" }");
            var backend = new RecordingBackend();
            using var world = new World("presentation-test-nomodel");
            var system = world.GetOrCreateSystemManaged<EntityViewSystem>();
            system.Configure(backend, content);

            Entity entity = world.EntityManager.CreateEntity(typeof(EntityTypeId));
            world.EntityManager.SetComponentData(entity, new EntityTypeId { Value = content.Registry.GetId("digblocks:trigger") });

            system.Update();
            Assert.That(backend.Attached, Is.Empty);
            Assert.That(world.EntityManager.HasComponent<EntityViewAttached>(entity), Is.True);
        }

        //the seam exists so a renderer can be replaced without touching anything else; a test backend
        //standing in for Entities Graphics is the same substitution.
        private sealed class RecordingBackend : IEntityPresentationBackend
        {
            public List<string> Attached { get; } = new List<string>();
            public bool Disposed { get; private set; }

            public void Attach(EntityManager manager, Entity entity, EntityModelDefinition model) =>
                Attached.Add(model.Key);

            public void Dispose() => Disposed = true;
        }
    }
}
