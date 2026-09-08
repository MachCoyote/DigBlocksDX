using System.Collections.Generic;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;
using Unity.Collections;

namespace DigBlocks.Voxels.Tests
{
    public sealed class BlockContentCompilerTests
    {
        private static RenderMaterialDefinition Opaque(int slices = 8) =>
            new RenderMaterialDefinition("digblocks:opaque", BlockRenderLayer.Opaque, slices);

        private static BlockDefinition Reserved(string key, BlockChannel channel)
        {
            var block = new BlockDefinition { Key = key, Channel = channel, Invisible = true };
            block.Attributes = new BlockAttributeOverrides { Opaque = false, FullCube = false, Collides = false, PermitsFluid = true };
            return block;
        }

        private static BlockDefinition Visible(string key, int texture, string archetype = null)
        {
            var block = new BlockDefinition { Key = key, ArchetypeKey = archetype };
            block.Appearance = new BlockAppearanceOverrides { MaterialKey = "digblocks:opaque", Texture = texture };
            return block;
        }

        private static List<BlockDefinition> WithReserved(params BlockDefinition[] blocks)
        {
            var all = new List<BlockDefinition>
            {
                Reserved("digblocks:air", BlockChannel.Solid),
                Reserved("digblocks:empty", BlockChannel.Fluid)
            };
            all.AddRange(blocks);
            return all;
        }

        private static CompiledBlockContent Compile(IEnumerable<BlockArchetype> archetypes, params BlockDefinition[] blocks) =>
            BlockContentCompiler.Compile(new[] { Opaque() }, archetypes, WithReserved(blocks));

        [Test]
        public void ArchetypeChainResolvesNearestLayerFirstAndUnionsTags()
        {
            var root = new BlockArchetype { Key = "digblocks:solid" };
            root.Attributes = new BlockAttributeOverrides { Hardness = 1f, ToolClass = BlockToolClass.Pickaxe, LightEmission = 3 };
            root.Tags.Add("digblocks:mineable");
            var child = new BlockArchetype { Key = "digblocks:rock", ArchetypeKey = "digblocks:solid" };
            child.Attributes = new BlockAttributeOverrides { Hardness = 1.5f };
            child.Tags.Add("digblocks:rock");

            var stone = Visible("digblocks:stone", 1, "digblocks:rock");
            stone.Attributes = new BlockAttributeOverrides { LightEmission = 7 };
            stone.Tags.Add("digblocks:stone");

            var content = Compile(new[] { root, child }, stone);
            var state = content.Registry.GetSolid(content.Registry.LookupSolid("digblocks:stone"));
            //the nearest layer that sets a field wins; unset fields keep falling through to the root.
            Assert.That(state.Attributes.Hardness, Is.EqualTo(1.5f));
            Assert.That(state.Attributes.ToolClass, Is.EqualTo(BlockToolClass.Pickaxe));
            Assert.That(state.Attributes.LightEmission, Is.EqualTo(7));
            Assert.That(state.Tags, Is.EqualTo(new[] { "digblocks:mineable", "digblocks:rock", "digblocks:stone" }));
        }

        [Test]
        public void BlockWideTextureReplacesInheritedPerFaceTextures()
        {
            var archetype = new BlockArchetype { Key = "digblocks:grassy" };
            archetype.Appearance = new BlockAppearanceOverrides { MaterialKey = "digblocks:opaque", Texture = 2 };
            archetype.Appearance.Faces[(int)BlockFace.Up] = new FaceAppearanceOverrides { Texture = 3 };

            var inherits = new BlockDefinition { Key = "digblocks:grass", ArchetypeKey = "digblocks:grassy" };
            var overrides = new BlockDefinition { Key = "digblocks:dirt", ArchetypeKey = "digblocks:grassy" };
            overrides.Appearance = new BlockAppearanceOverrides { Texture = 5 };

            var content = Compile(new[] { archetype }, inherits, overrides);
            var grass = content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:grass"));
            var dirt = content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:dirt"));
            Assert.That(grass.Faces[(int)BlockFace.Up].Texture, Is.EqualTo(3));
            Assert.That(grass.Faces[(int)BlockFace.North].Texture, Is.EqualTo(2));
            //restating the block-wide texture means every face, including one the archetype had specialised.
            Assert.That(dirt.Faces[(int)BlockFace.Up].Texture, Is.EqualTo(5));
            Assert.That(dirt.Faces[(int)BlockFace.North].Texture, Is.EqualTo(5));
        }

        [Test]
        public void PropertiesExpandIntoCanonicalStateKeysAndStateOverridesNarrowToMatches()
        {
            var log = Visible("digblocks:oak_log", 1);
            log.Properties.Add(new BlockProperty("axis", new[] { "y", "x", "z" }));
            log.Properties.Add(new BlockProperty("lit", new[] { "false", "true" }));
            log.StateOverrides.Add(new BlockStateOverride(new Dictionary<string, string> { { "lit", "true" } },
                new BlockAttributeOverrides { LightEmission = 13 }));

            var content = Compile(null, log);
            var registry = content.Registry;
            //properties sort by name, so the canonical key orders axis before lit regardless of declaration order.
            uint litY = registry.LookupSolid("digblocks:oak_log[axis=y,lit=true]");
            uint darkX = registry.LookupSolid("digblocks:oak_log[lit=false,axis=x]");
            Assert.That(registry.GetSolid(litY).Attributes.LightEmission, Is.EqualTo(13));
            Assert.That(registry.GetSolid(darkX).Attributes.LightEmission, Is.Zero);
            Assert.That(registry.MaxSolidStateId, Is.EqualTo(6));
        }

