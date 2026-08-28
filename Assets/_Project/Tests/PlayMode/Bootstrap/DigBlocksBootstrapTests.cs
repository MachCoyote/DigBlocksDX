using System.Collections;
using DigBlocks.Core.Hosting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DigBlocks.Bootstrap.PlayModeTests
{
    public sealed class DigBlocksBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_StartsEmptyHostAndRejectsDuplicate()
        {
            var ownerObject = new GameObject("Bootstrap Owner");
            DigBlocksBootstrap owner = ownerObject.AddComponent<DigBlocksBootstrap>();
            var duplicateObject = new GameObject("Bootstrap Duplicate");
            duplicateObject.AddComponent<DigBlocksBootstrap>();

            yield return null;

            DigBlocksBootstrap[] bootstraps = Object.FindObjectsByType<DigBlocksBootstrap>(
                FindObjectsSortMode.None);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0], Is.SameAs(owner));
            Assert.That(owner.HostState, Is.EqualTo(GameHostState.Running));
            Assert.That(owner.LastFailure, Is.Null);

            Object.Destroy(ownerObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SampleScene_ContainsRunningBootstrap()
        {
            SceneManager.LoadScene("SampleScene", LoadSceneMode.Single);
            yield return null;

            DigBlocksBootstrap[] bootstraps = Object.FindObjectsByType<DigBlocksBootstrap>(
                FindObjectsSortMode.None);
            Assert.That(bootstraps, Has.Length.EqualTo(1));
            Assert.That(bootstraps[0].HostState, Is.EqualTo(GameHostState.Running));
            Assert.That(bootstraps[0].LastFailure, Is.Null);
        }
    }
}
