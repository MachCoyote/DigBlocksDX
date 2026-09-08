using System;
using DigBlocks.Client.Rendering.Editor;
using NUnit.Framework;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor.Tests
{
    public sealed class BlockTextureArrayBakerTests
    {
        //Four 2x2 tiles laid out in a 4x4 image, distinct solid colour per tile.
        //Source pixels are bottom-origin, matching Texture2D.GetPixels32.
        private static readonly Color32 BottomLeft = new Color32(10, 0, 0, 255);
        private static readonly Color32 BottomRight = new Color32(20, 0, 0, 255);
        private static readonly Color32 TopLeft = new Color32(30, 0, 0, 255);
        private static readonly Color32 TopRight = new Color32(40, 0, 0, 255);

        private static Color32[] BuildFourTileImage()
        {
            var image = new Color32[16];
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool right = x >= 2;
                bool top = y >= 2;
                image[y * 4 + x] = top
                    ? (right ? TopRight : TopLeft)
                    : (right ? BottomRight : BottomLeft);
            }

            return image;
        }

        [Test]
        public void ResolveLayoutComputesColumnsAndRows()
        {
            var layout = BlockTextureArrayBaker.ResolveLayout(512, 256, 16);

            Assert.That(layout.Columns, Is.EqualTo(32));
            Assert.That(layout.Rows, Is.EqualTo(16));
            Assert.That(layout.SliceCount, Is.EqualTo(512));
        }

        [Test]
        public void ResolveLayoutRejectsIndivisibleDimensions()
        {
            Assert.Throws<ArgumentException>(() => BlockTextureArrayBaker.ResolveLayout(30, 32, 16));
        }

        [Test]
        public void SliceIntoTopLeftRowMajorOrdersFromTopLeftTile()
        {
            var image = BuildFourTileImage();
            var layout = BlockTextureArrayBaker.ResolveLayout(4, 4, 2);

            var slices = BlockTextureArrayBaker.SliceInto(
                image, 4, 4, layout, BlockAtlasSliceOrder.TopLeftRowMajor);

            Assert.That(slices.Length, Is.EqualTo(4));
            AssertUniform(slices[0], TopLeft);
            AssertUniform(slices[1], TopRight);
            AssertUniform(slices[2], BottomLeft);
            AssertUniform(slices[3], BottomRight);
        }

        [Test]
        public void SliceIntoBottomLeftRowMajorOrdersFromBottomLeftTile()
        {
            var image = BuildFourTileImage();
            var layout = BlockTextureArrayBaker.ResolveLayout(4, 4, 2);

            var slices = BlockTextureArrayBaker.SliceInto(
                image, 4, 4, layout, BlockAtlasSliceOrder.BottomLeftRowMajor);

            AssertUniform(slices[0], BottomLeft);
            AssertUniform(slices[1], BottomRight);
            AssertUniform(slices[2], TopLeft);
            AssertUniform(slices[3], TopRight);
        }

        [Test]
        public void SliceIntoPreservesWithinTileOrientation()
        {
            //Distinct value per texel so a flipped or transposed copy would be caught.
            var image = new Color32[16];
            for (int i = 0; i < 16; i++)
                image[i] = new Color32((byte)i, 0, 0, 255);
            var layout = BlockTextureArrayBaker.ResolveLayout(4, 4, 2);

            var slices = BlockTextureArrayBaker.SliceInto(
                image, 4, 4, layout, BlockAtlasSliceOrder.BottomLeftRowMajor);

            //Slice 0 is the bottom-left tile: source rows y0,y1 and columns x0,x1.
            Assert.That(slices[0][0].r, Is.EqualTo(0));  //(x0,y0)
            Assert.That(slices[0][1].r, Is.EqualTo(1));  //(x1,y0)
            Assert.That(slices[0][2].r, Is.EqualTo(4));  //(x0,y1)
            Assert.That(slices[0][3].r, Is.EqualTo(5));  //(x1,y1)
        }

        [Test]
        public void DilateAlphaFillsTransparentNeighboursWithoutRaisingAlpha()
        {
            //2x2 tile: one opaque texel, three fully transparent.
            var pixels = new Color32[4];
            pixels[0] = new Color32(100, 150, 200, 255);
            for (int i = 1; i < 4; i++)
                pixels[i] = new Color32(0, 0, 0, 0);

            BlockTextureArrayBaker.DilateAlpha(pixels, 2, passes: 1);

            for (int i = 1; i < 4; i++)
            {
                Assert.That(pixels[i].a, Is.EqualTo(0), $"texel {i} alpha must stay transparent");
                Assert.That(pixels[i].r, Is.EqualTo(100));
                Assert.That(pixels[i].g, Is.EqualTo(150));
                Assert.That(pixels[i].b, Is.EqualTo(200));
            }
        }

        [Test]
        public void DilateAlphaLeavesFullyTransparentTileUnchanged()
        {
            var pixels = new Color32[4];
            for (int i = 0; i < 4; i++)
                pixels[i] = new Color32(0, 0, 0, 0);

            Assert.DoesNotThrow(() => BlockTextureArrayBaker.DilateAlpha(pixels, 2, passes: 8));
            foreach (var texel in pixels)
                Assert.That(texel, Is.EqualTo(new Color32(0, 0, 0, 0)));
        }

        private static void AssertUniform(Color32[] slice, Color32 expected)
        {
            foreach (var texel in slice)
                Assert.That(texel, Is.EqualTo(expected));
        }
    }
}
