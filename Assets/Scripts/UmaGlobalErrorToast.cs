using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 全局浮动错误与警告横幅提示组件（跨场景、常驻 DontDestroyOnLoad）。
/// 基于 Unity IMGUI 纯代码自举构建，不依赖任何场景特定的 Canvas 或 EventSystem 资产，
/// 确保在任何场景加载阶段、过渡黑屏期间或 LiveScene 中均能即时展示高可见度的 UI 报错。
/// </summary>
public class UmaGlobalErrorToast : MonoBehaviour
{
    /// <summary>
    /// 提示消息类型
    /// </summary>
    public enum ToastType
    {
        Error,
        Warning,
        Info
    }

    /// <summary>
    /// 单条 Toast 数据模型
    /// </summary>
    private class ToastItem
    {
        public string Id;
        public string Message;
        public ToastType Type;
        public float ExpireTime;
        public float Duration;
        public float FadeDuration;
    }

    // 单例引用
    private static UmaGlobalErrorToast instance;

    // 线程安全的消息缓冲队列，允许后台下载/解密线程直接安全投递
    private static readonly ConcurrentQueue<ToastItem> pendingQueue = new ConcurrentQueue<ToastItem>();

    // 主线程中正在展示的 Toast 列表
    private readonly List<ToastItem> activeToasts = new List<ToastItem>();

    // 最大同时显示条数，避免错误堆积过高遮挡屏幕
    private const int MaxVisibleToasts = 4;

    // 绘制样式与背景贴图缓存
    private GUIStyle errorBoxStyle;
    private GUIStyle warningBoxStyle;
    private GUIStyle infoBoxStyle;
    private GUIStyle textStyle;
    private GUIStyle closeButtonStyle;
    private Texture2D errorBgTex;
    private Texture2D warningBgTex;
    private Texture2D infoBgTex;
    private Texture2D closeBtnTex;
    private Font customFont;
    private bool stylesInitialized = false;

    /// <summary>
    /// 游戏启动时自举注册常驻实例
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInitialize()
    {
        EnsureInstance();
    }

