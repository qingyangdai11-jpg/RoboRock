using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

public class privacynoticeClick : MonoBehaviour
{
    [Header("Window Target")]
    [Tooltip("The CanvasGroup of the window to fade in/out")]
    public CanvasGroup windowCanvasGroup;


    [Header("Animation Settings")]
    [Tooltip("Duration of the fade transition")]
    public float fadeDuration = 0.3f;

    [Tooltip("Ease type for fade transition")]
    public Ease easeType = Ease.InOutQuad;

    private Tween currentTween;

    private void Start()
    {
       
    }

    public void ShowWindow()
    {
        
        // Kill any running tween on this CanvasGroup to avoid conflicts
        if (currentTween != null && currentTween.IsActive())
        {
            currentTween.Kill();
        }

        // Activate the window gameobject first
        windowCanvasGroup.gameObject.SetActive(true);
        windowCanvasGroup.interactable = true;
        windowCanvasGroup.blocksRaycasts = true;

        // Fade in from current alpha to 1
        currentTween = windowCanvasGroup.DOFade(1f, fadeDuration)
            .SetEase(easeType)
            .SetUpdate(true); // Allow running even if Time.timeScale is 0
    }

   
    public void HideWindow()
    {
       
        // Kill any running tween on this CanvasGroup to avoid conflicts
        if (currentTween != null && currentTween.IsActive())
        {
            currentTween.Kill();
        }

        // Disable interaction during transition
        windowCanvasGroup.interactable = false;
        windowCanvasGroup.blocksRaycasts = false;

        // Fade out to 0, then deactivate the gameobject
        currentTween = windowCanvasGroup.DOFade(0f, fadeDuration)
            .SetEase(easeType)
            .SetUpdate(true) // Allow running even if Time.timeScale is 0
            .OnComplete(() =>
            {
                windowCanvasGroup.gameObject.SetActive(false);
            });
    }

    private void OnDestroy()
    {
        // Clean up tweens to prevent memory leaks
        if (currentTween != null && currentTween.IsActive())
        {
            currentTween.Kill();
        }
    }
}
