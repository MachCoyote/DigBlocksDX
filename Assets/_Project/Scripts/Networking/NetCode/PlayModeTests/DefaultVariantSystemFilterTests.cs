using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;

namespace DigBlocks.Networking.NetCode.PlayModeTests
{
    //DefaultVariantSystemBase.OnCreate reaches for GhostComponentSerializerCollectionSystemGroup,
    //which exists only in a NetCode world. A variant system that advertises itself to the plain
    //default world therefore throws the moment Unity builds that world, which it does on entering
    //and leaving play mode, far away from anything this project runs.
    public sealed class DefaultVariantSystemFilterTests
    {
        [Test]
        public void TheProjectVariantSystemIsNotAddedToTheDefaultWorld()
        {
            Assert.That(SystemsFor(WorldSystemFilterFlags.Default).Contains(typeof(DigBlocksDefaultVariantSystem)), Is.False,
                "the default world has no NetCode serializer collection for a variant system to register with");
        }

        [Test]
        public void TheProjectVariantSystemRunsInTheWorldsThatReplicate()
        {
            Assert.That(SystemsFor(WorldSystemFilterFlags.ClientSimulation).Contains(typeof(DigBlocksDefaultVariantSystem)), Is.True);
            Assert.That(SystemsFor(WorldSystemFilterFlags.ServerSimulation).Contains(typeof(DigBlocksDefaultVariantSystem)), Is.True);
        }

        //whatever NetCode's own variant systems opt into is the correct set, since they have exactly
        //the same requirement. Matching it keeps this right through a package update.
        [Test]
        public void TheProjectVariantSystemMatchesNetCodesOwn()
        {
            foreach (var flags in new[]
            {
                WorldSystemFilterFlags.Default, WorldSystemFilterFlags.ClientSimulation,
                WorldSystemFilterFlags.ServerSimulation, WorldSystemFilterFlags.ThinClientSimulation,
                WorldSystemFilterFlags.BakingSystem, WorldSystemFilterFlags.Editor
            })
            {
                var systems = SystemsFor(flags);
                Assert.That(systems.Contains(typeof(DigBlocksDefaultVariantSystem)),
                    Is.EqualTo(systems.Contains(typeof(Unity.NetCode.TransformDefaultVariantSystem))),
                    $"world filter {flags} should treat both variant systems the same way");
            }
        }

        private static HashSet<Type> SystemsFor(WorldSystemFilterFlags flags) =>
            new HashSet<Type>(DefaultWorldInitialization.GetAllSystems(flags));
    }
}
