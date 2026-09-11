using System.IO;
using DigBlocks.Simulation.Content;
using DigBlocks.Simulation.Definitions;
using NUnit.Framework;
using UnityEngine;

namespace DigBlocks.Simulation.Tests
{
    //guards the entity content the game actually ships. These assertions are the contract a content
    //edit has to update deliberately, which is what stops a stray hitbox or ghost policy change from
    //going unnoticed. Shipped content that fails to compile is a fatal session error, not a warning.
    public sealed class ShippedEntityContentTests
    {
        private static CompiledEntityContent Load() => EntityContentLoader.LoadFromDirectory(
            Path.Combine(Application.streamingAssetsPath, EntityContentLoader.DefaultContentFolder));

        [Test]
        public void ShippedContentCompilesWithOrdinalIdsAfterTheReservedZero()
        {
            var registry = Load().Registry;
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.GetId("digblocks:debug_orbiter"), Is.EqualTo(1));
            Assert.That(registry.Fingerprint, Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void TheDebugOrbiterResolvesThroughTheMobArchetype()
        {
            var registry = Load().Registry;
            var orbiter = registry[registry.GetId("digblocks:debug_orbiter")];

            //it inherits the mob archetype's replication policy but overrides everything physical:
            //it is a marker that flies a fixed path, so it neither falls, collides, nor persists.
            Assert.That(orbiter.Attributes.GhostMode, Is.EqualTo(EntityGhostMode.Interpolated));
            Assert.That(orbiter.Attributes.GhostOptimization, Is.EqualTo(EntityGhostOptimization.Dynamic));
            Assert.That(orbiter.Attributes.Category, Is.EqualTo(EntityCategory.Marker));
            Assert.That(orbiter.Attributes.Has(EntityFlags.Gravity), Is.False);
            Assert.That(orbiter.Attributes.Has(EntityFlags.Collides), Is.False);
            Assert.That(orbiter.Attributes.Has(EntityFlags.Persists), Is.False);
            Assert.That(orbiter.Attributes.Width, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(orbiter.Attributes.Height, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(orbiter.Behaviors, Is.EqualTo(new[] { "digblocks:circle_flight" }));
        }

        [Test]
        public void TheDebugOrbiterModelIsOneBoxMatchingItsHitbox()
        {
            var content = Load();
            var model = content.ModelOf(content.Registry.GetId("digblocks:debug_orbiter"));

            Assert.That(model, Is.Not.Null);
            Assert.That(model.Key, Is.EqualTo("digblocks:models/debug_cube"));
            Assert.That(model.Boxes, Has.Count.EqualTo(1));
            //0.6 blocks is 9.6 model units, so the drawn box matches the authored hitbox width.
            Assert.That(model.Boxes[0].Size.x / EntityModelDefinition.UnitsPerBlock, Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(model.Boxes[0].Name, Is.EqualTo("body"));
        }
    }
}
