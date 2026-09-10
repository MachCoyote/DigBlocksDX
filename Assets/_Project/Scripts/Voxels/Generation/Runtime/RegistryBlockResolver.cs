using System;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>
    /// Resolves the block keys generation content is authored against into the state ids it writes.
    /// <para>
    /// Failures are turned into content errors naming the key, because a generator naming a block that
    /// does not exist is an authoring mistake and the registry's own exception does not say which
    /// generator asked.
    /// </para>
    /// </summary>
    public sealed class RegistryBlockResolver : IBlockResolver
    {
        private readonly BlockRegistry registry;

        public RegistryBlockResolver(BlockRegistry registry)
            => this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

        public uint Solid(string key)
        {
            try { return registry.LookupSolid(key); }
            catch (Exception exception)
            {
                throw new GenerationContentException($"Generation names the block '{key}', which no shipped content defines.", exception);
            }
        }

        public uint Fluid(string key)
        {
            try { return registry.LookupFluid(key); }
            catch (Exception exception)
            {
                throw new GenerationContentException($"Generation names the fluid '{key}', which no shipped content defines.", exception);
            }
        }
    }
}
