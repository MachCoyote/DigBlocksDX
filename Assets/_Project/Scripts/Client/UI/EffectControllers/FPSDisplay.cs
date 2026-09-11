using UnityEngine;
using TMPro;

namespace DigBlocks.Client.UI
{
    public class FPSDisplay : MonoBehaviour
    {
        public TextMeshProUGUI fpsText;

        int frameCount = 0;
        float elapsedTime = 0.0f;
        int avgFps = 0;
        float rawFps = 0.0f;
        float avgFrameTime = 0.0f;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
        
        }

        void OnEnable()
        {
            fpsText.text = "000 / 000 FPS\n0.00 ms FRAMETIME";
            frameCount = 0;
            elapsedTime = 0.0f;
            avgFps = 0;
            rawFps = 0.0f;
        }

        // Update is called once per frame
        void Update()
        {
            frameCount++;
            elapsedTime += Time.unscaledDeltaTime;
            rawFps = 1.0f / Time.unscaledDeltaTime;

            if (elapsedTime > 1.0f)
            {
                avgFps = (int)(frameCount / elapsedTime);
                avgFrameTime = elapsedTime / frameCount * 1000.0f; // Convert to milliseconds
                fpsText.text = $"{rawFps:F2} / {avgFps} FPS\n{Time.unscaledDeltaTime:F2} / {avgFrameTime:F2} ms FRAMETIME";
                frameCount = 0;
                elapsedTime = 0.0f;
            }

            fpsText.text = $"{rawFps:F2} / {avgFps} FPS\n{Time.unscaledDeltaTime:F2} / {avgFrameTime:F2} ms FRAMETIME";
        }
    }
}
