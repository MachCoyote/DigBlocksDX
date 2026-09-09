using NUnit.Framework;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class TerrainMaterialTests
    {
        [Test]
        public void TerrainShaderExposesEditableSurfacePropertiesWithCompatibleDefaults()
        {
            var material = new Material(Shader.Find("DigBlocks/Terrain"));
            try
            {
                Assert.That(material.HasProperty("_BaseColor"), Is.True);
                Assert.That(material.HasProperty("_Smoothness"), Is.True);
                Assert.That(material.HasProperty("_Metallic"), Is.True);
                Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(Color.white));
                Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(0.15f));
                Assert.That(material.GetFloat("_Metallic"), Is.Zero);
            }
            finally { Object.DestroyImmediate(material); }
        }
    }
}
