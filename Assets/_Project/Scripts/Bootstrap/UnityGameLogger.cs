using System;
using DigBlocks.Core.Hosting;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    public sealed class UnityGameLogger : IGameLogger
    {
        public void Log(GameLogLevel level, string message, Exception exception = null)
        {
            switch (level)
            {
                case GameLogLevel.Debug:
                case GameLogLevel.Information:
                    UnityEngine.Debug.Log(message);
                    break;
                case GameLogLevel.Warning:
                    UnityEngine.Debug.LogWarning(message);
                    break;
                case GameLogLevel.Error:
                    UnityEngine.Debug.LogError(message);
                    if (exception != null)
                    {
                        UnityEngine.Debug.LogException(exception);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, null);
            }
        }
    }
}
