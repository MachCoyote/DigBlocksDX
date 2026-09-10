using System.IO;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace DigBlocks.Voxels.Content.Tests
{
    //guards the content the game actually ships. These assertions are the contract a content edit has to
    //update deliberately, which is what stops a stray slice or hardness change from going unnoticed.
    public sealed class ShippedBlockContentTests
    {
        private static CompiledBlockContent Load() => BlockContentLoader.LoadFromDirectory(
            Path.Combine(Application.streamingAssetsPath, BlockContentLoader.DefaultContentFolder));

        [Test]
        public void ShippedContentCompilesWithReservedIdsFirstAndRemainingStatesByOrdinalKey()
        {
            var registry = Load().Registry;
            Assert.That(registry.LookupSolid("digblocks:air"), Is.Zero);
            Assert.That(registry.LookupFluid("digblocks:empty"), Is.Zero);
            Assert.That(registry.LookupSolid("digblocks:bedrock"), Is.EqualTo(1));
            Assert.That(registry.LookupSolid("digblocks:cobblestone"), Is.EqualTo(2));
            Assert.That(registry.LookupSolid("digblocks:dirt"), Is.EqualTo(3));
            Assert.That(registry.LookupSolid("digblocks:grass_block"), Is.EqualTo(4));
            Assert.That(registry.LookupSolid("digblocks:gravel"), Is.EqualTo(5));
            Assert.That(registry.LookupSolid("digblocks:sand"), Is.EqualTo(6));
            Assert.That(registry.LookupSolid("digblocks:stone"), Is.EqualTo(7));
            Assert.That(registry.LookupSolid("digblocks:testblock"), Is.EqualTo(8));
            Assert.That(registry.MaxSolidStateId, Is.EqualTo(8));
            //water is the first authored fluid, after the reserved empty state.
            Assert.That(registry.LookupFluid("digblocks:water"), Is.EqualTo(1));
            Assert.That(registry.MaxFluidStateId, Is.EqualTo(1));
            Assert.That(registry.Fingerprint, Does.Match("^[0-9a-f]{64}$"));
        }

        [Test]
        public void ArchetypesResolveTheSimulationAttributesOfEachShippedBlock()
        {
            var registry = Load().Registry;
            BlockAttributes Of(string key) => registry.GetSolid(registry.LookupSolid(key)).Attributes;

            var air = Of("digblocks:air");
            Assert.That(air.Has(BlockFlags.Opaque), Is.False);
            Assert.That(air.Has(BlockFlags.Collides), Is.False);
            Assert.That(air.Has(BlockFlags.Replaceable | BlockFlags.PermitsFluid), Is.True);
            //a non-opaque block does not attenuate light unless it says so.
            Assert.That(air.LightAttenuation, Is.Zero);

            var dirt = Of("digblocks:dirt");
            Assert.That(dirt.Hardness, Is.EqualTo(0.5f));
            Assert.That(dirt.ToolClass, Is.EqualTo(BlockToolClass.Shovel));
            Assert.That(dirt.Has(BlockFlags.Opaque | BlockFlags.FullCube | BlockFlags.Collides), Is.True);
            Assert.That(dirt.LightAttenuation, Is.EqualTo(BlockAttributes.MaxLight));

            var stone = Of("digblocks:stone");
            Assert.That(stone.Hardness, Is.EqualTo(1.5f));
            Assert.That(stone.ToolClass, Is.EqualTo(BlockToolClass.Pickaxe));
            Assert.That(stone.Has(BlockFlags.RequiresTool), Is.True);
            Assert.That(stone.BlastResistance, Is.EqualTo(6f));

            var bedrock = Of("digblocks:bedrock");
            Assert.That(bedrock.Has(BlockFlags.Unbreakable), Is.True);
            Assert.That(bedrock.BlastResistance, Is.GreaterThan(stone.BlastResistance));

            //the testblock inherits the generic solid archetype rather than a family, so it keeps the defaults.
            Assert.That(Of("digblocks:testblock").Hardness, Is.EqualTo(1f));
            Assert.That(registry.GetSolid(registry.LookupSolid("digblocks:testblock")).Tags,
                Is.EqualTo(new[] { "digblocks:debug", "digblocks:mineable" }));
            Assert.That(Of("digblocks:grass_block").Has(BlockFlags.RandomTicks), Is.True);
        }

        [Test]
        public void ShippedAppearanceUsesTheAuthoredTextureArraySlices()
        {
            var content = Load();
            var registry = content.Registry;
            Assert.That(content.Materials.Count, Is.EqualTo(1));
            Assert.That(content.Materials[0].Key, Is.EqualTo("digblocks:opaque"));
            Assert.That(content.Materials[0].SliceCount, Is.EqualTo(32));

            ushort Slice(string key, BlockFace face) =>
                content.SolidAppearanceOf(registry.LookupSolid(key)).Faces[(int)face].Texture;

            Assert.That(Slice("digblocks:testblock", BlockFace.North), Is.EqualTo(0));
            Assert.That(Slice("digblocks:stone", BlockFace.North), Is.EqualTo(4));
            Assert.That(Slice("digblocks:dirt", BlockFace.North), Is.EqualTo(3));
            Assert.That(Slice("digblocks:grass_block", BlockFace.Up), Is.EqualTo(1));
            Assert.That(Slice("digblocks:grass_block", BlockFace.North), Is.EqualTo(2));
            Assert.That(Slice("digblocks:grass_block", BlockFace.Down), Is.EqualTo(3));
            Assert.That(Slice("digblocks:bedrock", BlockFace.North), Is.EqualTo(5));
            Assert.That(Slice("digblocks:gravel", BlockFace.North), Is.EqualTo(13));
            Assert.That(Slice("digblocks:sand", BlockFace.North), Is.EqualTo(14));
            Assert.That(Slice("digblocks:cobblestone", BlockFace.North), Is.EqualTo(8));

            //water is generated into the fluid channel but nothing meshes fluids yet, so it carries no
            //appearance. Giving it one now would fail the renderer's opaque-only material check.
            Assert.That(content.FluidAppearanceOf(registry.LookupFluid("digblocks:water")).IsVisible, Is.False);

            //only the grass top is tinted, and air carries no appearance row at all.
            var grass = content.SolidAppearanceOf(registry.LookupSolid("digblocks:grass_block"));
            Assert.That(grass.Faces[(int)BlockFace.Up].Tint, Is.EqualTo(1));
            Assert.That(grass.Faces[(int)BlockFace.North].Tint, Is.Zero);
            Assert.That(grass.Faces[(int)BlockFace.Up].RandomizeRotation, Is.True);
            Assert.That(grass.Faces[(int)BlockFace.Down].RandomizeRotation, Is.True);
            Assert.That(grass.Faces[(int)BlockFace.North].RandomizeRotation, Is.False);
            Assert.That(content.SolidAppearanceOf(registry.LookupSolid("digblocks:air")).IsVisible, Is.False);
        }

        [Test]
        public void ShippedContentProducesAnAttributeTableForJobs()
        {
            var registry = Load().Registry;
            var table = registry.CreateAttributeTable(Allocator.Temp);
            try
            {
                var view = table.AsReadOnly();
                Assert.That(view.Solids.Length, Is.EqualTo(registry.MaxSolidStateId + 1));
                Assert.That(view.Fluids.Length, Is.EqualTo(registry.MaxFluidStateId + 1));
                Assert.That(view.Solid(registry.LookupSolid("digblocks:bedrock")).Has(BlockFlags.Unbreakable), Is.True);
            }
            finally { table.Dispose(); }
        }
    }
}
