using System;

namespace DigBlocks.Core.Session
{
    public readonly struct GameSessionStatus
    {
        public static readonly GameSessionStatus Idle = new GameSessionStatus(
            GameSessionPhase.Idle,
            "No session.",
            null);

        public GameSessionStatus(GameSessionPhase phase, string description, Exception failure)
        {
            Phase = phase;
            Description = description ?? string.Empty;
            Failure = failure;
        }

        public GameSessionPhase Phase { get; }

        public string Description { get; }

        public Exception Failure { get; }

        public bool IsReady => Phase == GameSessionPhase.Ready;

        public override string ToString()
        {
            return $"{Phase}: {Description}";
        }
    }
}
