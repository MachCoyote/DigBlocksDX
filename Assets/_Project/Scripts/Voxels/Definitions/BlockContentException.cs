using System;

namespace DigBlocks.Voxels.Definitions
{
    //carries the offending content key so a load failure names the file an author has to fix.
    public sealed class BlockContentException : Exception
    {
        public string ContentKey { get; }

        public BlockContentException(string contentKey, string message, Exception inner = null)
            : base(contentKey == null ? message : contentKey + ": " + message, inner)
        {
            ContentKey = contentKey;
        }
    }
}
