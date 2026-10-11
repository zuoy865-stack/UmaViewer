using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

/// <summary>
/// 统一资源与网络错误管理器，负责对资源加载、下载和缺失等异常进行语义化封包与友好展示。
/// </summary>
public static class UmaErrorManager
{
    // 主线程同步上下文与主线程 ID，确保异步任务中的错误提示也能安全分发到主线程 UI
    private static SynchronizationContext mainContext;
    private static int mainThreadId;

    // 错误频控记录：用于同类型网络错误或重复报错的防刷屏去重
    private static readonly Dictionary<string, float> lastReportTime = new Dictionary<string, float>();
    private static readonly Dictionary<string, int> suppressedCount = new Dictionary<string, int>();
    private const float SuppressionWindowSeconds = 4.0f; // 4秒内同一错误类型抑制重复弹窗

    static UmaErrorManager()
    {
        mainContext = SynchronizationContext.Current;
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>
    /// 初始化/刷新主线程上下文（由主线程在 Awake 或 Start 时调用）
    /// </summary>
    public static void InitializeOnMainThread()
    {
        mainContext = SynchronizationContext.Current;
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>
    /// 封包报告：网络资源下载失败
    /// </summary>
    /// <param name="entry">尝试下载的资源条目</param>
    /// <param name="rawError">UnityWebRequest 抛出的原始错误信息</param>
    /// <param name="requestUrl">请求的具体 URL（可选）</param>
    public static void ReportDownloadError(UmaDatabaseEntry entry, string rawError, string requestUrl = null)
    {
        string entryName = entry != null ? entry.Name : "Unknown";
        Debug.LogError($"[UmaErrorManager] Download failed for '{entryName}' (URL: {requestUrl}): {rawError}");

        bool isChinese = IsChineseLanguage();
        string errorCategoryKey = ClassifyNetworkError(rawError);

        // 检查频控，防止批量加载时连续弹出几十条相同的连接报错
        float now = Time.realtimeSinceStartup;
        bool shouldSuppress = CheckSuppression(errorCategoryKey, now);

        if (shouldSuppress)
        {
            suppressedCount[errorCategoryKey]++;
            return;
        }

        int previousSuppressed = 0;
        if (suppressedCount.TryGetValue(errorCategoryKey, out int count) && count > 0)
        {
            previousSuppressed = count;
            suppressedCount[errorCategoryKey] = 0;
        }

        string friendlyMessage = BuildDownloadErrorMessage(entryName, rawError, isChinese, previousSuppressed);
        ShowUIMessage(friendlyMessage, UIMessageType.Error);
    }

    /// <summary>
    /// 封包报告：本地资源文件不存在
    /// </summary>
    /// <param name="entry">请求的数据库资源条目</param>
    /// <param name="filePath">期望存在的目标路径</param>
    public static void ReportMissingResource(UmaDatabaseEntry entry, string filePath)
    {
        string entryName = entry != null ? entry.Name : "Unknown";
        Debug.LogError($"[UmaErrorManager] Resource file does not exist: {entryName} -> {filePath}");

        bool isChinese = IsChineseLanguage();
        string categoryKey = "MissingFile_" + entryName;

        float now = Time.realtimeSinceStartup;
        if (CheckSuppression(categoryKey, now))
        {
            return;
        }

        string message;
        if (isChinese)
        {
            message = $"[资源缺失] 本地未找到资源: {entryName}\n" +
                      $"路径: {filePath}\n" +
                      $"建议: 游戏本地未缓存该文件。可在游戏客户端内批量下载（一括ダウンロード）或检查游戏路径。";
        }
        else
        {
            message = $"[Resource Missing] File not found: {entryName}\n" +
                      $"Path: {filePath}\n" +
                      $"Suggestion: Perform a full resource download in the game client or check game path.";
        }

        ShowUIMessage(message, UIMessageType.Error);
    }

    /// <summary>
    /// 封包报告：AssetBundle 加载或解密失败
    /// </summary>
    /// <param name="entry">资源条目</param>
    /// <param name="filePath">文件路径</param>
    /// <param name="ex">捕获到的异常</param>
    public static void ReportBundleLoadError(UmaDatabaseEntry entry, string filePath, Exception ex)
    {
        string entryName = entry != null ? entry.Name : "Unknown";
        Debug.LogError($"[UmaErrorManager] Failed to load AssetBundle: {entryName}, path: {filePath}, error: {ex}");

        bool isChinese = IsChineseLanguage();
        string message;
        if (isChinese)
        {
            message = $"[加载失败] AssetBundle 解析异常: {entryName}\n" +
                      $"原因: {ex?.Message}\n" +
                      $"建议: 文件可能损坏或解密密钥不匹配。";
        }
        else
        {
            message = $"[Load Error] Failed to load AssetBundle: {entryName}\n" +
                      $"Reason: {ex?.Message}\n" +
                      $"Suggestion: The file may be corrupted or encryption key does not match.";
        }

        ShowUIMessage(message, UIMessageType.Error);
    }

    /// <summary>
    /// 通用错误报告封包
    /// </summary>
    /// <param name="summary">简要标题</param>
    /// <param name="detail">详细信息</param>
    /// <param name="suggestion">解决建议</param>
    /// <param name="type">消息类型</param>
    public static void ReportGenericError(string summary, string detail = null, string suggestion = null, UIMessageType type = UIMessageType.Error)
    {
        Debug.LogError($"[UmaErrorManager] {summary}: {detail}");

        string message = $"[{summary}]";
        if (!string.IsNullOrEmpty(detail))
        {
            message += $"\n{detail}";
        }
        if (!string.IsNullOrEmpty(suggestion))
        {
            message += $"\n建议: {suggestion}";
        }

        ShowUIMessage(message, type);
    }

    /// <summary>
    /// 封包报告：音频文件缺失或解码失败（用于伴奏、人声等音频资源）
    /// </summary>
    /// <param name="audioPath">音频名称或文件路径</param>
    /// <param name="detail">错误详情或底层异常信息</param>
    public static void ReportAudioLoadError(string audioPath, string detail)
    {
        Debug.LogError($"[UmaErrorManager] Audio load error for '{audioPath}': {detail}");

        bool isChinese = IsChineseLanguage();
        string categoryKey = "AudioError_" + audioPath;

        float now = Time.realtimeSinceStartup;
        if (CheckSuppression(categoryKey, now))
        {
            return;
        }

        string message;
        if (isChinese)
        {
            message = $"[伴奏/音频错误] 伴奏或语音加载失败: {audioPath}\n" +
                      $"详情: {detail}\n" +
                      $"建议: 请检查音频资源是否完整缓存，可在客户端内重新下载该歌曲资源。";
        }
        else
        {
            message = $"[Audio Error] Failed to load audio: {audioPath}\n" +
                      $"Detail: {detail}\n" +
                      $"Suggestion: Check local audio cache or re-download in game client.";
        }

        ShowUIMessage(message, UIMessageType.Error);
    }

    /// <summary>
    /// 判断当前是否处于中文语言环境
    /// </summary>
    private static bool IsChineseLanguage()
    {
        if (Config.Instance != null)
        {
            return Config.Instance.Language == Language.Cn;
        }
        return Application.systemLanguage == SystemLanguage.Chinese ||
               Application.systemLanguage == SystemLanguage.ChineseSimplified;
    }

    /// <summary>
    /// 对网络错误进行归类，用于智能诊断与去重判定
    /// </summary>
    private static string ClassifyNetworkError(string rawError)
    {
        if (string.IsNullOrEmpty(rawError)) return "Unknown";
        if (rawError.IndexOf("Cannot connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rawError.IndexOf("destination host", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rawError.IndexOf("Connection refused", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "ConnectionFailed";
        }
        if (rawError.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rawError.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Timeout";
        }
        if (rawError.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 ||
            rawError.IndexOf("handshake", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "SSL";
        }
        if (rawError.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "NotFound";
        }
        return "OtherNetError";
    }

    /// <summary>
    /// 构造易于理解且带有排查指引的下载失败错误信息
    /// </summary>
    private static string BuildDownloadErrorMessage(string entryName, string rawError, bool isChinese, int previousSuppressedCount)
    {
        string header;
        string suggestion;

        string category = ClassifyNetworkError(rawError);
        if (category == "ConnectionFailed")
        {
            if (isChinese)
            {
                header = "无法连接官方资源服务器 (Akamai CDN)";
                suggestion = "排查建议:\n" +
                             "1. 直连海外服务器可能受限，若使用代理工具请开启 TUN 模式以接管系统全局流量；\n" +
                             "2. 更推荐在赛马娘游戏本体中进行“一括ダウンロード”（全量数据下载），离线读取更稳定。";
            }
            else
            {
                header = "Cannot connect to official CDN host";
                suggestion = "Suggestions:\n" +
                             "1. Enable TUN mode in your proxy/VPN client to route Unity process traffic;\n" +
                             "2. Recommended: Perform a Full Download (一括ダウンロード) in the game client.";
            }
        }
        else if (category == "Timeout")
        {
            if (isChinese)
            {
                header = "资源下载连接超时";
                suggestion = "网络延迟过高或丢包，请检查网络稳定性或在游戏内批量下载。";
            }
            else
            {
                header = "Download request timed out";
                suggestion = "High latency or packet loss. Please check network connection.";
            }
        }
        else
        {
            if (isChinese)
            {
                header = "资源下载失败";
                suggestion = "建议检查网络连通性，或在游戏本体中下载完整资源。";
            }
            else
            {
                header = "Failed to download resource";
                suggestion = "Check network connection or perform a full resource download in-game.";
            }
        }

        string suppressedNotice = "";
        if (previousSuppressedCount > 0)
        {
            suppressedNotice = isChinese
                ? $"[提示: 前序已有 {previousSuppressedCount} 个资源的同类网络连接错误已被合并]\n"
                : $"[Notice: {previousSuppressedCount} similar errors were previously suppressed]\n";
        }

        if (isChinese)
        {
            return $"{suppressedNotice}[下载失败] {header}\n" +
                   $"目标资源: {entryName}\n" +
                   $"{suggestion}\n" +
                   $"底层信息: {rawError}";
        }
        else
        {
            return $"{suppressedNotice}[Download Error] {header}\n" +
                   $"Asset: {entryName}\n" +
                   $"{suggestion}\n" +
                   $"Native Error: {rawError}";
        }
    }

    /// <summary>
    /// 检查并记录频控状态
    /// </summary>
    private static bool CheckSuppression(string key, float now)
    {
        if (lastReportTime.TryGetValue(key, out float lastTime))
        {
            if (now - lastTime < SuppressionWindowSeconds)
            {
                return true; // 处于抑制窗口期内
            }
        }

        lastReportTime[key] = now;
        if (!suppressedCount.ContainsKey(key))
        {
            suppressedCount[key] = 0;
        }
        return false;
    }

    /// <summary>
    /// 安全地在主线程向 UI 发送消息，并贯穿全场景（包括 LiveScene）弹出顶层浮动横幅，杜绝静默失败
    /// </summary>
    /// <param name="message">提示内容</param>
    /// <param name="type">消息类型（默认为 Error）</param>
    public static void ShowUIMessage(string message, UIMessageType type = UIMessageType.Error)
    {
        void Dispatch()
        {
            // 1. 若主界面 UI 仍存活（如在主菜单界面），照常写入底部的滚动消息日志面板
            if (UmaViewerUI.Instance != null)
            {
                UmaViewerUI.Instance.ShowMessage(message, type);
            }

            // 2. 无论 UmaViewerUI 是否存在，若是 Error 或 Warning 类型，或者进入 LiveScene 后（UmaViewerUI 为 null），
            // 均调用 UmaGlobalErrorToast 向屏幕顶层呈现，确保任何场景下的错误都不丢失
            if (type == UIMessageType.Error || UmaViewerUI.Instance == null)
            {
                UmaGlobalErrorToast.ShowError(message);
            }
            else if (type == UIMessageType.Warning)
            {
                UmaGlobalErrorToast.ShowWarning(message);
            }
        }

        if (mainContext != null && Thread.CurrentThread.ManagedThreadId != mainThreadId)
        {
            mainContext.Post(_ => Dispatch(), null);
        }
        else
        {
            Dispatch();
        }
    }
}
