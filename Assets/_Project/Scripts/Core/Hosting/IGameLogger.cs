using System;

namespace DigBlocks.Core.Hosting
{
    public interface IGameLogger
    {
        void Log(GameLogLevel level, string message, Exception exception = null);
    }
}
