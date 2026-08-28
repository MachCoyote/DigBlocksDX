using System;
using DigBlocks.Core.Hosting;

namespace DigBlocks.Core.Tests.Hosting
{
    internal sealed class ThrowingLogger : IGameLogger
    {
        private readonly Func<string, bool> shouldThrow;

        public ThrowingLogger(Func<string, bool> shouldThrow)
        {
            this.shouldThrow = shouldThrow;
        }

        public void Log(GameLogLevel level, string message, Exception exception = null)
        {
            if (shouldThrow(message))
            {
                throw new InvalidOperationException("logger failed");
            }
        }
    }
}
