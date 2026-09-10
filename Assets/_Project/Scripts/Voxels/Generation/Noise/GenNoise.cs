using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>How a fractal sum shapes each octave before accumulating it.</summary>
    public enum FbmMode : byte
    {
        /// <summary>Plain fractal Brownian motion. Rolling hills.</summary>
        Standard = 0,
        /// <summary>Absolute value, so zero crossings become sharp crests. Mountain ridges.</summary>
        Ridged = 1,
        /// <summary>Absolute value inverted, so zero crossings become creases. Puffy, cloud-like.</summary>
        Billow = 2
    }

    /// <summary>
    /// Seeded gradient and value noise, written here rather than taken from
    /// <c>Unity.Mathematics.noise</c> for two reasons: that implementation has no seed hook, and its
    /// codegen is not contracted to stay identical across Burst versions and backends.
    /// <para>
    /// Every operation below is exact or correctly rounded under IEEE-754, so results are
    /// bit-identical across platforms. Nothing here may use a transcendental function.
    /// </para>
    /// </summary>
    public static class GenNoise
    {
        //the theoretical bound of Perlin noise with unit gradients is sqrt(n)/2, so these bring both
        //variants into a guaranteed [-1, 1]. Ordinary samples concentrate around +/-0.7 of that;
        //Remap and Spline are the intended way to spread a field back out.
        private const float Perlin2Scale = 1.41421356f;   //sqrt(2)
        private const float Perlin3Scale = 1.15470054f;   //2 / sqrt(3)
        private const float Diagonal = 0.70710678f;       //1 / sqrt(2)

        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        //written out rather than math.lerp so the operation order is part of the contract.
        private static float Lerp(float a, float b, float t) => a + t * (b - a);

        private static float Grad2(uint hash, float x, float y)
        {
            switch (hash & 7u)
            {
                case 0: return x;
                case 1: return -x;
                case 2: return y;
                case 3: return -y;
                case 4: return Diagonal * (x + y);
                case 5: return Diagonal * (-x + y);
                case 6: return Diagonal * (x - y);
                default: return Diagonal * (-x - y);
            }
        }

        //the twelve cube-edge directions of Perlin's improved noise, scaled to unit length.
        private static float Grad3(uint hash, float x, float y, float z)
        {
            switch (hash % 12u)
            {
                case 0: return Diagonal * (x + y);
                case 1: return Diagonal * (-x + y);
                case 2: return Diagonal * (x - y);
                case 3: return Diagonal * (-x - y);
                case 4: return Diagonal * (x + z);
                case 5: return Diagonal * (-x + z);
                case 6: return Diagonal * (x - z);
                case 7: return Diagonal * (-x - z);
                case 8: return Diagonal * (y + z);
                case 9: return Diagonal * (-y + z);
                case 10: return Diagonal * (y - z);
                default: return Diagonal * (-y - z);
            }
        }

        /// <summary>Seeded 2D gradient noise in [-1, 1].</summary>
        public static float Perlin2(uint seed, float x, float y)
        {
            float baseX = math.floor(x), baseY = math.floor(y);
            int ix = (int)baseX, iy = (int)baseY;
            float tx = x - baseX, ty = y - baseY;
            float u = Fade(tx), v = Fade(ty);

            float c00 = Grad2(GenHash.Lattice(seed, ix, iy), tx, ty);
            float c10 = Grad2(GenHash.Lattice(seed, ix + 1, iy), tx - 1f, ty);
            float c01 = Grad2(GenHash.Lattice(seed, ix, iy + 1), tx, ty - 1f);
            float c11 = Grad2(GenHash.Lattice(seed, ix + 1, iy + 1), tx - 1f, ty - 1f);

            return Lerp(Lerp(c00, c10, u), Lerp(c01, c11, u), v) * Perlin2Scale;
        }

        /// <summary>Seeded 3D gradient noise in [-1, 1].</summary>
        public static float Perlin3(uint seed, float x, float y, float z)
        {
            float baseX = math.floor(x), baseY = math.floor(y), baseZ = math.floor(z);
            int ix = (int)baseX, iy = (int)baseY, iz = (int)baseZ;
            float tx = x - baseX, ty = y - baseY, tz = z - baseZ;
            float u = Fade(tx), v = Fade(ty), w = Fade(tz);
            float tx1 = tx - 1f, ty1 = ty - 1f, tz1 = tz - 1f;

            float c000 = Grad3(GenHash.Lattice(seed, ix, iy, iz), tx, ty, tz);
            float c100 = Grad3(GenHash.Lattice(seed, ix + 1, iy, iz), tx1, ty, tz);
            float c010 = Grad3(GenHash.Lattice(seed, ix, iy + 1, iz), tx, ty1, tz);
            float c110 = Grad3(GenHash.Lattice(seed, ix + 1, iy + 1, iz), tx1, ty1, tz);
            float c001 = Grad3(GenHash.Lattice(seed, ix, iy, iz + 1), tx, ty, tz1);
            float c101 = Grad3(GenHash.Lattice(seed, ix + 1, iy, iz + 1), tx1, ty, tz1);
            float c011 = Grad3(GenHash.Lattice(seed, ix, iy + 1, iz + 1), tx, ty1, tz1);
            float c111 = Grad3(GenHash.Lattice(seed, ix + 1, iy + 1, iz + 1), tx1, ty1, tz1);

            float x00 = Lerp(c000, c100, u), x10 = Lerp(c010, c110, u);
            float x01 = Lerp(c001, c101, u), x11 = Lerp(c011, c111, u);
            return Lerp(Lerp(x00, x10, v), Lerp(x01, x11, v), w) * Perlin3Scale;
        }

        /// <summary>Seeded 2D value noise in [-1, 1). Cheaper and blockier than gradient noise.</summary>
        public static float Value2(uint seed, float x, float y)
        {
            float baseX = math.floor(x), baseY = math.floor(y);
            int ix = (int)baseX, iy = (int)baseY;
            float u = Fade(x - baseX), v = Fade(y - baseY);

            float c00 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy));
            float c10 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy));
            float c01 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy + 1));
            float c11 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy + 1));

            return Lerp(Lerp(c00, c10, u), Lerp(c01, c11, u), v);
        }

        /// <summary>Seeded 3D value noise in [-1, 1).</summary>
        public static float Value3(uint seed, float x, float y, float z)
        {
            float baseX = math.floor(x), baseY = math.floor(y), baseZ = math.floor(z);
            int ix = (int)baseX, iy = (int)baseY, iz = (int)baseZ;
            float u = Fade(x - baseX), v = Fade(y - baseY), w = Fade(z - baseZ);

            float c000 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy, iz));
            float c100 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy, iz));
            float c010 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy + 1, iz));
            float c110 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy + 1, iz));
            float c001 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy, iz + 1));
            float c101 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy, iz + 1));
            float c011 = GenHash.SignedFloat(GenHash.Lattice(seed, ix, iy + 1, iz + 1));
            float c111 = GenHash.SignedFloat(GenHash.Lattice(seed, ix + 1, iy + 1, iz + 1));

            float x00 = Lerp(c000, c100, u), x10 = Lerp(c010, c110, u);
            float x01 = Lerp(c001, c101, u), x11 = Lerp(c011, c111, u);
            return Lerp(Lerp(x00, x10, v), Lerp(x01, x11, v), w);
        }

        private static float Shape(float sample, FbmMode mode)
        {
            switch (mode)
            {
                case FbmMode.Ridged: return 1f - math.abs(sample) * 2f;
                case FbmMode.Billow: return math.abs(sample) * 2f - 1f;
                default: return sample;
            }
        }

        //octaves accumulate in a fixed scalar order and divide by the summed amplitude, so the result
        //stays in [-1, 1] whatever gain the author picked.
        private static uint OctaveSeed(uint seed, int octave) => GenHash.Mix32(seed + (uint)octave * 0x9E3779B1u);

        /// <summary>Fractal sum of 2D gradient noise in [-1, 1].</summary>
        public static float Fbm2(uint seed, float x, float y, int octaves, float lacunarity, float gain, FbmMode mode)
        {
            float sum = 0f, amplitude = 1f, total = 0f, sx = x, sy = y;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += Shape(Perlin2(OctaveSeed(seed, octave), sx, sy), mode) * amplitude;
                total += amplitude;
                amplitude *= gain;
                sx *= lacunarity; sy *= lacunarity;
            }
            return total > 0f ? sum / total : 0f;
        }

        /// <summary>Fractal sum of 3D gradient noise in [-1, 1].</summary>
        public static float Fbm3(uint seed, float x, float y, float z, int octaves, float lacunarity, float gain, FbmMode mode)
        {
            float sum = 0f, amplitude = 1f, total = 0f, sx = x, sy = y, sz = z;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += Shape(Perlin3(OctaveSeed(seed, octave), sx, sy, sz), mode) * amplitude;
                total += amplitude;
                amplitude *= gain;
                sx *= lacunarity; sy *= lacunarity; sz *= lacunarity;
            }
            return total > 0f ? sum / total : 0f;
        }

        /// <summary>Fractal sum of 2D value noise in [-1, 1].</summary>
        public static float FbmValue2(uint seed, float x, float y, int octaves, float lacunarity, float gain, FbmMode mode)
        {
            float sum = 0f, amplitude = 1f, total = 0f, sx = x, sy = y;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += Shape(Value2(OctaveSeed(seed, octave), sx, sy), mode) * amplitude;
                total += amplitude;
                amplitude *= gain;
                sx *= lacunarity; sy *= lacunarity;
            }
            return total > 0f ? sum / total : 0f;
        }

        /// <summary>Fractal sum of 3D value noise in [-1, 1].</summary>
        public static float FbmValue3(uint seed, float x, float y, float z, int octaves, float lacunarity, float gain, FbmMode mode)
        {
            float sum = 0f, amplitude = 1f, total = 0f, sx = x, sy = y, sz = z;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += Shape(Value3(OctaveSeed(seed, octave), sx, sy, sz), mode) * amplitude;
                total += amplitude;
                amplitude *= gain;
                sx *= lacunarity; sy *= lacunarity; sz *= lacunarity;
            }
            return total > 0f ? sum / total : 0f;
        }
    }
}