        [Test]
        public void AppearanceIsExcludedFromTheFingerprintWhileAttributesAreCovered()
        {
            CompiledBlockContent Build(int texture, float hardness)
            {
                var stone = Visible("digblocks:stone", texture);
                stone.Attributes = new BlockAttributeOverrides { Hardness = hardness };
                return Compile(null, stone);
            }

            string baseline = Build(1, 1.5f).Registry.Fingerprint;
            //a client texture difference must never fail companion binding.
            Assert.That(Build(4, 1.5f).Registry.Fingerprint, Is.EqualTo(baseline));
            //a simulation difference is a genuine desync and must fail it.
            Assert.That(Build(1, 2f).Registry.Fingerprint, Is.Not.EqualTo(baseline));
        }

        [Test]
        public void AttributeTableIsAlignedToRuntimeStateIds()
        {
            var stone = Visible("digblocks:stone", 1);
            stone.Attributes = new BlockAttributeOverrides { Hardness = 1.5f, Unbreakable = true };
            var registry = Compile(null, stone).Registry;

            var table = registry.CreateAttributeTable(Allocator.Temp);
            try
            {
                var view = table.AsReadOnly();
                for (uint id = 0; id <= registry.MaxSolidStateId; id++)
                    Assert.That(view.Solid(id), Is.EqualTo(registry.GetSolid(id).Attributes));
                Assert.That(view.Solid(registry.LookupSolid("digblocks:air")).Has(BlockFlags.Opaque), Is.False);
                Assert.That(view.Solid(registry.LookupSolid("digblocks:stone")).Has(BlockFlags.Unbreakable), Is.True);
            }
            finally { table.Dispose(); }
        }

        [Test]
        public void InvisibleBlocksSkipAppearanceWhileVisibleBlocksRequireAMaterial()
        {
            var content = Compile(null, Visible("digblocks:stone", 1));
            Assert.That(content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:air")).IsVisible, Is.False);
            Assert.That(content.FluidAppearanceOf(content.Registry.LookupFluid("digblocks:empty")).IsVisible, Is.False);

            var untextured = new BlockDefinition { Key = "digblocks:stone" };
            Assert.That(() => Compile(null, untextured), Throws.TypeOf<BlockContentException>());
        }

        [Test]
        public void CompilerRejectsUnknownArchetypesCyclesAndOutOfRangeSlices()
        {
            Assert.That(() => Compile(null, Visible("digblocks:stone", 1, "digblocks:missing")),
                Throws.TypeOf<BlockContentException>());

            var left = new BlockArchetype { Key = "digblocks:left", ArchetypeKey = "digblocks:right" };
            var right = new BlockArchetype { Key = "digblocks:right", ArchetypeKey = "digblocks:left" };
            Assert.That(() => Compile(new[] { left, right }, Visible("digblocks:stone", 1, "digblocks:left")),
                Throws.TypeOf<BlockContentException>());

            Assert.That(() => BlockContentCompiler.Compile(new[] { Opaque(4) }, null,
                WithReserved(Visible("digblocks:stone", 4))), Throws.TypeOf<BlockContentException>());
        }

        [Test]
        public void ExpansionRejectsBlocksBeyondTheStateCap()
        {
            var block = Visible("digblocks:huge", 1);
            for (int i = 0; i < 3; i++)
            {
                var values = new List<string>();
                for (int value = 0; value < 8; value++) values.Add("v" + value);
                block.Properties.Add(new BlockProperty("p" + i, values));
            }
            Assert.That(() => Compile(null, block), Throws.TypeOf<BlockContentException>()
                .With.Message.Contains(BlockDefinition.MaxStates.ToString()));
        }

        [Test]
        public void MaterialsSortOpaqueFirstAndTintZeroMeansUntinted()
        {
            var transparent = new RenderMaterialDefinition("digblocks:glass", BlockRenderLayer.Transparent, 4);
            var grass = Visible("digblocks:grass", 3);
            grass.Appearance.Faces[(int)BlockFace.Up] = new FaceAppearanceOverrides { TintKey = "digblocks:grass" };

            var content = BlockContentCompiler.Compile(new[] { transparent, Opaque() }, null, WithReserved(grass));
            Assert.That(content.Materials[0].Key, Is.EqualTo("digblocks:opaque"));
            Assert.That(content.MaterialIndexOf("digblocks:glass"), Is.EqualTo(1));

            var faces = content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:grass")).Faces;
            Assert.That(faces[(int)BlockFace.Up].Tint, Is.EqualTo(1));
            Assert.That(faces[(int)BlockFace.North].Tint, Is.Zero);
            Assert.That(content.TintKeys[0], Is.Null);
            Assert.That(content.TintKeys[1], Is.EqualTo("digblocks:grass"));
        }
    }
}
