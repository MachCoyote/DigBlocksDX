using DigBlocks.Core.Hosting;
using UnityEngine;

namespace DigBlocks.Bootstrap
{
    public sealed class UnityApplicationLifetime : IApplicationLifetime
    {
        public void RequestQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
