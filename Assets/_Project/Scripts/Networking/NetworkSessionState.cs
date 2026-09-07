namespace DigBlocks.Networking
{
    public enum NetworkSessionState
    {
        Stopped,
        Starting,
        Listening,
        Connecting,
        Approving,
        AwaitingWorldData,
        Stopping,
        Faulted,
        Disconnected
    }
}
