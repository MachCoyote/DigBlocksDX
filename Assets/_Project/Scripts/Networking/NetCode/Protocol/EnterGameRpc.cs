using Unity.Entities;
using Unity.NetCode;

namespace DigBlocks.Networking.NetCode
{
    /// <summary>
    /// Asks the server to put this admitted connection in game, which is what starts ghost
    /// replication in both directions. Sent after approval, so this is an ordinary RPC rather than
    /// an approval one.
    /// </summary>
    public struct EnterGameRpc : IRpcCommand { }

    /// <summary>
    /// Present in the client world once the world data a session needs before play has arrived.
    /// Whoever owns world data creates it; the enter-game gate only reads it, so a world with no
    /// chunk streaming simply never opens the gate rather than inferring readiness from silence.
    /// </summary>
    //latching on purpose. Client interest changes whenever the player crosses a chunk boundary, so
    //the underlying store is only momentarily complete; leaving the game on every boundary would be
    //absurd. Readiness means the world has been ready once, not that it is ready right now.
    public struct WorldDataReady : IComponentData { }
}
