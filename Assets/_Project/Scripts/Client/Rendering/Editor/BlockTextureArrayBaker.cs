using System;
using UnityEngine;

namespace DigBlocks.Client.Rendering.Editor
{
    //Slice traversal order used when mapping atlas tiles onto Texture2DArray elements.
    public enum BlockAtlasSliceOrder
    {
        //Element 0 is the top-left tile, advancing right then down. Matches Unity's flipbook convention.
        TopLeftRowMajor = 0,

        //Element 0 is the bottom-left tile, advancing right then up. Matches raw texture-space order.
        BottomLeftRowMajor = 1,
    }

    //Resolved tile grid for a tightly packed atlas.
    public readonly struct BlockAtlasLayout
    {
        public readonly int TileSize;
        public readonly int Columns;
        public readonly int Rows;

        public int SliceCount => Columns * Rows;

        public BlockAtlasLayout(int tileSize, int columns, int rows)
        {
            TileSize = tileSize;
            Columns = columns;
            Rows = rows;
        }
    }

    //Pure atlas-slicing logic, kept free of the asset pipeline so it can be unit tested directly.
    public static class BlockTextureArrayBaker
    {
        public static BlockAtlasLayout ResolveLayout(int sourceWidth, int sourceHeight, int tileSize)
        {
            if (tileSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(tileSize), "Tile size must be positive.");
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new ArgumentException("Source image has no pixels.");
            if (sourceWidth % tileSize != 0 || sourceHeight % tileSize != 0)
                throw new ArgumentException(
                    $"Source {sourceWidth}x{sourceHeight} is not evenly divisible by tile size {tileSize}.");

            return new BlockAtlasLayout(tileSize, sourceWidth / tileSize, sourceHeight / tileSize);
        }

        //Splits a bottom-origin RGBA32 image into one bottom-origin buffer per array element.
        public static Color32[][] SliceInto(
            Color32[] source, int sourceWidth, int sourceHeight, in BlockAtlasLayout layout, BlockAtlasSliceOrder order)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (source.Length != sourceWidth * sourceHeight)
                throw new ArgumentException("Source pixel count does not match its dimensions.");

            int tile = layout.TileSize;
            int columns = layout.Columns;
            int rows = layout.Rows;
            var slices = new Color32[layout.SliceCount][];

            for (int slice = 0; slice < slices.Length; slice++)
            {
                int gridRow = slice / columns;
                int gridColumn = slice % columns;

                //Convert the ordered grid row into a bottom-origin tile row for sampling.
                int tileRowFromBottom = order == BlockAtlasSliceOrder.TopLeftRowMajor
                    ? rows - 1 - gridRow
                    : gridRow;

                var buffer = new Color32[tile * tile];
                int sourceX = gridColumn * tile;
                int sourceY = tileRowFromBottom * tile;
                for (int y = 0; y < tile; y++)
                    Array.Copy(source, (sourceY + y) * sourceWidth + sourceX, buffer, y * tile, tile);

                slices[slice] = buffer;
            }

            return slices;
        }

        //Bleeds opaque colour outward into fully transparent texels so mip averaging never pulls in black.
        //Alpha stays at zero; only RGB is filled.
        public static void DilateAlpha(Color32[] pixels, int size, int passes)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length != size * size)
                throw new ArgumentException("Pixel count does not match the tile size.");

            for (int pass = 0; pass < passes; pass++)
            {
                var snapshot = (Color32[])pixels.Clone();
                bool changed = false;

                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (snapshot[index].a != 0)
                        continue;

                    int r = 0, g = 0, b = 0, contributors = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size)
                            continue;
                        var neighbour = snapshot[ny * size + nx];
                        if (neighbour.a == 0)
                            continue;
                        r += neighbour.r;
                        g += neighbour.g;
                        b += neighbour.b;
                        contributors++;
                    }

                    if (contributors == 0)
                        continue;
                    pixels[index] = new Color32(
                        (byte)(r / contributors), (byte)(g / contributors), (byte)(b / contributors), 0);
                    changed = true;
                }

                if (!changed)
                    break;
            }
        }
    }
}
