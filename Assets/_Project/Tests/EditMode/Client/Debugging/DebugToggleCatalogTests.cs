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
        public void EveryToggleCyclesThroughAtLeastTwoStates()
        {
            foreach (DebugToggle toggle in DebugToggleCatalog.Default)
            {
                Assert.That(toggle.StateCount, Is.GreaterThanOrEqualTo(2), toggle.Label);
                Assert.That(toggle.DescribeState(0), Is.EqualTo("Off"), toggle.Label);
            }
        }

        [Test]
        public void RenderingToggglesAreDeclaredWithTheirDocumentedBinds()
        {
            Assert.That(Find(DebugToggleIds.Wireframe).BindDisplay, Is.EqualTo("F8"));
            Assert.That(Find(DebugToggleIds.Fullbright).BindDisplay, Is.EqualTo("F7"));
            Assert.That(Find(DebugToggleIds.Overdraw).BindDisplay, Is.EqualTo("F6"));
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
