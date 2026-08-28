using System;
using System.Collections.Generic;
using DigBlocks.Core.Hosting;

namespace DigBlocks.Core.Tests.Hosting
{
    internal sealed class RecordingLogger : IGameLogger
    {
        public List<Entry> Entries { get; } = new List<Entry>();

        public void Log(GameLogLevel level, string message, Exception exception = null)
        {
            Entries.Add(new Entry(level, message, exception));
        }

        internal readonly struct Entry
        {
            public Entry(GameLogLevel level, string message, Exception exception)
            {
                Level = level;
                Message = message;
                Exception = exception;
            }

            public GameLogLevel Level { get; }

            public string Message { get; }

            public Exception Exception { get; }
        }
    }
}
