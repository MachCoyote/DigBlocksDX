using System.Collections.Generic;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;
using Unity.Collections;

namespace DigBlocks.Voxels.Appearance.Tests
{
    public sealed class BlockAppearanceTableTests
    {
        private static CompiledBlockContent Content()
        {
            var air = new BlockDefinition { Key = "digblocks:air", Invisible = true };
            air.Attributes = new BlockAttributeOverrides { Opaque = false, FullCube = false, Collides = false, PermitsFluid = true };
            var empty = new BlockDefinition { Key = "digblocks:empty", Channel = BlockChannel.Fluid, Invisible = true };
            empty.Attributes = new BlockAttributeOverrides { Opaque = false, FullCube = false, Collides = false };

            var grass = new BlockDefinition { Key = "digblocks:grass" };
            grass.Appearance = new BlockAppearanceOverrides
            {
                MaterialKey = "digblocks:opaque", Texture = 4, RandomizeRotation = true
            };
            grass.Appearance.Faces[(int)BlockFace.Up] = new FaceAppearanceOverrides
            {
                Texture = 3, Rotation = 2, TintKey = "digblocks:grass"
            };

            return BlockContentCompiler.Compile(
                new[] { new RenderMaterialDefinition("digblocks:opaque", BlockRenderLayer.Opaque, 8) },
                null, new List<BlockDefinition> { air, empty, grass });
        }

        [Test]
        public void PackedTableCarriesPerFaceSlicesRotationAndTintForVisibleStates()
        {
            var content = Content();
            var table = BlockAppearanceTable.Create(content, Allocator.Temp);
            try
            {
                var view = table.AsReadOnly();
                uint grass = content.Registry.LookupSolid("digblocks:grass");
                Assert.That(view.Solid(grass).IsVisible, Is.True);
                Assert.That(view.Solid(grass).RandomizeRotation, Is.True);
                Assert.That(view.Solid(grass).Material, Is.EqualTo(content.MaterialIndexOf("digblocks:opaque")));

                var up = view.SolidFace(grass, BlockFace.Up);
                Assert.That(up.Texture, Is.EqualTo(3));
                Assert.That(up.Rotation, Is.EqualTo(2));
                Assert.That(up.Tint, Is.EqualTo(1));

                var north = view.SolidFace(grass, BlockFace.North);
                Assert.That(north.Texture, Is.EqualTo(4));
                Assert.That(north.Rotation, Is.Zero);
                Assert.That(north.Tint, Is.Zero);
            }
            finally { table.Dispose(); }
        }

        [Test]
        public void InvisibleStatesPackAsEmptyRowsAndBothChannelsAreSized()
        {
            var content = Content();
            var table = BlockAppearanceTable.Create(content, Allocator.Temp);
            try
            {
                var view = table.AsReadOnly();
                Assert.That(view.SolidStateCount, Is.EqualTo(content.Registry.MaxSolidStateId + 1));
                Assert.That(view.FluidStateCount, Is.EqualTo(content.Registry.MaxFluidStateId + 1));
                Assert.That(view.Solid(content.Registry.LookupSolid("digblocks:air")).IsVisible, Is.False);
                Assert.That(view.Fluid(content.Registry.LookupFluid("digblocks:empty")).IsVisible, Is.False);
            }
            finally { table.Dispose(); }
        }

        [Test]
        public void DisposingReleasesBothChannelsAndTheViewRefusesAnUncreatedTable()
        {
            var table = BlockAppearanceTable.Create(Content(), Allocator.Temp);
            table.Dispose();
            Assert.That(table.IsCreated, Is.False);
            Assert.That(() => table.AsReadOnly(), Throws.InvalidOperationException);
            //a second dispose must stay safe; session teardown is not guaranteed to run exactly once.
            Assert.That(() => table.Dispose(), Throws.Nothing);
        }
    }
}
