using DigBlocks.Simulation;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;

namespace DigBlocks.Simulation.Tests
{
    //Behaviour state has to survive an unload without the stored record naming the components it
    //carries. These cover the seam itself rather than the one behaviour that happens to use it: a
    //second stateful behaviour has to work without editing anything the first one touched.
    public sealed class EntityStateCodecTests
    {
        private struct Wander : IComponentData
        {
            public float3 Target;
            public int StepsRemaining;
        }

        private const string WanderKey = "digblocks:wander";

        [Test]
        public void AnyUnmanagedComponentRoundTripsThroughItsCodec()
        {
            var codecs = new EntityStateCodecs(new ComponentStateCodec<Wander>(WanderKey));
            using var world = new World("state-codec-test");
            var manager = world.EntityManager;

            Entity entity = manager.CreateEntity(typeof(Wander));
            manager.SetComponentData(entity, new Wander { Target = new float3(3f, -4f, 5.5f), StepsRemaining = 17 });

            var captured = codecs.Capture(manager, entity);
            Assert.That(captured, Has.Count.EqualTo(1));
            Assert.That(captured[0].Behavior, Is.EqualTo(WanderKey));

            //a fresh entity standing in for the one the chunk reload spawns.
            Entity respawned = manager.CreateEntity(typeof(Wander));
            codecs.Restore(manager, respawned, captured);

            var restored = manager.GetComponentData<Wander>(respawned);
            Assert.That(restored.StepsRemaining, Is.EqualTo(17));
            Assert.That(restored.Target.x, Is.EqualTo(3f).Within(1e-6f));
            Assert.That(restored.Target.y, Is.EqualTo(-4f).Within(1e-6f));
            Assert.That(restored.Target.z, Is.EqualTo(5.5f).Within(1e-6f));
        }

        //most entities carry no behaviour state at all, and allocating a list per entity to say so
        //would be the common case paying for the rare one.
        [Test]
        public void AnEntityWithNoBehaviourStateCapturesNothing()
        {
            var codecs = new EntityStateCodecs(new ComponentStateCodec<Wander>(WanderKey));
            using var world = new World("state-codec-test-empty");
            Entity entity = world.EntityManager.CreateEntity();
            Assert.That(codecs.Capture(world.EntityManager, entity), Is.Null);
        }

        //content may name behaviours a given build has not got, and a record written by one build is
        //read by another. Neither may throw; the entity comes back on the behaviour's own defaults.
        [Test]
        public void StateThisBuildCannotReadIsSkippedRatherThanApplied()
        {
            var codecs = new EntityStateCodecs(new ComponentStateCodec<Wander>(WanderKey));
            using var world = new World("state-codec-test-foreign");
            var manager = world.EntityManager;
            Entity entity = manager.CreateEntity(typeof(Wander));
            manager.SetComponentData(entity, new Wander { StepsRemaining = 9 });

            codecs.Restore(manager, entity, new[]
            {
                new EntityStateRecord("digblocks:not_a_behaviour_here", new byte[4]),
                new EntityStateRecord(WanderKey, new byte[3])
            });

            Assert.That(manager.GetComponentData<Wander>(entity).StepsRemaining, Is.EqualTo(9),
                "an unreadable record must leave the component alone rather than reinterpret it");
        }

        [Test]
        public void TheDefaultCodecsCoverEveryStatefulBehaviour()
        {
            //circle flight is the only behaviour with state today. This exists so that adding one and
            //forgetting to register it is a failing test rather than state silently lost on a chunk
            //boundary, which is how it would otherwise show up.
            var codecs = EntityStateCodecs.Default;
            using var world = new World("state-codec-test-default");
            var manager = world.EntityManager;
            Entity entity = manager.CreateEntity(typeof(CircleFlight));
            manager.SetComponentData(entity, new CircleFlight { Radius = 6f, AngularSpeed = 1.2f, Phase = 2.5f });

            var captured = codecs.Capture(manager, entity);
            Assert.That(captured, Has.Count.EqualTo(1));
            Assert.That(captured[0].Behavior, Is.EqualTo(SimulationBehaviors.CircleFlight));

            Entity respawned = manager.CreateEntity(typeof(CircleFlight));
            codecs.Restore(manager, respawned, captured);
            Assert.That(manager.GetComponentData<CircleFlight>(respawned).Phase, Is.EqualTo(2.5f).Within(1e-6f));
        }
    }
}
