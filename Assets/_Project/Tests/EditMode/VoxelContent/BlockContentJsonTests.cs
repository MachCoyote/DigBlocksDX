using DigBlocks.Voxels;
using DigBlocks.Voxels.Content;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;

namespace DigBlocks.Voxels.Content.Tests
{
    public sealed class BlockContentJsonTests
    {
        private const string Material = "{\"key\":\"digblocks:opaque\",\"renderLayer\":\"opaque\",\"slices\":8}";
        private const string Air = "{\"key\":\"digblocks:air\",\"invisible\":true,\"opaque\":false,\"fullCube\":false," +
            "\"collides\":false,\"replaceable\":true,\"permitsFluid\":true,\"hardness\":0}";
        private const string Empty = "{\"key\":\"digblocks:empty\",\"channel\":\"fluid\",\"invisible\":true," +
            "\"opaque\":false,\"fullCube\":false,\"collides\":false,\"hardness\":0}";

        private static CompiledBlockContent Load(params string[] blocks)
        {
            var source = new MemoryBlockContentSource()
                .Add(BlockContentCategory.Materials, "opaque.json", Material)
                .Add(BlockContentCategory.Blocks, "air.json", Air)
                .Add(BlockContentCategory.Blocks, "empty.json", Empty);
            for (int i = 0; i < blocks.Length; i++)
                source.Add(BlockContentCategory.Blocks, "block" + i + ".json", blocks[i]);
            return BlockContentLoader.Load(source);
        }

        [Test]
        public void PerFaceRandomRotationPolicyLoadsAlongsideFixedRotation()
        {
            var content = Load("{\"key\":\"digblocks:grass\",\"material\":\"digblocks:opaque\",\"texture\":1," +
                "\"randomizeRotation\":true,\"randomizeRotations\":{\"side\":false,\"up\":true},\"rotation\":1}");
            var faces = content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:grass")).Faces;
            Assert.That(faces[(int)BlockFace.Up].RandomizeRotation, Is.True);
            Assert.That(faces[(int)BlockFace.Down].RandomizeRotation, Is.True);
            foreach (var direction in new[] { BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East })
            {
                Assert.That(faces[(int)direction].RandomizeRotation, Is.False);
                Assert.That(faces[(int)direction].Rotation, Is.EqualTo(1));
            }
        }

        [Test]
        public void FaceGroupShorthandExpandsBeforeExplicitFaceNames()
        {
            var content = Load("{\"key\":\"digblocks:grass\",\"material\":\"digblocks:opaque\"," +
                "\"textures\":{\"side\":4,\"end\":2,\"up\":3},\"tints\":{\"up\":\"digblocks:grass\"}}");
            var faces = content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:grass")).Faces;
            //"up" wins over "end" because groups are applied first and specific faces override them.
            Assert.That(faces[(int)BlockFace.Up].Texture, Is.EqualTo(3));
            Assert.That(faces[(int)BlockFace.Down].Texture, Is.EqualTo(2));
            Assert.That(faces[(int)BlockFace.North].Texture, Is.EqualTo(4));
            Assert.That(faces[(int)BlockFace.East].Texture, Is.EqualTo(4));
            Assert.That(faces[(int)BlockFace.Up].Tint, Is.EqualTo(1));
            Assert.That(faces[(int)BlockFace.Down].Tint, Is.Zero);
        }

        [Test]
        public void ArchetypesSupplyDefaultsThroughASeparateContentCategory()
        {
            var source = new MemoryBlockContentSource()
                .Add(BlockContentCategory.Materials, "opaque.json", Material)
                .Add(BlockContentCategory.Archetypes, "soil.json",
                    "{\"key\":\"digblocks:soil\",\"material\":\"digblocks:opaque\",\"hardness\":0.5,\"tool\":\"shovel\"}")
                .Add(BlockContentCategory.Blocks, "air.json", Air)
                .Add(BlockContentCategory.Blocks, "empty.json", Empty)
                .Add(BlockContentCategory.Blocks, "dirt.json",
                    "{\"key\":\"digblocks:dirt\",\"archetype\":\"digblocks:soil\",\"texture\":2}");

            var content = BlockContentLoader.Load(source);
            var dirt = content.Registry.GetSolid(content.Registry.LookupSolid("digblocks:dirt"));
            Assert.That(dirt.Attributes.Hardness, Is.EqualTo(0.5f));
            Assert.That(dirt.Attributes.ToolClass, Is.EqualTo(BlockToolClass.Shovel));
            Assert.That(content.SolidAppearanceOf(content.Registry.LookupSolid("digblocks:dirt"))
                .Faces[(int)BlockFace.North].Texture, Is.EqualTo(2));
        }

        [Test]
        public void PropertiesAndStateOverridesRoundTripThroughJson()
        {
            var content = Load("{\"key\":\"digblocks:lamp\",\"material\":\"digblocks:opaque\",\"texture\":1," +
                "\"properties\":{\"lit\":{\"values\":[\"false\",\"true\"],\"default\":\"false\"}}," +
                "\"states\":[{\"when\":{\"lit\":\"true\"},\"lightEmission\":15,\"texture\":2}]}");
            var registry = content.Registry;
            Assert.That(registry.GetSolid(registry.LookupSolid("digblocks:lamp[lit=true]")).Attributes.LightEmission, Is.EqualTo(15));
            Assert.That(registry.GetSolid(registry.LookupSolid("digblocks:lamp[lit=false]")).Attributes.LightEmission, Is.Zero);
            Assert.That(content.SolidAppearanceOf(registry.LookupSolid("digblocks:lamp[lit=true]"))
                .Faces[(int)BlockFace.Up].Texture, Is.EqualTo(2));
        }

        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"texture\":1,\"hardnes\":2}")]
        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"textures\":{\"top\":1}}")]
        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"texture\":\"1\"}")]
        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"texture\":99}")]
        [TestCase("{\"key\":\"Digblocks:Stone\",\"material\":\"digblocks:opaque\",\"texture\":1}")]
        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:missing\",\"texture\":1}")]
        [TestCase("{\"material\":\"digblocks:opaque\",\"texture\":1}")]
        [TestCase("{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"texture\":1,")]
        public void MalformedOrUnknownContentFailsRatherThanLoadingSilently(string block)
        {
            Assert.That(() => Load(block), Throws.InstanceOf<System.Exception>());
        }

        [Test]
        public void LoadFailureNamesTheOriginatingDocument()
        {
            var error = Assert.Throws<BlockContentException>(() => Load(
                "{\"key\":\"digblocks:stone\",\"material\":\"digblocks:opaque\",\"texture\":1,\"wat\":true}"));
            Assert.That(error.ContentKey, Does.Contain("block0.json"));
            Assert.That(error.Message, Does.Contain("wat"));
        }
    }
}
