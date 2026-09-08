using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Definitions
{
    //the compiled result of one content set: a fingerprinted registry shared with the server, plus the
    //client-only appearance rows aligned to runtime state ids. Nothing here references UnityEngine.
    public sealed class CompiledBlockContent
    {
        public BlockRegistry Registry { get; }
        public IReadOnlyList<RenderMaterialDefinition> Materials { get; }
        //index zero is the untinted entry, so a compiled face tint of zero always means "no tint".
        public IReadOnlyList<string> TintKeys { get; }
        public IReadOnlyList<BlockStateAppearance> SolidAppearance { get; }
        public IReadOnlyList<BlockStateAppearance> FluidAppearance { get; }
        private readonly Dictionary<string, int> materialIndices;

        internal CompiledBlockContent(BlockRegistry registry, IReadOnlyList<RenderMaterialDefinition> materials,
            IReadOnlyList<string> tintKeys, IReadOnlyList<BlockStateAppearance> solidAppearance,
            IReadOnlyList<BlockStateAppearance> fluidAppearance)
        {
            Registry = registry; Materials = materials; TintKeys = tintKeys;
            SolidAppearance = solidAppearance; FluidAppearance = fluidAppearance;
            materialIndices = new Dictionary<string, int>(materials.Count, StringComparer.Ordinal);
            for (int i = 0; i < materials.Count; i++) materialIndices.Add(materials[i].Key, i);
        }

        public int MaterialIndexOf(string key) => materialIndices.TryGetValue(key, out int index) ? index : -1;

        //state ids are unsigned throughout the registry, so callers should not have to cast to index these.
        public BlockStateAppearance SolidAppearanceOf(uint stateId) => SolidAppearance[(int)stateId];
        public BlockStateAppearance FluidAppearanceOf(uint stateId) => FluidAppearance[(int)stateId];
    }
}
