using System;

namespace DigBlocks.Core.Hosting
{
    public sealed class ScopedGameLogger : IGameLogger
    {
        private readonly IGameLogger logger;
        private readonly string sourceName;

        public ScopedGameLogger(IGameLogger logger, string sourceName)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("A logger source name is required.", nameof(sourceName));
            }

            this.sourceName = sourceName;
        }

        public IGameLogger CreateFor(string sourceName)
        {
            return logger.CreateFor(sourceName);
        }

        public void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null)
        {
            logger.Log($"[{sourceName}] {message}", level, exception);
        }
    }
}
