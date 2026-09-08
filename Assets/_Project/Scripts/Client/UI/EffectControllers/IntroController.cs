using Cysharp.Threading.Tasks;
using DG.Tweening;
using System;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

public class IntroController : MonoBehaviour
{
    public float duration = 4.0f;
    public RectTransform Logo;
    public Image fadeImg;

    private float swingamount = 5f;
    private float swingInterval = 1.5f;

    private Color coverColor = Color.black;
    private Quaternion logoRotation = Quaternion.identity;
    private bool baselineCaptured;

    CancellationTokenSource linkedCts;
    public CancellationToken ActiveToken => linkedCts?.Token ?? CancellationToken.None;

    void Awake()
    {
        CaptureBaseline();
    }

    void OnEnable()
    {
        CaptureBaseline();

        //the menu factory activates, deactivates and reactivates this prefab within one frame,
        //so a pass may already be running; clear it before starting a new one
        StopEffects();

        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);

        FadeInOut(linkedCts.Token).Forget();
        SwingLogo(linkedCts.Token).Forget();
    }

    private void OnDisable()
    {
        StopEffects();
    }

    //the authored pose is the only safe restore point; a killed tween leaves the live values anywhere
    private void CaptureBaseline()
    {
        if (baselineCaptured)
            return;

        baselineCaptured = true;

        if (fadeImg != null)
            coverColor = fadeImg.color;

        if (Logo != null)
            logoRotation = Logo.rotation;
    }

    private void StopEffects()
    {
        linkedCts?.Cancel();
        linkedCts?.Dispose();
        linkedCts = null;

        //cancellation kills the tween a pass is awaiting, but never one it has not reached yet
        if (fadeImg != null)
        {
            DOTween.Kill(fadeImg);
            fadeImg.color = coverColor;
        }

        if (Logo != null)
        {
            DOTween.Kill(Logo);
            Logo.rotation = logoRotation;
        }
    }

    private async UniTask SwingLogo(CancellationToken tok)
    {
        if (Logo == null)
            return;

        try
        {
            while (!tok.IsCancellationRequested)
            {
                for (int i = 0; i < ((duration / swingInterval) + 1); i++)
                {
                    float swingAmt = (i % 2 == 0) ? swingamount : -swingamount;

                    //KillAndCancelAwait, not WithCancellation: plain Kill completes the await normally,
                    //which would let a cancelled pass fall through and start the next tween
                    await Logo.DORotate(new Vector3(0f, 0f, swingAmt), swingInterval / 2f)
                        .SetEase(Ease.InOutSine)
                        .ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, tok);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async UniTask FadeInOut(CancellationToken tok)
    {
        if (fadeImg == null)
            return;

        Color clearColor = new Color(coverColor.r, coverColor.g, coverColor.b, 0f);

        try
        {
            await fadeImg.DOColor(clearColor, duration / 2f)
                .ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, tok);

            await fadeImg.DOColor(coverColor, duration / 2f)
                .ToUniTask(TweenCancelBehaviour.KillAndCancelAwait, tok);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
