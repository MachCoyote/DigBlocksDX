using System;
using System.Text.RegularExpressions;

namespace DigBlocks.Voxels
{
    //shared namespaced-key rules. Definitions, archetypes, materials and state properties all use them,
    //so the grammar lives in one place rather than being restated per layer.
    public static class ResourceKeys
    {
        private static readonly Regex ResourceKey = new Regex(@"\A[a-z0-9_.-]+:[a-z0-9_./-]+\z", RegexOptions.CultureInvariant);
        private static readonly Regex PropertyToken = new Regex(@"\A[a-z0-9_.-]+\z", RegexOptions.CultureInvariant);

        public static bool IsResourceKey(string key) => key != null && ResourceKey.IsMatch(key);
        public static bool IsPropertyToken(string token) => token != null && PropertyToken.IsMatch(token);

        public static string Validate(string key, string parameterName)
        {
            if (!IsResourceKey(key)) throw new ArgumentException("Expected a lowercase namespaced key.", parameterName);
            return key;
        }

        public static string ValidateOptional(string key, string parameterName) =>
            key == null ? null : Validate(key, parameterName);

        public static string ValidateToken(string token, string parameterName)
        {
            if (!IsPropertyToken(token)) throw new ArgumentException("Expected a lowercase token.", parameterName);
            return token;
        }
    }
}
