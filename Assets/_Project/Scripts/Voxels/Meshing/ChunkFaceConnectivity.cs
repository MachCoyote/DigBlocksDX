using DigBlocks.Voxels.Definitions;

namespace DigBlocks.Voxels.Meshing
{
    public readonly struct ChunkFaceConnectivity
    {
        public const int FaceCount = 6;
        public const ulong ValidBits = (1ul << (FaceCount * FaceCount)) - 1;

        public ulong Bits { get; }

        public ChunkFaceConnectivity(ulong bits) => Bits = bits & ValidBits;

        public bool Connects(BlockFace incoming, BlockFace outgoing) =>
            (Bits & (1ul << ((int)incoming * FaceCount + (int)outgoing))) != 0;

        public byte Outgoing(byte incomingFaces)
        {
            byte outgoing = 0;
            for (int incoming = 0; incoming < FaceCount; incoming++)
                if ((incomingFaces & (1 << incoming)) != 0)
                    outgoing |= (byte)(Bits >> (incoming * FaceCount));
            return (byte)(outgoing & 0x3f);
        }

        public static BlockFace Opposite(BlockFace face) => (BlockFace)((int)face ^ 1);
    }
}
