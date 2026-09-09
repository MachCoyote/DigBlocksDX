using System.Collections.Generic;
using DigBlocks.Client.Rendering;
using DigBlocks.Voxels;
using NUnit.Framework;
using Unity.Mathematics;

namespace DigBlocks.Client.Rendering.Tests
{
    public sealed class ChunkInterestReporterTests
    {
        [Test]
        public void ReportsMathematicallyFlooredChunkChangesAndRetriesRejectedRequests()
        {
            var reported = new List<ChunkAddress>();
            bool accept = false;
            var tracker = new ChunkInterestReporter(3, address =>
            {
                reported.Add(address);
                return accept;
            });

            Assert.That(tracker.Update(new float3(-0.1f, 64, 31.9f)), Is.False);
            accept = true;
            Assert.That(tracker.Update(new float3(-0.1f, 64, 31.9f)), Is.True);
            Assert.That(tracker.Update(new float3(-31.9f, 95.9f, 0)), Is.False);
            Assert.That(tracker.Update(new float3(32, 64, 0)), Is.True);

            Assert.That(reported.Count, Is.EqualTo(3));
            Assert.That(reported[1], Is.EqualTo(new ChunkAddress(3, new int3(-1, 2, 0))));
            Assert.That(reported[2], Is.EqualTo(new ChunkAddress(3, new int3(1, 2, 0))));
        }
    }
}
