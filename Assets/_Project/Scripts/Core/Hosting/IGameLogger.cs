using System;

namespace DigBlocks.Core.Hosting
{
    public interface IGameLogger
    {
        IGameLogger CreateFor(string sourceName);

        void Log(string message, GameLogLevel level = GameLogLevel.Information, Exception exception = null);
    }
}
