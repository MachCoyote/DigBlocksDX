using Cysharp.Threading.Tasks;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SplashController : MonoBehaviour
{
    public TextMeshProUGUI SplashText;
    public TextAsset SplashTextAsset;
    private string _splashText = "You shouldn't be seeing this!";
    public float blinkInterval = 0.5f;

    CancellationTokenSource disableCts;
    CancellationTokenSource linkedCts;
    public CancellationToken ActiveToken => linkedCts?.Token ?? CancellationToken.None;

    private void OnEnable()
    {
        disableCts = new CancellationTokenSource();
        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            disableCts.Token,
            destroyCancellationToken
        );

        _splashText = GetRandomLine(SplashTextAsset);
        SplashText.text = "";
        TypeText(linkedCts.Token).Forget();
    }

    private void OnDisable()
    {
        linkedCts?.Cancel();
        linkedCts?.Dispose();
        linkedCts = null;
        disableCts?.Dispose();
        disableCts = null;
    }

    private async UniTask TypeText(CancellationToken tok)
    {
        SplashText.text = "";
        foreach (char c in _splashText)
        {
            SplashText.text += c + "_";
            await UniTask.Delay(50, cancellationToken: tok);
            SplashText.text = SplashText.text.Substring(0, SplashText.text.Length - 1);
        }

        BlinkCursor(tok).Forget();
    }

    private async UniTask BlinkCursor(CancellationToken tok)
    {
        string ogText = SplashText.text;
        string cursorText = ogText + "_";
        while (true)
        {
            SplashText.text = ogText;
            await UniTask.Delay((int)(blinkInterval * 1000), cancellationToken: tok);
            SplashText.text = cursorText;
            await UniTask.Delay((int)(blinkInterval * 1000), cancellationToken: tok);
        }
    }

    private string GetRandomLine(TextAsset asset)
    {
        // 1. Check if the asset is missing or empty
        if (asset == null || string.IsNullOrEmpty(asset.text))
        {
            Debug.LogWarning("TextAsset is null or empty!");
            return string.Empty;
        }
        string[] lines = asset.text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

        // 3. Make sure the file actually contains lines of text
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        // 4. Pick a random number between 0 and the total number of lines
        int randomIndex = Random.Range(0, lines.Length);

        // 5. Return the line at that random position
        return lines[randomIndex];
    }
}
