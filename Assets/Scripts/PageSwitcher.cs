using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

public class PageSwitcher : MonoBehaviour
{
    [Header("页面设置")]
    [Tooltip("所有需要控制的CanvasGroup页面")]
    public CanvasGroup[] pages;

    [Header("切换动画设置")]
    [Tooltip("淡入淡出时间")]
    public float fadeDuration = 0.5f;

    [Tooltip("初始显示的页面索引")]
    public int defaultPageIndex = 0;

    public int currentPageIndex=-1;


    private void Awake()
    {
        // 初始化所有页面
        InitializePages();
    }
    void Start()
    {
        // 显示默认页面
        SwitchToPage(defaultPageIndex);
    }

    void InitializePages()
    {
        // 确保所有页面初始状态正确
        foreach (var page in pages)
        {
            if (page == null) continue;

            page.alpha = 0;
            page.interactable = false;
            page.blocksRaycasts = false;
            page.gameObject.SetActive(false);

        }
    }

    // 切换到指定索引的页面
    public void SwitchToPage(int pageIndex)
    {
        // 检查索引是否有效
        if (pageIndex < 0 || pageIndex >= pages.Length || pages[pageIndex] == null)
        {
            Debug.LogWarning($"无效的页面索引: {pageIndex}");
            return;
        }

        // 如果已经是当前页面，不做任何操作
        if (pageIndex == currentPageIndex) return;

        // 获取当前页面和新页面
        CanvasGroup currentPage = currentPageIndex >= 0 ? pages[currentPageIndex] : null;
        CanvasGroup newPage = pages[pageIndex];

        // 使用DOTween进行动画
        Sequence switchSequence = DOTween.Sequence();

        // 如果有当前页面，先淡出
        if (currentPage != null)
        {
            currentPage.interactable = false;
            currentPage.blocksRaycasts = false;
            var temp = currentPage;
            switchSequence.Append(currentPage.DOFade(0, fadeDuration).SetEase(Ease.Linear)
                .OnComplete(() => {
                    temp.gameObject.SetActive(false);
                }));
        }

        // 然后淡入新页面
        switchSequence.Insert(0,newPage.DOFade(1, fadeDuration).SetEase(Ease.Linear)
            .OnStart(() => {
                newPage.gameObject.SetActive(true);
            })
            .OnComplete(() => {
                newPage.interactable = true;
                newPage.blocksRaycasts = true;
            }));

        // 更新当前页面索引
        currentPageIndex = pageIndex;
    }

    // 通过按钮直接调用的方法
    public void SwitchToPageByButton(int pageIndex)
    {
        SwitchToPage(pageIndex);
    }

    // 切换到下一页
    public void NextPage()
    {
        int nextIndex = (currentPageIndex + 1) % pages.Length;
        SwitchToPage(nextIndex);
    }

    // 切换到上一页
    public void PreviousPage()
    {
        int prevIndex = (currentPageIndex - 1 + pages.Length) % pages.Length;
        SwitchToPage(prevIndex);
    }

    void OnDestroy()
    {
        // 清理DOTween动画
        DOTween.KillAll();
    }
}
