using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 继承自 Image 组件，当 Sprite 资源切换时自动适配 RectTransform 大小
/// </summary>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/Image-AutoSize", 12)]
public class ImageAutoSize : Image
{
    [Header("Auto Size Settings")]
    [Tooltip("是否启用自动调整大小")]
    [SerializeField] private bool enableAutoSize = true;

    [Tooltip("是否保持原始宽高比")]
    [SerializeField] private bool keepAspectRatio = true;

    [Tooltip("最大宽度限制（0表示无限制）")]
    [SerializeField] private float maxWidth = 0f;

    [Tooltip("最大高度限制（0表示无限制）")]
    [SerializeField] private float maxHeight = 0f;

    [Tooltip("缩放倍数")]
    [SerializeField] private float scaleFactor = 1f;

    private RectTransform rectTransform;
    private Sprite lastSprite;

    protected override void Awake()
    {
        base.Awake();
        rectTransform = GetComponent<RectTransform>();
    }

    protected override void Start()
    {
        base.Start();
        // 初始化时调整大小
        if (enableAutoSize && sprite != null)
        {
            AdjustSize();
        }
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        // 编辑器模式下，当属性改变时也调整大小
        if (enableAutoSize && sprite != null)
        {
            AdjustSize();
        }
    }
#endif

    private void LateUpdate()
    {
        // 检测 Sprite 是否发生变化
        if (enableAutoSize && sprite != lastSprite)
        {
            lastSprite = sprite;
            if (sprite != null)
            {
                AdjustSize();
            }
        }
    }

    /// <summary>
    /// 根据 Sprite 的大小调整 RectTransform
    /// </summary>
    private void AdjustSize()
    {
        if (sprite == null || rectTransform == null)
            return;

        // 获取 Sprite 的原始尺寸
        Rect spriteRect = sprite.rect;
        float spriteWidth = spriteRect.width;
        float spriteHeight = spriteRect.height;

        // 应用缩放倍数
        float targetWidth = spriteWidth * scaleFactor;
        float targetHeight = spriteHeight * scaleFactor;

        // 应用最大尺寸限制
        if (maxWidth > 0 || maxHeight > 0)
        {
            float widthScale = maxWidth > 0 ? maxWidth / targetWidth : float.MaxValue;
            float heightScale = maxHeight > 0 ? maxHeight / targetHeight : float.MaxValue;

            if (keepAspectRatio)
            {
                // 保持宽高比，取较小的缩放比例
                float scale = Mathf.Min(widthScale, heightScale, 1f);
                targetWidth *= scale;
                targetHeight *= scale;
            }
            else
            {
                // 不保持宽高比，分别限制宽高
                if (widthScale < 1f)
                    targetWidth = maxWidth;
                if (heightScale < 1f)
                    targetHeight = maxHeight;
            }
        }

        // 设置 RectTransform 的尺寸
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targetWidth);
        rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
    }

    /// <summary>
    /// 手动触发尺寸调整
    /// </summary>
    public void RefreshSize()
    {
        if (enableAutoSize)
        {
            AdjustSize();
        }
    }

    /// <summary>
    /// 设置是否启用自动调整大小
    /// </summary>
    public void SetAutoSizeEnabled(bool enabled)
    {
        enableAutoSize = enabled;
        if (enabled && sprite != null)
        {
            AdjustSize();
        }
    }

    /// <summary>
    /// 设置缩放倍数
    /// </summary>
    public void SetScaleFactor(float factor)
    {
        scaleFactor = factor;
        if (enableAutoSize && sprite != null)
        {
            AdjustSize();
        }
    }
}

#if UNITY_EDITOR
/// <summary>
/// 在 Hierarchy 右键菜单中添加创建 Image-AutoSize 的选项
/// </summary>
public static class ImageAutoSizeMenuItems
{
    [MenuItem("GameObject/UI/Image-AutoSize", false, 2003)]
    static void CreateImageAutoSize(MenuCommand menuCommand)
    {
        // 获取或创建 Canvas
        Canvas canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            // 如果没有 Canvas，创建一个
            GameObject canvasGO = new GameObject("Canvas");
            canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();
            Undo.RegisterCreatedObjectUndo(canvasGO, "Create Canvas");

            // 创建 EventSystem（如果不存在）
            if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                GameObject eventSystemGO = new GameObject("EventSystem");
                eventSystemGO.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystemGO.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(eventSystemGO, "Create EventSystem");
            }
        }

        // 创建 Image-AutoSize GameObject
        GameObject imageGO = new GameObject("Image-AutoSize");
        GameObjectUtility.SetParentAndAlign(imageGO, menuCommand.context as GameObject);
        
        // 如果没有指定父对象，使用 Canvas 作为父对象
        if (imageGO.transform.parent == null)
        {
            imageGO.transform.SetParent(canvas.transform, false);
        }

        // 添加 RectTransform（如果还没有）
        RectTransform rectTransform = imageGO.GetComponent<RectTransform>();
        if (rectTransform == null)
        {
            rectTransform = imageGO.AddComponent<RectTransform>();
        }

        // 设置默认大小
        rectTransform.sizeDelta = new Vector2(100, 100);

        // 添加 ImageAutoSize 组件
        imageGO.AddComponent<ImageAutoSize>();

        // 注册 Undo 操作
        Undo.RegisterCreatedObjectUndo(imageGO, "Create Image-AutoSize");
        
        // 选中新创建的对象
        Selection.activeGameObject = imageGO;
    }
}
#endif
