using System;
using System.Collections.Generic;
using DigBlocks.Simulation;
using NUnit.Framework;
using Unity.Collections;

namespace DigBlocks.Simulation.Tests
{
    public sealed class EntityTypeRegistryTests
    {
        private static EntityTypeDefinition Type(string key, string model = "digblocks:models/cube",
            EntityTypeAttributes? attributes = null, params string[] behaviors) =>
            new EntityTypeDefinition(key, model, behaviors ?? Array.Empty<string>(), attributes ?? EntityTypeAttributes.Default);

        private static EntityTypeRegistry Registry(params EntityTypeDefinition[] types) => new EntityTypeRegistry(types);

        //ids come from sorted keys, so two peers that authored their content in a different order
        //still agree on what id 1 means.
        [Test]
        public void IdsDoNotDependOnDefinitionOrder()
        {
            var forward = Registry(Type("digblocks:pig"), Type("digblocks:cow"), Type("digblocks:zombie"));
            var reversed = Registry(Type("digblocks:zombie"), Type("digblocks:cow"), Type("digblocks:pig"));

            Assert.That(forward.GetId("digblocks:cow"), Is.EqualTo(reversed.GetId("digblocks:cow")));
            Assert.That(forward.GetId("digblocks:pig"), Is.EqualTo(reversed.GetId("digblocks:pig")));
            Assert.That(forward.Fingerprint, Is.EqualTo(reversed.Fingerprint));
        }

        [Test]
        public void ZeroIsReservedSoADefaultComponentNamesNoType()
        {
            var registry = Registry(Type("digblocks:pig"));
            Assert.That(new EntityTypeId().IsValid, Is.False);
            Assert.That(registry.GetId("digblocks:pig"), Is.Not.EqualTo(EntityTypeId.None));
            Assert.That(() => registry[EntityTypeId.None], Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        //a client with different mob art still simulates the same world; a different hitbox does not.
        [Test]
        public void FingerprintIgnoresTheModelButNotTheHitbox()
        {
            var baseline = Registry(Type("digblocks:pig", model: "digblocks:models/pig"));
            var restyled = Registry(Type("digblocks:pig", model: "digblocks:models/pig_alternate"));
            Assert.That(restyled.Fingerprint, Is.EqualTo(baseline.Fingerprint));

            var wider = Registry(Type("digblocks:pig", model: "digblocks:models/pig", attributes: new EntityTypeAttributes(
                EntityTypeAttributes.Default.Flags, EntityCategory.Mob, 1.4f, 1.8f,
                EntityGhostMode.Interpolated, EntityGhostOptimization.Dynamic, 1)));
            Assert.That(wider.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));
        }

        [Test]
        public void FingerprintCoversBehavioursAndReplicationPolicy()
        {
            var baseline = Registry(Type("digblocks:pig"));
            var behaving = Registry(Type("digblocks:pig", behaviors: new[] { "digblocks:circle_flight" }));
            Assert.That(behaving.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));

            var predicted = Registry(Type("digblocks:pig", attributes: new EntityTypeAttributes(
                EntityTypeAttributes.Default.Flags, EntityCategory.Mob, 0.6f, 1.8f,
                EntityGhostMode.Predicted, EntityGhostOptimization.Dynamic, 1)));
            Assert.That(predicted.Fingerprint, Is.Not.EqualTo(baseline.Fingerprint));
        }

        [Test]
        public void DuplicateAndMalformedKeysAreRejected()
        {
            Assert.That(() => Registry(Type("digblocks:pig"), Type("digblocks:pig")), Throws.ArgumentException);
            Assert.That(() => Type("Digblocks:Pig"), Throws.ArgumentException);
            Assert.That(() => Type("pig"), Throws.ArgumentException);
        }

        [Test]
        public void BehavioursAreSortedAndDuplicatesRejected()
        {
            var type = Type("digblocks:pig", behaviors: new[] { "digblocks:wander", "digblocks:circle_flight" });
            Assert.That(type.Behaviors, Is.EqualTo(new[] { "digblocks:circle_flight", "digblocks:wander" }));
            Assert.That(() => Type("digblocks:cow", behaviors: new[] { "digblocks:wander", "digblocks:wander" }),
                Throws.ArgumentException);
        }

        [Test]
        public void AttributeTableIsIndexedByRuntimeId()
        {
            var flier = new EntityTypeAttributes(EntityFlags.None, EntityCategory.Marker, 0.6f, 0.6f,
                EntityGhostMode.Interpolated, EntityGhostOptimization.Static, 3);
            var registry = Registry(Type("digblocks:cow"), Type("digblocks:orbiter", attributes: flier));

            using var table = registry.CreateAttributeTable(Allocator.Temp);
            Assert.That(table.Length, Is.EqualTo(registry.Count + 1));
            Assert.That(table[registry.GetId("digblocks:orbiter")], Is.EqualTo(flier));
            Assert.That(table[registry.GetId("digblocks:cow")], Is.EqualTo(EntityTypeAttributes.Default));
        }

        [Test]
        public void UnknownKeysReportRatherThanGuess()
        {
            var registry = Registry(Type("digblocks:pig"));
            Assert.That(registry.TryGetId("digblocks:missing", out _), Is.False);
            Assert.That(registry.TryGetId(null, out _), Is.False);
            Assert.That(() => registry.GetId("digblocks:missing"), Throws.TypeOf<KeyNotFoundException>());
        }
    }
}
