namespace DigBlocks.Core.Diagnostics
{
    //well-known toggles whose state is read outside the assembly that declares the keybind
    public static class DebugToggleIds
    {
        public static readonly DebugToggleId Wireframe = new DebugToggleId("Wireframe");

        public static readonly DebugToggleId Fullbright = new DebugToggleId("Fullbright");

        public static readonly DebugToggleId Overdraw = new DebugToggleId("Overdraw");

        public static readonly DebugToggleId SecondaryCameraCulling = new DebugToggleId("SecondaryCameraCulling");

        //single player only: hand chunks straight from the server store to the client store instead of
        //encoding, slicing and decoding them. Off by default so ordinary play exercises the wire path.
        public static readonly DebugToggleId DirectChunkDelivery = new DebugToggleId("DirectChunkDelivery");
    }
}
