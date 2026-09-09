using System.Collections.Generic;
using DigBlocks.Core.Diagnostics;
using NUnit.Framework;

namespace DigBlocks.Core.Tests.Diagnostics
{
    public sealed class DebugOptionsTests
    {
        private static readonly DebugToggleId Wireframe = new DebugToggleId("Wireframe");
        private static readonly DebugToggleId Colliders = new DebugToggleId("Colliders");

        [Test]
        public void UnknownToggle_ReadsAsOff()
        {
            var options = new DebugOptions();

            Assert.That(options.GetState(Wireframe), Is.EqualTo(0));
            Assert.That(options.IsEnabled(Wireframe), Is.False);
        }

        [Test]
        public void Cycle_AdvancesThroughEveryStateAndWraps()
        {
            var options = new DebugOptions();

            Assert.That(options.Cycle(Wireframe, 3), Is.EqualTo(1));
            Assert.That(options.Cycle(Wireframe, 3), Is.EqualTo(2));
            Assert.That(options.Cycle(Wireframe, 3), Is.EqualTo(0));
            Assert.That(options.IsEnabled(Wireframe), Is.False);
        }

        [Test]
        public void Cycle_LeavesOtherTogglesAlone()
        {
            var options = new DebugOptions();

            options.Cycle(Wireframe, 3);

            Assert.That(options.GetState(Colliders), Is.EqualTo(0));
        }

        [Test]
        public void StateChanged_ReportsEveryTransitionOnce()
        {
            var options = new DebugOptions();
            var observed = new List<int>();
            options.StateChanged += (id, state) =>
            {
                if (id == Wireframe)
                {
                    observed.Add(state);
                }
            };

            options.SetState(Wireframe, 2);
            options.SetState(Wireframe, 2);
            options.SetState(Wireframe, 0);

            Assert.That(observed, Is.EqualTo(new[] { 2, 0 }));
        }

        [Test]
        public void Reset_ReturnsEveryToggleToOffAndNotifies()
        {
            var options = new DebugOptions();
            options.SetState(Wireframe, 1);
            options.SetState(Colliders, 1);

            var cleared = new List<DebugToggleId>();
            options.StateChanged += (id, state) =>
            {
                if (state == 0)
                {
                    cleared.Add(id);
                }
            };

            options.Reset();

            Assert.That(options.GetState(Wireframe), Is.EqualTo(0));
            Assert.That(options.GetState(Colliders), Is.EqualTo(0));
            Assert.That(cleared, Is.EquivalentTo(new[] { Wireframe, Colliders }));
        }
    }
}
