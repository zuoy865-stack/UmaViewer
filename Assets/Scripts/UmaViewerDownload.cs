using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using static UmaViewerUI;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

public class UmaViewerDownload : MonoBehaviour
{
    public static string MANIFEST_ROOT_URL = "https://prd-storage-app-umamusume.akamaized.net/dl/resources/Manifest";
    public static string GENERIC_BASE_URL = "https://prd-storage-game-umamusume.akamaized.net/dl/resources/Generic";
#if UNITY_IOS || UNITY_IPHONE || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    public static string ASSET_BASE_URL = "https://prd-storage-game-umamusume.akamaized.net/dl/resources/iOS/assetbundles/";
#elif UNITY_ANDROID
    public static string ASSET_BASE_URL = "https://prd-storage-game-umamusume.akamaized.net/dl/resources/Android/assetbundles/";
#else
    public static string ASSET_BASE_URL = "https://prd-storage-game-umamusume.akamaized.net/dl/resources/Windows/assetbundles/";
#endif

#if UNITY_ANDROID || UNITY_IOS || UNITY_IPHONE
    private const int maxConcurrentDownloads = 6;
#else
    private const int maxConcurrentDownloads = 30;
#endif
    private static SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentDownloads);
    private static List<Coroutine> downloadCoroutines = new List<Coroutine>();
    private static int CurrentCoroutinesCount = 0;
    private static WaitUntil downloadWaitUntil = new WaitUntil(() => CurrentCoroutinesCount < maxConcurrentDownloads);
    private static WaitUntil downloadWaitUntilComplete = new WaitUntil(() => CurrentCoroutinesCount == 0);
    private static List<Task> downloadTasks = new List<Task>();

    public static IEnumerator DownloadText(string url, System.Action<string> callback)
    {
        UnityWebRequest www = UnityWebRequest.Get(url);
        www.timeout = 3;
        yield return www.SendWebRequest();
        
        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.Log(www.error);
            callback("");
        }
        else
        {
            callback(www.downloadHandler.text);
        }
    }

    public static void DownloadAssetSync(UmaDatabaseEntry entry, Action<string , UIMessageType> callback = null)
    {
        string baseurl = entry.IsAssetBundle ? GetAssetRequestUrl(entry.Url) : GetGenericRequestUrl(entry.Url);

        using UnityWebRequest www = UnityWebRequest.Get(baseurl);
        www.SendWebRequest();
        while (!www.isDone) { }
        if (www.result != UnityWebRequest.Result.Success)
        {
            // 通过统一错误管理器报告下载失败并进行友好提示与去重
            UmaErrorManager.ReportDownloadError(entry, www.error, baseurl);
            callback?.Invoke($"Failed to download resources : {www.error}", UIMessageType.Error);
        }
        else
        {
            Debug.Log("saving " + entry.Url);
            SaveDownloadedAsset(entry, www.downloadHandler.data);
        }
    }
  
    public static IEnumerator DownloadAssets(List<UmaDatabaseEntry> entries, Action<int, int, string> callback = null)
    {
        entries = entries.Where(e => !File.Exists(e.Path)).ToList();
        if (entries.Count == 0) yield break;
        callback?.Invoke(0, entries.Count, "DownLoading");
        var percent_num = (float)entries.Count / 100;
        for(int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            yield return downloadWaitUntil;
            if(i % percent_num == 0)
            {
                callback?.Invoke(i, entries.Count, "DownLoading");
            }
            CurrentCoroutinesCount++;
            // 将分发子协程的宿主从易受场景切换影响的 UI 实例（UmaViewerUI.Instance）替换为全局持久单例 UmaAssetManager.instance
            downloadCoroutines.Add(UmaAssetManager.instance.StartCoroutine(DownloadTask(entry)));
        }

        yield return downloadWaitUntilComplete;
    }

    public static IEnumerator DownloadTask(UmaDatabaseEntry entry)
    {
        try
        {
            string baseurl = (string.IsNullOrEmpty(Path.GetExtension(entry.Name)) ? GetAssetRequestUrl(entry.Url) : GetGenericRequestUrl(entry.Url));
            using (UnityWebRequest www = UnityWebRequest.Get(baseurl))
            {
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success)
                {
                    // 统一错误处理，向 UI 与控制台报告人性化指引
                    UmaErrorManager.ReportDownloadError(entry, www.error, baseurl);
                }
                else
                {
                    SaveDownloadedAsset(entry, www.downloadHandler.data);
                }
            }
        }
        finally
        {
            // 确保无论发生何种网络异常或写入错误，CurrentCoroutinesCount 均能可靠递减并归零，防止加载进度条挂起卡死
            CurrentCoroutinesCount--;
        }
    }

    public static async void DownloadAssets(IEnumerable<UmaDatabaseEntry> entrys)
    {
        downloadTasks.Clear();
        foreach (var entry in entrys)
        {
            await semaphore.WaitAsync();
            downloadTasks.Add(DownloadTask(entry, semaphore));
        }
        await Task.WhenAll(downloadTasks);
    }

    public static async Task DownloadTask(UmaDatabaseEntry entry, SemaphoreSlim semaphore)
    {
        if (!File.Exists(entry.Path))
        {
            string baseurl = (string.IsNullOrEmpty(Path.GetExtension(entry.Name)) ? GetAssetRequestUrl(entry.Url) : GetGenericRequestUrl(entry.Url));
            using UnityWebRequest www = UnityWebRequest.Get(baseurl);
            www.SendWebRequest();
            await Task.Run(() => { while (!www.isDone) { } });
            if (www.result != UnityWebRequest.Result.Success)
            {
                // 异步任务同样接入统一错误管理器
                UmaErrorManager.ReportDownloadError(entry, www.error, baseurl);
            }
            else
            {
                Debug.Log("saving " + entry.Url);
                SaveDownloadedAsset(entry, www.downloadHandler.data);
            }
        }   
        semaphore.Release();
    }

    private static void SaveDownloadedAsset(UmaDatabaseEntry entry, byte[] data)
    {
        var directory = Path.GetDirectoryName(entry.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            MarkNoBackup(directory);
        }

        var tempPath = entry.Path + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        File.WriteAllBytes(tempPath, data);
        if (File.Exists(entry.Path))
        {
            File.Delete(entry.Path);
        }
        File.Move(tempPath, entry.Path);
        MarkNoBackup(entry.Path);
    }

    private static void MarkNoBackup(string path)
    {
#if UNITY_IOS || UNITY_IPHONE
        UnityEngine.iOS.Device.SetNoBackupFlag(path);
#endif
    }

    public static string GetManifestRequestUrl(string hash)
    {
        // 去除根前缀末尾的斜杠，防止与后续相对路径拼接时产生双斜杠（//）引发 Akamai CDN 的 HTTP 400/403/404 错误
        return $"{MANIFEST_ROOT_URL.TrimEnd('/')}/{hash.Substring(0, 2)}/{hash}";
    }

    public static string GetGenericRequestUrl(string hash)
    {
        // 去除根前缀末尾的斜杠，防止与后续相对路径拼接时产生双斜杠（//）引发 Akamai CDN 的 HTTP 400/403/404 错误
        return $"{GENERIC_BASE_URL.TrimEnd('/')}/{hash.Substring(0, 2)}/{hash}";
    }
    
    public static string GetAssetRequestUrl(string hash)
    {
        // 去除资源前缀末尾的斜杠（如 Windows 平台配置下的结尾斜杠），防止拼装出 assetbundles//xx/xxxx 导致 CDN 拒绝访问
        return $"{ASSET_BASE_URL.TrimEnd('/')}/{hash.Substring(0, 2)}/{hash}";
    }
}