    /// <summary>
    /// 获取或创建全局单例
    /// </summary>
    public static UmaGlobalErrorToast EnsureInstance()
    {
        if (instance != null) return instance;

        // 尝试寻找场景中现有组件
        instance = FindObjectOfType<UmaGlobalErrorToast>();
        if (instance == null)
        {
            GameObject toastGo = new GameObject("[UmaGlobalErrorToast]");
            instance = toastGo.AddComponent<UmaGlobalErrorToast>();
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(toastGo);
            }
        }
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            DestroyImmediate(gameObject);
            return;
        }

        instance = this;
        if (Application.isPlaying)
        {
            DontDestroyOnLoad(gameObject);
        }
    }

    private void Update()
    {
        // 1. 将后台线程队列中的消息抽取到主线程活跃列表中
        while (pendingQueue.TryDequeue(out ToastItem newItem))
        {
            // 若超过最大条数，移除最早的一条
            if (activeToasts.Count >= MaxVisibleToasts)
            {
                activeToasts.RemoveAt(0);
            }
            activeToasts.Add(newItem);
        }

        // 2. 清理已完全过期的 Toast
        float now = Time.realtimeSinceStartup;
        for (int i = activeToasts.Count - 1; i >= 0; i--)
        {
            if (now >= activeToasts[i].ExpireTime)
            {
                activeToasts.RemoveAt(i);
            }
        }
    }

    private void OnGUI()
    {
        if (activeToasts.Count == 0) return;

        // 确保使用最顶层深度，覆盖在游戏所有界面与场景元素之上
        GUI.depth = -10000;

        EnsureStyles();

        float now = Time.realtimeSinceStartup;
        float screenWidth = Screen.width;
        float bannerWidth = Mathf.Clamp(screenWidth * 0.75f, 360f, 760f);
        float bannerX = (screenWidth - bannerWidth) * 0.5f;
        float currentY = 24f;
        float itemSpacing = 8f;

        Color originalGuiColor = GUI.color;

        // 倒序展示或按加入顺序展示：从上往下依次排布
        for (int i = 0; i < activeToasts.Count; i++)
        {
            ToastItem toast = activeToasts[i];

            // 计算剩余时间与淡出透明度
            float remaining = toast.ExpireTime - now;
            float alpha = 1.0f;
            if (remaining < toast.FadeDuration && toast.FadeDuration > 0)
            {
                alpha = Mathf.Clamp01(remaining / toast.FadeDuration);
            }

            GUI.color = new Color(1f, 1f, 1f, alpha);

            // 选择背景样式
            GUIStyle boxStyle = toast.Type switch
            {
                ToastType.Warning => warningBoxStyle,
                ToastType.Info => infoBoxStyle,
                _ => errorBoxStyle
            };

            // 根据文本计算自适应高度
            GUIContent textContent = new GUIContent(toast.Message);
            float textAvailableWidth = bannerWidth - 56f; // 预留左右 padding 与关闭按钮空间
            float textHeight = textStyle.CalcHeight(textContent, textAvailableWidth);
            float bannerHeight = Mathf.Max(48f, textHeight + 20f);

            Rect bannerRect = new Rect(bannerX, currentY, bannerWidth, bannerHeight);

            // 绘制横幅背景
            GUI.Box(bannerRect, GUIContent.none, boxStyle);

            // 绘制横幅文本
            Rect textRect = new Rect(bannerX + 16f, currentY + (bannerHeight - textHeight) * 0.5f, textAvailableWidth, textHeight);
            GUI.Label(textRect, textContent, textStyle);

            // 绘制右上角关闭按钮 [×]
            float closeBtnSize = 24f;
            Rect closeBtnRect = new Rect(bannerX + bannerWidth - closeBtnSize - 10f, currentY + 10f, closeBtnSize, closeBtnSize);
            if (GUI.Button(closeBtnRect, "×", closeButtonStyle))
            {
                // 用户点击关闭，立即标记为过期
                toast.ExpireTime = 0;
            }

            currentY += bannerHeight + itemSpacing;
        }

        GUI.color = originalGuiColor;
    }

    /// <summary>
    /// 初始化并缓存 IMGUI 样式与纯色背景贴图，避免每帧重复创建引发 GC
    /// </summary>
    private void EnsureStyles()
    {
        if (stylesInitialized && errorBoxStyle != null) return;

        // 尝试加载系统常见中文字体，确保在 Windows 各环境下中文不乱码
        if (customFont == null)
        {
            try
            {
                string[] fontNames = new[] { "Microsoft YaHei", "SimHei", "PingFang SC", "Segoe UI", "Arial" };
                customFont = Font.CreateDynamicFontFromOSFont(fontNames, 14);
            }
            catch
            {
                customFont = null;
            }
        }

        // 创建背景纯色纹理
        errorBgTex = CreateColorTexture(new Color(0.72f, 0.12f, 0.12f, 0.94f));      // 警示深红
        warningBgTex = CreateColorTexture(new Color(0.85f, 0.48f, 0.08f, 0.94f));    // 警示琥珀橙
        infoBgTex = CreateColorTexture(new Color(0.15f, 0.42f, 0.72f, 0.94f));       // 提示蓝
        closeBtnTex = CreateColorTexture(new Color(1f, 1f, 1f, 0.15f));              // 按钮半透明白

        // 错误框样式
        errorBoxStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = errorBgTex },
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(12, 12, 8, 8)
        };

        // 警告框样式
        warningBoxStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = warningBgTex },
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(12, 12, 8, 8)
        };

        // 信息框样式
        infoBoxStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = infoBgTex },
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(12, 12, 8, 8)
        };

        // 消息文字样式
        textStyle = new GUIStyle(GUI.skin.label)
        {
            font = customFont ?? GUI.skin.label.font,
            fontSize = 13,
            wordWrap = true,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };

        // 关闭按钮样式
        closeButtonStyle = new GUIStyle(GUI.skin.button)
        {
            font = customFont ?? GUI.skin.button.font,
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(1f, 1f, 1f, 0.85f), background = closeBtnTex },
            hover = { textColor = Color.white, background = closeBtnTex }
        };

        stylesInitialized = true;
    }

    /// <summary>
    /// 生成 1x1 纯色像素贴图
    /// </summary>
    private static Texture2D CreateColorTexture(Color color)
    {
        Texture2D texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private void OnDestroy()
    {
        // 销毁动态创建的纹理资源，防止内存泄漏
        if (errorBgTex != null) Destroy(errorBgTex);
        if (warningBgTex != null) Destroy(warningBgTex);
        if (infoBgTex != null) Destroy(infoBgTex);
        if (closeBtnTex != null) Destroy(closeBtnTex);
    }

    /// <summary>
    /// 最近一次弹出的错误或提示消息（方便测试与诊断断言）
    /// </summary>
    public static string LastMessage { get; private set; } = string.Empty;

    #region 公共静态调用入口

    /// <summary>
    /// 手动显式初始化全局 Toast（供启动或自动化测试调用）
    /// </summary>
    public static void Initialize() => EnsureInstance();

    /// <summary>
    /// 获取最近一次弹出的消息文本
    /// </summary>
    public static string GetLastMessage() => LastMessage;

    /// <summary>
    /// 弹出全局顶层错误提示横幅（醒目红底）
    /// </summary>
    /// <param name="message">提示文字</param>
    /// <param name="duration">停留展示秒数（默认 5 秒）</param>
    public static void ShowError(string message, float duration = 5f)
    {
        Show(message, ToastType.Error, duration);
    }

    /// <summary>
    /// 弹出全局顶层警告提示横幅（醒目橙底）
    /// </summary>
    /// <param name="message">提示文字</param>
    /// <param name="duration">停留展示秒数（默认 4 秒）</param>
    public static void ShowWarning(string message, float duration = 4f)
    {
        Show(message, ToastType.Warning, duration);
    }

    /// <summary>
    /// 弹出指定类型的全局横幅提示
    /// </summary>
    /// <param name="message">提示内容</param>
    /// <param name="type">消息类型</param>
    /// <param name="duration">显示秒数</param>
    public static void Show(string message, ToastType type = ToastType.Error, float duration = 5f)
    {
        if (string.IsNullOrEmpty(message)) return;

        LastMessage = message;
        EnsureInstance();

        float now = Time.realtimeSinceStartup;
        ToastItem item = new ToastItem
        {
            Id = Guid.NewGuid().ToString(),
            Message = message,
            Type = type,
            Duration = duration,
            ExpireTime = now + duration,
            FadeDuration = 0.6f
        };

        pendingQueue.Enqueue(item);
    }

    /// <summary>
    /// 清除当前所有显示中的 Toast 横幅
    /// </summary>
    public static void ClearAll()
    {
        while (pendingQueue.TryDequeue(out _)) { }
        if (instance != null)
        {
            instance.activeToasts.Clear();
        }
    }

    #endregion
}
