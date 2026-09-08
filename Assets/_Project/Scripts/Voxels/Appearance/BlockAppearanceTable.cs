using System;
using DigBlocks.Voxels.Definitions;
using Unity.Collections;

namespace DigBlocks.Voxels.Appearance
{
    //per-state render row. Two bytes so the state array stays small enough to keep resident for the
    //whole client session; the six face records live in a parallel array indexed by state id.
    public readonly struct BlockStateRender
    {
        public const byte VisibleFlag = 1 << 0;
        public const byte RandomizeRotationFlag = 1 << 1;

        public readonly byte Material;
        public readonly byte Flags;

        public BlockStateRender(byte material, byte flags) { Material = material; Flags = flags; }

        public bool IsVisible => (Flags & VisibleFlag) != 0;
        public bool RandomizeRotation => (Flags & RandomizeRotationFlag) != 0;
    }

    //client-only compiled appearance, packed for meshing jobs. This assembly is deliberately separate from
    //DigBlocks.Voxels so a dedicated server build carries no render data and no texture-array bindings.
    public struct BlockAppearanceTable : IDisposable
    {
        private NativeArray<BlockStateRender> solidStates, fluidStates;
        private NativeArray<BlockFaceAppearance> solidFaces, fluidFaces;
        public bool IsCreated => solidStates.IsCreated;

        public static BlockAppearanceTable Create(CompiledBlockContent content, Allocator allocator)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            var table = default(BlockAppearanceTable);
            Pack(content.SolidAppearance, allocator, out table.solidStates, out table.solidFaces);
            Pack(content.FluidAppearance, allocator, out table.fluidStates, out table.fluidFaces);
            return table;
        }

        private static void Pack(System.Collections.Generic.IReadOnlyList<BlockStateAppearance> source, Allocator allocator,
            out NativeArray<BlockStateRender> states, out NativeArray<BlockFaceAppearance> faces)
        {
            int faceCount = BlockAppearanceOverrides.FaceCount;
            states = new NativeArray<BlockStateRender>(source.Count, allocator, NativeArrayOptions.UninitializedMemory);
            faces = new NativeArray<BlockFaceAppearance>(source.Count * faceCount, allocator, NativeArrayOptions.ClearMemory);
            for (int state = 0; state < source.Count; state++)
            {
                var appearance = source[state];
                if (!appearance.IsVisible) { states[state] = default; continue; }
                if (appearance.Material > byte.MaxValue)
                    throw new BlockContentException(null, "More render materials than the compiled table can index.");
                byte flags = BlockStateRender.VisibleFlag;
                if (appearance.RandomizeRotation) flags |= BlockStateRender.RandomizeRotationFlag;
                states[state] = new BlockStateRender((byte)appearance.Material, flags);
                for (int face = 0; face < faceCount; face++) faces[state * faceCount + face] = appearance.Faces[face];
            }
        }

        public ReadOnly AsReadOnly()
        {
            if (!IsCreated) throw new InvalidOperationException("Appearance table has not been created.");
            return new ReadOnly(solidStates.AsReadOnly(), solidFaces.AsReadOnly(),
                fluidStates.AsReadOnly(), fluidFaces.AsReadOnly());
        }

        public void Dispose()
        {
            if (solidStates.IsCreated) solidStates.Dispose();
            if (solidFaces.IsCreated) solidFaces.Dispose();
            if (fluidStates.IsCreated) fluidStates.Dispose();
            if (fluidFaces.IsCreated) fluidFaces.Dispose();
            this = default;
        }

        //Burst-friendly value copy; safe to capture inside meshing jobs.
        public readonly struct ReadOnly
        {
            private readonly NativeArray<BlockStateRender>.ReadOnly solidStates, fluidStates;
            private readonly NativeArray<BlockFaceAppearance>.ReadOnly solidFaces, fluidFaces;

            internal ReadOnly(NativeArray<BlockStateRender>.ReadOnly solidStates, NativeArray<BlockFaceAppearance>.ReadOnly solidFaces,
                NativeArray<BlockStateRender>.ReadOnly fluidStates, NativeArray<BlockFaceAppearance>.ReadOnly fluidFaces)
            {
                this.solidStates = solidStates; this.solidFaces = solidFaces;
                this.fluidStates = fluidStates; this.fluidFaces = fluidFaces;
            }

            public int SolidStateCount => solidStates.Length;
            public int FluidStateCount => fluidStates.Length;
            public BlockStateRender Solid(uint stateId) => solidStates[(int)stateId];
            public BlockStateRender Fluid(uint stateId) => fluidStates[(int)stateId];

            public BlockFaceAppearance SolidFace(uint stateId, BlockFace face) =>
                solidFaces[(int)stateId * BlockAppearanceOverrides.FaceCount + (int)face];

            public BlockFaceAppearance FluidFace(uint stateId, BlockFace face) =>
                fluidFaces[(int)stateId * BlockAppearanceOverrides.FaceCount + (int)face];
        }
    }
}
