using DigBlocks.Core.Launch;

namespace DigBlocks.Core.Session
{
    public readonly struct SessionStartRequest
    {
        public SessionStartRequest(LaunchOptions launchOptions, string worldId)
        {
            LaunchOptions = launchOptions;
            WorldId = worldId ?? string.Empty;
        }

        public LaunchOptions LaunchOptions { get; }

        //identifies the world a session should host; the development world until world selection exists
        public string WorldId { get; }
    }
}
