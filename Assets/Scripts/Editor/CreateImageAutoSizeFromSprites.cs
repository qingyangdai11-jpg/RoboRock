using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 编辑器工具：从选中的 Sprite 资源批量创建 Image-AutoSize
/// </summary>
public class CreateImageAutoSizeFromSprites : Editor
{
    [MenuItem("Assets/Create/Image-AutoSize", false, 10)]
    static void CreateImageAutoSizeFromSelectedSprites()
    {
        // 获取所有选中的 Sprite 资源
        List<Sprite> selectedSprites = new List<Sprite>();
        
        foreach (Object obj in Selection.objects)
        {
            // 检查是否是 Sprite
            if (obj is Sprite sprite)
            {
                selectedSprites.Add(sprite);
            }
            // 检查是否是 Texture2D（可能包含多个 Sprite）
            else if (obj is Texture2D)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                foreach (Object asset in assets)
                {
                    if (asset is Sprite spriteAsset)
                    {
                        selectedSprites.Add(spriteAsset);
                    }
                }
            }
        }

        if (selectedSprites.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "请先选择至少一个 Sprite 图片资源！", "确定");
            return;
        }

        // 获取或创建 Canvas
        Canvas canvas = Object.FindObjectOfType<Canvas>();
        if (canvas == null)
        {
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

        // 创建 Panel 作为容器
        GameObject panelGO = new GameObject("Panel_ImageAutoSize");
        panelGO.transform.SetParent(canvas.transform, false);
        panelGO.AddComponent<CanvasGroup>();
        RectTransform panelRect = panelGO.AddComponent<RectTransform>();
        // 设置 Panel 为全屏
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.sizeDelta = Vector2.zero;
        panelRect.anchoredPosition = Vector2.zero;

        // 可选：添加 Image 组件使 Panel 可见（背景色）
        //Image panelImage = panelGO.AddComponent<Image>();
        //panelImage.color = new Color(1, 1, 1, 0.0f); // 半透明白色背景

        Undo.RegisterCreatedObjectUndo(panelGO, "Create Panel");

        // 为每个 Sprite 创建 Image-AutoSize
        List<GameObject> createdObjects = new List<GameObject>();
        float spacing = 120f; // 图片之间的间距
        float startX = -((selectedSprites.Count - 1) * spacing) / 2f; // 居中排列

        for (int i = 0; i < selectedSprites.Count; i++)
        {
            Sprite sprite = selectedSprites[i];
            
            // 创建 GameObject，命名格式为 "Image-" + 图片名字
            string imageName = "Image-" + sprite.name;
            GameObject imageGO = new GameObject(imageName);
            imageGO.transform.SetParent(panelGO.transform, false);

            // 添加 RectTransform
            RectTransform rectTransform = imageGO.AddComponent<RectTransform>();
            
            // 设置位置（水平排列）
            rectTransform.anchoredPosition = new Vector2(startX + i * spacing, 0);

            // 添加 ImageAutoSize 组件
            ImageAutoSize imageAutoSize = imageGO.AddComponent<ImageAutoSize>();
            
            // 赋予 Sprite
            imageAutoSize.sprite = sprite;

            // 注册 Undo
            Undo.RegisterCreatedObjectUndo(imageGO, "Create Image-AutoSize");
            
            createdObjects.Add(imageGO);
        }

        // 选中创建的 Panel
        Selection.activeGameObject = panelGO;

        // 显示成功消息
        Debug.Log($"成功创建 {selectedSprites.Count} 个 Image-AutoSize 对象，位于 Panel: {panelGO.name}");
        EditorUtility.DisplayDialog("成功", 
            $"已成功创建 {selectedSprites.Count} 个 Image-AutoSize 对象！\n所有对象已放置在 Panel 下。", 
            "确定");
    }

    // 验证菜单项是否可用（只有选中 Sprite 时才显示）
    [MenuItem("Assets/Create/Image-AutoSize", true)]
    static bool ValidateCreateImageAutoSize()
    {
        foreach (Object obj in Selection.objects)
        {
            if (obj is Sprite || obj is Texture2D)
            {
                return true;
            }
        }
        return false;
    }
}
