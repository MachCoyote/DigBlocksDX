using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// One world's random seed, plus the derivation that gives every layer, band and noise node its
    /// own independent stream. Derivation is by name rather than by index so adding a node to a
    /// generator leaves its siblings' worlds untouched.
    /// </summary>
    public readonly struct GenSeed : IEquatable<GenSeed>
    {
        public readonly ulong Value;

        public GenSeed(ulong value) => Value = value;

        /// <summary>
        /// Turns a player-supplied seed into the seed the world actually uses. Zero means "surprise
        /// me", and resolves once here; the resolved value is what the generator carries and what a
        /// save would record, so a randomized world is still reproducible afterwards.
        /// </summary>
        public static GenSeed Resolve(ulong requested)
        {
            if (requested != 0) return new GenSeed(requested);
            ulong entropy = GenHash.Combine((ulong)DateTime.UtcNow.Ticks, (ulong)Guid.NewGuid().GetHashCode());
            entropy = GenHash.Combine(entropy, (ulong)System.Diagnostics.Stopwatch.GetTimestamp());
            //a resolved seed of zero would re-randomize on the next load, so nudge it off zero.
            return new GenSeed(entropy == 0 ? 0x9E3779B97F4A7C15UL : entropy);
        }

        /// <summary>Parses a player-entered seed. Text that is not a number hashes to one, like Minecraft.</summary>
        public static GenSeed Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Resolve(0);
            string trimmed = text.Trim();
            if (long.TryParse(trimmed, out long signed)) return Resolve(unchecked((ulong)signed));
            if (ulong.TryParse(trimmed, out ulong unsigned)) return Resolve(unsigned);
            return new GenSeed(GenHash.Text(trimmed));
        }

        public GenSeed Derive(ulong tag) => new GenSeed(GenHash.Combine(Value, tag));
        public GenSeed Derive(string name) => Derive(GenHash.Text(name));
        public GenSeed Derive(string name, int ordinal) => Derive(name).Derive((ulong)(long)ordinal);

        /// <summary>The 32-bit form the noise lattice hashes take.</summary>
        public uint Lattice => (uint)(Value ^ (Value >> 32));

        public bool Equals(GenSeed other) => Value == other.Value;
        public override bool Equals(object obj) => obj is GenSeed other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
    }
}
