using System.IO;
using DigBlocks.Simulation.Content;
using DigBlocks.Simulation.Definitions;
using NUnit.Framework;

namespace DigBlocks.Simulation.Tests
{
    public sealed class EntityContentTests
    {
        private const string Mob = @"{ ""key"": ""digblocks:mob"", ""category"": ""mob"", ""width"": 0.6, ""height"": 1.8,
            ""gravity"": true, ""collides"": true, ""behaviors"": [ ""digblocks:wander"" ] }";

        private const string CubeModel = @"{ ""key"": ""digblocks:models/cube"", ""texture"": ""digblocks:entity/debug"",
            ""boxes"": [ { ""name"": ""body"", ""size"": [8, 8, 8] } ] }";

        private static CompiledEntityContent Load(string types, string archetypes = Mob, string models = CubeModel)
        {
            var source = new MemoryEntityContentSource();
            if (archetypes != null) source.Add(EntityContentCategory.Archetypes, "archetype.json", archetypes);
            if (models != null) source.Add(EntityContentCategory.Models, "model.json", models);
            if (types != null) source.Add(EntityContentCategory.Types, "type.json", types);
            return EntityContentLoader.Load(source);
        }

        //the whole point of the additive model: a plain entity type is three lines.
        [Test]
        public void UnsetFieldsInheritThroughTheArchetypeChain()
        {
            var content = Load(@"{ ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:mob"" }");
            var pig = content.Registry[content.Registry.GetId("digblocks:pig")];

            Assert.That(pig.Attributes.Width, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(pig.Attributes.Height, Is.EqualTo(1.8f).Within(1e-4f));
            Assert.That(pig.Attributes.Category, Is.EqualTo(EntityCategory.Mob));
            Assert.That(pig.Attributes.Has(EntityFlags.Gravity), Is.True);
        }

        [Test]
        public void TheNearestLayerWinsEveryFieldItSets()
        {
            var content = Load(@"{ ""key"": ""digblocks:ghost"", ""archetype"": ""digblocks:mob"",
                ""gravity"": false, ""height"": 2.4 }");
            var ghost = content.Registry[content.Registry.GetId("digblocks:ghost")];

            Assert.That(ghost.Attributes.Has(EntityFlags.Gravity), Is.False, "the type should override the archetype");
            Assert.That(ghost.Attributes.Height, Is.EqualTo(2.4f).Within(1e-4f));
            Assert.That(ghost.Attributes.Width, Is.EqualTo(0.6f).Within(1e-4f), "unset fields still inherit");
            Assert.That(ghost.Attributes.Has(EntityFlags.Collides), Is.True);
        }

        //behaviours union across the chain rather than replacing, so adding one does not silently
        //discard what the archetype already declared.
        [Test]
        public void BehavioursUnionAcrossTheChain()
        {
            var content = Load(@"{ ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:mob"",
                ""behaviors"": [ ""digblocks:panic"" ] }");
            var pig = content.Registry[content.Registry.GetId("digblocks:pig")];
            Assert.That(pig.Behaviors, Is.EqualTo(new[] { "digblocks:panic", "digblocks:wander" }));
        }

        [Test]
        public void ModelsResolveAndAreAddressableByTypeId()
        {
            var content = Load(@"{ ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:mob"", ""model"": ""digblocks:models/cube"" }");
            ushort id = content.Registry.GetId("digblocks:pig");

            var model = content.ModelOf(id);
            Assert.That(model, Is.Not.Null);
            Assert.That(model.Key, Is.EqualTo("digblocks:models/cube"));
            Assert.That(model.Boxes, Has.Count.EqualTo(1));
            Assert.That(model.Boxes[0].Name, Is.EqualTo("body"));
            Assert.That(model.TextureSize.x, Is.EqualTo(64), "textureSize should fall back to the default sheet");
        }

        //an unresolvable model is an authoring mistake, and it should be reported at load rather than
        //discovered when something tries to draw.
        [Test]
        public void AnUnknownModelIsRejectedAtLoad()
        {
            Assert.That(() => Load(@"{ ""key"": ""digblocks:pig"", ""model"": ""digblocks:models/missing"" }"),
                Throws.TypeOf<EntityContentException>());
        }

        [Test]
        public void UnknownArchetypesAndCyclesAreRejected()
        {
            Assert.That(() => Load(@"{ ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:missing"" }"),
                Throws.TypeOf<EntityContentException>());

            var source = new MemoryEntityContentSource()
                .Add(EntityContentCategory.Archetypes, "a.json", @"{ ""key"": ""digblocks:a"", ""archetype"": ""digblocks:b"" }")
                .Add(EntityContentCategory.Archetypes, "b.json", @"{ ""key"": ""digblocks:b"", ""archetype"": ""digblocks:a"" }")
                .Add(EntityContentCategory.Types, "t.json", @"{ ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:a"" }");
            Assert.That(() => EntityContentLoader.Load(source), Throws.TypeOf<EntityContentException>());
        }

        //a typo in a field name must fail loudly rather than being silently ignored.
        [Test]
        public void UnknownFieldsAreRejected()
        {
            Assert.That(() => Load(@"{ ""key"": ""digblocks:pig"", ""hieght"": 2.0 }"),
                Throws.TypeOf<EntityContentException>());
        }

        [Test]
        public void MalformedDocumentsNameTheFileThatFailed()
        {
            var error = Assert.Throws<EntityContentException>(() =>
                EntityContentLoader.Load(new MemoryEntityContentSource()
                    .Add(EntityContentCategory.Types, "broken.json", "{ not json")));
            Assert.That(error.Message, Does.Contain("broken.json"));
        }

        [Test]
        public void GhostPolicyIsAuthorable()
        {
            var content = Load(@"{ ""key"": ""digblocks:arrow"", ""category"": ""projectile"",
                ""ghostMode"": ""predicted"", ""ghostOptimization"": ""static"", ""ghostImportance"": 9 }", archetypes: null);
            var arrow = content.Registry[content.Registry.GetId("digblocks:arrow")];

            Assert.That(arrow.Attributes.Category, Is.EqualTo(EntityCategory.Projectile));
            Assert.That(arrow.Attributes.GhostMode, Is.EqualTo(EntityGhostMode.Predicted));
            Assert.That(arrow.Attributes.GhostOptimization, Is.EqualTo(EntityGhostOptimization.Static));
            Assert.That(arrow.Attributes.GhostImportance, Is.EqualTo(9));
        }

        [Test]
        public void AWorldWithNoEntityContentCompilesToAnEmptyRegistry()
        {
            var content = EntityContentLoader.Load(new MemoryEntityContentSource());
            Assert.That(content.Registry.Count, Is.Zero);
            Assert.That(content.Registry.Fingerprint, Is.Not.Empty);
        }

        //several related types in one file, matching how block content is allowed to be authored,
        //and the folder layout the shipped content actually uses.
        [Test]
        public void ADirectoryOfContentLoadsIncludingMultiTypeFiles()
        {
            string root = Path.Combine(Path.GetTempPath(), "digblocks-entity-content-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "entity_archetypes"));
                Directory.CreateDirectory(Path.Combine(root, "entity_models"));
                Directory.CreateDirectory(Path.Combine(root, "entities"));
                File.WriteAllText(Path.Combine(root, "entity_archetypes", "mob.json"), Mob);
                File.WriteAllText(Path.Combine(root, "entity_models", "cube.json"), CubeModel);
                File.WriteAllText(Path.Combine(root, "entities", "livestock.json"),
                    @"[ { ""key"": ""digblocks:pig"", ""archetype"": ""digblocks:mob"" },
                        { ""key"": ""digblocks:cow"", ""archetype"": ""digblocks:mob"" } ]");

                var content = EntityContentLoader.LoadFromDirectory(root);
                Assert.That(content.Registry.Count, Is.EqualTo(2));
                Assert.That(content.Registry.TryGetId("digblocks:cow", out _), Is.True);
                Assert.That(content.Registry.TryGetId("digblocks:pig", out _), Is.True);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
