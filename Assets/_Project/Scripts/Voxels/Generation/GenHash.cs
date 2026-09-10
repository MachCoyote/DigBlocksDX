namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// Every seed, sub-seed and noise lattice gradient in world generation comes from here.
    /// <para>
    /// All of it is integer arithmetic on fixed-width types, so a world seed reproduces
    /// bit-identically on any platform, backend and Burst version. Nothing in this type may ever
    /// take a float path.
    /// </para>
    /// </summary>
    public static class GenHash
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>Mixes one 64-bit value. This is the standard splitmix64 finalizer.</summary>
        public static ulong SplitMix64(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            ulong mixed = value;
            mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
            mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;
            return mixed ^ (mixed >> 31);
        }

        /// <summary>Combines two 64-bit values into one well-mixed value.</summary>
        public static ulong Combine(ulong left, ulong right) => SplitMix64(left ^ SplitMix64(right));

        /// <summary>
        /// FNV-1a over the string's UTF-16 code units. Names rather than positions seed noise nodes,
        /// so inserting a node into a generator does not reshuffle every other node's seed and
        /// silently change every existing world.
        /// </summary>
        public static ulong Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return SplitMix64(FnvOffset);
            ulong hash = FnvOffset;
            for (int index = 0; index < text.Length; index++)
            {
                char unit = text[index];
                hash = (hash ^ (byte)(unit & 0xFF)) * FnvPrime;
                hash = (hash ^ (byte)(unit >> 8)) * FnvPrime;
            }
            return hash;
        }

        /// <summary>Mixes one 32-bit value. Murmur3's finalizer, used to close the lattice hashes.</summary>
        public static uint Mix32(uint value)
        {
            value ^= value >> 16;
            value *= 0x85EBCA6Bu;
            value ^= value >> 13;
            value *= 0xC2B2AE35u;
            value ^= value >> 16;
            return value;
        }

        /// <summary>Hashes an integer lattice point in two dimensions to a well-distributed value.</summary>
        public static uint Lattice(uint seed, int x, int y)
            => Mix32(seed ^ ((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u));

        /// <summary>Hashes an integer lattice point in three dimensions to a well-distributed value.</summary>
        public static uint Lattice(uint seed, int x, int y, int z)
            => Mix32(seed ^ ((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u) ^ ((uint)z * 0xC2B2AE3Du));

        /// <summary>
        /// A hashed value in [0, 1). Built from the top 24 bits so the result is an exactly
        /// representable multiple of 2^-24 and carries no rounding of its own.
        /// </summary>
        public static float UnitFloat(uint hash) => (hash >> 8) * (1f / 16777216f);

        /// <summary>A hashed value in [-1, 1), on the same exact grid as <see cref="UnitFloat"/>.</summary>
        public static float SignedFloat(uint hash) => UnitFloat(hash) * 2f - 1f;
    }
}
