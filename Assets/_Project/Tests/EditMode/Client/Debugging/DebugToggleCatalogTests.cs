using System.Collections.Generic;
using DigBlocks.Client.Debugging;
using DigBlocks.Core.Diagnostics;
using NUnit.Framework;

namespace DigBlocks.Client.Tests.Debugging
{
    public sealed class DebugToggleCatalogTests
    {
        [Test]
        public void EveryToggleHasAUniqueIdentifier()
        {
            var seen = new HashSet<DebugToggleId>();

            foreach (DebugToggle toggle in DebugToggleCatalog.Default)
            {
                Assert.That(seen.Add(toggle.Id), Is.True, $"'{toggle.Id}' is declared more than once.");
            }
        }

        //two toggles sharing a key would both fire on one press, and neither author would notice
        [Test]
        public void EveryToggleHasAUniqueKey()
        {
            var seen = new HashSet<string> { DebugToggleCatalog.MenuKey.ToString() };

            foreach (DebugToggle toggle in DebugToggleCatalog.Default)
            {
                Assert.That(seen.Add(toggle.BindDisplay), Is.True, $"'{toggle.BindDisplay}' is bound twice.");
            }
        }

        [Test]
        public void EveryToggleCyclesThroughAtLeastTwoNamedStates()
        {
            foreach (DebugToggle toggle in DebugToggleCatalog.Default)
            {
                Assert.That(toggle.StateCount, Is.GreaterThanOrEqualTo(2), toggle.Label);

                //the menu prints whatever the state is called, so an unnamed one reads as a bare number
                var named = new HashSet<string>();
                for (int state = 0; state < toggle.StateCount; state++)
                {
                    string label = toggle.DescribeState(state);
                    Assert.That(label, Is.Not.Null.And.Not.Empty, toggle.Label);
                    Assert.That(named.Add(label), Is.True, $"'{toggle.Label}' names two states '{label}'.");
                }
            }
        }

        //state zero is what the switchboard starts and resets to, so a toggle that alters how the world is
        //shaded has to be off there. A toggle that only picks between equivalent modes has no such state
        [Test]
        public void ShadingTogglesAreOffUntilSomebodyPressesTheirKey()
        {
            Assert.That(Find(DebugToggleIds.Wireframe).DescribeState(0), Is.EqualTo("Off"));
            Assert.That(Find(DebugToggleIds.Fullbright).DescribeState(0), Is.EqualTo("Off"));
            Assert.That(Find(DebugToggleIds.Overdraw).DescribeState(0), Is.EqualTo("Off"));
        }

        [Test]
        public void RenderingToggglesAreDeclaredWithTheirDocumentedBinds()
        {
            Assert.That(Find(DebugToggleIds.Wireframe).BindDisplay, Is.EqualTo("F8"));
            Assert.That(Find(DebugToggleIds.Fullbright).BindDisplay, Is.EqualTo("F7"));
            Assert.That(Find(DebugToggleIds.Overdraw).BindDisplay, Is.EqualTo("F6"));
            Assert.That(Find(DebugToggleIds.SecondaryCameraCulling).BindDisplay, Is.EqualTo("F5"));
        }

        private static DebugToggle Find(DebugToggleId id)
        {
            foreach (DebugToggle toggle in DebugToggleCatalog.Default)
            {
                if (toggle.Id == id)
                {
                    return toggle;
                }
            }

            Assert.Fail($"'{id}' is not declared in the catalog.");
            return null;
        }
    }
}
