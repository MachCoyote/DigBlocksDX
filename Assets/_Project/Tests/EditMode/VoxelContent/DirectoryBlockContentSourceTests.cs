using System;
using System.IO;
using System.Linq;
using DigBlocks.Voxels.Definitions;
using NUnit.Framework;

namespace DigBlocks.Voxels.Content.Tests
{
    public sealed class DirectoryBlockContentSourceTests
    {
        private string directory;

        [SetUp]
        public void CreateTempDirectory() =>
            Directory.CreateDirectory(directory = Path.Combine(Path.GetTempPath(), "DigBlocksContent-" + Guid.NewGuid()));

        [TearDown]
        public void DeleteTempDirectory()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private string WriteBlocks(string fileName, string json)
        {
            string folder = Path.Combine(directory, "blocks");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, fileName);
            File.WriteAllText(path, json);
            return path;
        }

        [Test]
        public void SingleObjectFileYieldsOneDocumentNamedByFile()
        {
            WriteBlocks("dirt.json", "{\"key\":\"digblocks:dirt\"}");
            var documents = new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks).ToList();
            Assert.That(documents.Count, Is.EqualTo(1));
            Assert.That(documents[0].Origin, Is.EqualTo("dirt.json"));
            Assert.That(documents[0].Json, Is.EqualTo("{\"key\":\"digblocks:dirt\"}"));
        }

        [Test]
        public void ArrayFileYieldsOneDocumentPerEntryInArrayOrder()
        {
            WriteBlocks("wood.json",
                "[{\"key\":\"digblocks:oak_log\"},{\"key\":\"digblocks:birch_log\"},{\"key\":\"digblocks:spruce_log\"}]");
            var documents = new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks).ToList();
            Assert.That(documents.Select(document => document.Origin), Is.EqualTo(new[]
            {
                "wood.json[digblocks:oak_log]", "wood.json[digblocks:birch_log]", "wood.json[digblocks:spruce_log]"
            }));
        }

        [Test]
        public void ArrayEntriesInterleaveWithOtherFilesByFileNameOrdinal()
        {
            WriteBlocks("a_single.json", "{\"key\":\"digblocks:a\"}");
            WriteBlocks("b_pair.json", "[{\"key\":\"digblocks:b1\"},{\"key\":\"digblocks:b2\"}]");
            WriteBlocks("c_single.json", "{\"key\":\"digblocks:c\"}");
            var origins = new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks)
                .Select(document => document.Origin).ToList();
            Assert.That(origins, Is.EqualTo(new[]
            {
                "a_single.json", "b_pair.json[digblocks:b1]", "b_pair.json[digblocks:b2]", "c_single.json"
            }));
        }

        [Test]
        public void ArrayEntryWithoutAKeyIsNamedByIndex()
        {
            WriteBlocks("wood.json", "[{\"texture\":1},{\"texture\":2}]");
            var origins = new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks)
                .Select(document => document.Origin).ToList();
            Assert.That(origins, Is.EqualTo(new[] { "wood.json[0]", "wood.json[1]" }));
        }

        [Test]
        public void ArrayEntryThatIsNotAnObjectFailsNamingTheFileAndIndex()
        {
            WriteBlocks("wood.json", "[{\"key\":\"digblocks:oak_log\"},\"nope\"]");
            var error = Assert.Throws<BlockContentException>(() =>
                new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks).ToList());
            Assert.That(error.Message, Does.Contain("wood.json[1]"));
        }

        [Test]
        public void MalformedJsonFailsNamingTheFile()
        {
            WriteBlocks("wood.json", "[{\"key\":\"digblocks:oak_log\"},");
            var error = Assert.Throws<BlockContentException>(() =>
                new DirectoryBlockContentSource(directory).Read(BlockContentCategory.Blocks).ToList());
            Assert.That(error.Message, Does.Contain("wood.json"));
        }

        [Test]
        public void EndToEndLoadCompilesBlocksAuthoredAsAnArrayInOneFile()
        {
            Directory.CreateDirectory(Path.Combine(directory, "materials"));
            File.WriteAllText(Path.Combine(directory, "materials", "opaque.json"),
                "{\"key\":\"digblocks:opaque\",\"renderLayer\":\"opaque\",\"slices\":4}");
            WriteBlocks("air.json", "{\"key\":\"digblocks:air\",\"invisible\":true,\"opaque\":false,\"fullCube\":false," +
                "\"collides\":false,\"replaceable\":true,\"permitsFluid\":true,\"hardness\":0}");
            WriteBlocks("empty.json", "{\"key\":\"digblocks:empty\",\"channel\":\"fluid\",\"invisible\":true," +
                "\"opaque\":false,\"fullCube\":false,\"collides\":false,\"hardness\":0}");
            WriteBlocks("wood.json", "[" +
                "{\"key\":\"digblocks:oak_log\",\"material\":\"digblocks:opaque\",\"texture\":0}," +
                "{\"key\":\"digblocks:birch_log\",\"material\":\"digblocks:opaque\",\"texture\":1}" +
                "]");

            var content = BlockContentLoader.LoadFromDirectory(directory);
            //final ids sort ordinally by key, not by position within the array, so birch precedes oak.
            Assert.That(content.Registry.LookupSolid("digblocks:oak_log"), Is.GreaterThan(0));
            Assert.That(content.Registry.LookupSolid("digblocks:birch_log"),
                Is.LessThan(content.Registry.LookupSolid("digblocks:oak_log")));
        }
    }
}
