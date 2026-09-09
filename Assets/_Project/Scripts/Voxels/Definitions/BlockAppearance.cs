using System;
using System.Collections.Generic;

namespace DigBlocks.Voxels.Definitions
{
    //face order is the canonical axis order used by the compiled tables and, later, by meshing.
    public enum BlockFace : byte { Down = 0, Up = 1, North = 2, South = 3, West = 4, East = 5 }

    public enum BlockRenderLayer : byte { Opaque = 0, Cutout = 1, Transparent = 2 }

    //one renderer material and the texture array it owns. Slice indices in block definitions are raw
    //indices into this material's array, so the array asset and the definitions are authored together.
    public sealed class RenderMaterialDefinition
    {
        public const int MaxSlices = ushort.MaxValue;
        public string Key { get; }
        public BlockRenderLayer RenderLayer { get; }
        public int SliceCount { get; }

        public RenderMaterialDefinition(string key, BlockRenderLayer renderLayer, int sliceCount)
        {
            Key = ResourceKeys.Validate(key, nameof(key));
            if (!Enum.IsDefined(typeof(BlockRenderLayer), renderLayer)) throw new ArgumentOutOfRangeException(nameof(renderLayer));
            if (sliceCount <= 0 || sliceCount > MaxSlices) throw new ArgumentOutOfRangeException(nameof(sliceCount));
            RenderLayer = renderLayer; SliceCount = sliceCount;
        }
    }

    public sealed class FaceAppearanceOverrides
    {
        public int? Texture;
        //quarter turns applied to the face texture, 0-3.
        public byte? Rotation;
        public bool? RandomizeRotation;
        public string TintKey;

        public FaceAppearanceOverrides Clone() => (FaceAppearanceOverrides)MemberwiseClone();

        public void Overlay(FaceAppearanceOverrides other)
        {
            if (other == null) return;
            Texture = other.Texture ?? Texture;
            Rotation = other.Rotation ?? Rotation;
            RandomizeRotation = other.RandomizeRotation ?? RandomizeRotation;
            TintKey = other.TintKey ?? TintKey;
        }
    }

    public sealed class BlockAppearanceOverrides
    {
        public const int FaceCount = 6;
        public string MaterialKey;
        //applies to every face that has no explicit override.
        public int? Texture;
        public byte? Rotation;
        public string TintKey;
        public bool? RandomizeRotation;
        public readonly FaceAppearanceOverrides[] Faces = new FaceAppearanceOverrides[FaceCount];

        public BlockAppearanceOverrides Clone()
        {
            var clone = new BlockAppearanceOverrides
            {
                MaterialKey = MaterialKey, Texture = Texture, Rotation = Rotation,
                TintKey = TintKey, RandomizeRotation = RandomizeRotation
            };
            for (int i = 0; i < FaceCount; i++) clone.Faces[i] = Faces[i]?.Clone();
            return clone;
        }

        //a later layer restating the block-wide texture or tint discards inherited per-face values for that
        //field, so "I set the texture" means every face, not every face the parent left alone.
        public void Overlay(BlockAppearanceOverrides other)
        {
            if (other == null) return;
            MaterialKey = other.MaterialKey ?? MaterialKey;
            if (other.Texture.HasValue) { Texture = other.Texture; ClearFaces(face => face.Texture = null); }
            if (other.Rotation.HasValue) { Rotation = other.Rotation; ClearFaces(face => face.Rotation = null); }
            if (other.TintKey != null) { TintKey = other.TintKey; ClearFaces(face => face.TintKey = null); }
            if (other.RandomizeRotation.HasValue)
            {
                RandomizeRotation = other.RandomizeRotation;
                ClearFaces(face => face.RandomizeRotation = null);
            }
            for (int i = 0; i < FaceCount; i++)
            {
                if (other.Faces[i] == null) continue;
                if (Faces[i] == null) Faces[i] = new FaceAppearanceOverrides();
                Faces[i].Overlay(other.Faces[i]);
            }
        }

        private void ClearFaces(Action<FaceAppearanceOverrides> clear)
        {
            foreach (var face in Faces) if (face != null) clear(face);
        }
    }

    //compiled per-face record; four bytes so a full state costs 24 bytes of face data.
    public readonly struct BlockFaceAppearance
    {
        public readonly ushort Texture;
        private readonly byte rotationAndPolicy;
        public byte Rotation => (byte)(rotationAndPolicy & 3);
        public bool RandomizeRotation => (rotationAndPolicy & 4) != 0;
        //index into the compiled tint-source table; zero means untinted.
        public readonly byte Tint;

        public BlockFaceAppearance(ushort texture, byte rotation, byte tint, bool randomizeRotation = false)
        {
            if (rotation > 3) throw new ArgumentOutOfRangeException(nameof(rotation));
            Texture = texture; rotationAndPolicy = (byte)(rotation | (randomizeRotation ? 4 : 0)); Tint = tint;
        }
    }

    //compiled per-state appearance aligned to a runtime state id. Held as plain data here so the
    //resolution rules stay testable without UnityEngine; the client packs these into native arrays.
    public sealed class BlockStateAppearance
    {
        public static readonly BlockStateAppearance Invisible = new BlockStateAppearance();
        public bool IsVisible { get; }
        public int Material { get; }
        public bool RandomizeRotation { get; }
        public IReadOnlyList<BlockFaceAppearance> Faces { get; }

        private BlockStateAppearance()
        {
            IsVisible = false; Material = 0; RandomizeRotation = false;
            Faces = Array.Empty<BlockFaceAppearance>();
        }

        public BlockStateAppearance(int material, bool randomizeRotation, IReadOnlyList<BlockFaceAppearance> faces)
        {
            if (material < 0) throw new ArgumentOutOfRangeException(nameof(material));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (faces.Count != BlockAppearanceOverrides.FaceCount)
                throw new ArgumentException("Expected one entry per block face.", nameof(faces));
            IsVisible = true; Material = material; RandomizeRotation = randomizeRotation;
            var copy = new BlockFaceAppearance[faces.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = faces[i];
            Faces = Array.AsReadOnly(copy);
        }
    }
}
