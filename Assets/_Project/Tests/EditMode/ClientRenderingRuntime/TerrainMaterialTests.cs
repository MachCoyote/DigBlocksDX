using NUnit.Framework;
using System.IO;
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

        [Test]
        public void ComputeCullerRejectsCameraHiddenChunksButKeepsShadowBatchesConservative()
        {
            string source = File.ReadAllText("Assets/_Project/Shaders/Terrain/TerrainStreaming.compute");

            StringAssert.Contains("_ShadowOnly == 0 && data.cameraVisible == 0", source);
            StringAssert.Contains("if (_ShadowOnly == 0)", source);
            StringAssert.Contains("shadow batches conservatively retain off-camera chunks", source);
        }
    }
}
