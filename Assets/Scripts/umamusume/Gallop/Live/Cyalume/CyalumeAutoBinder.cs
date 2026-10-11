using System.Collections;
using Gallop;
using Gallop.Live.Cyalume;
using UnityEngine;

[DisallowMultipleComponent]
public class CyalumeAutoBinder : MonoBehaviour
{
    [Header("live音乐id")]
    [InspectorName("音乐 ID")]
    [Tooltip("可选。设置为 0 时，会自动从 Director.instance.live.MusicId 获取。")]
    public int musicId;

    [Header("组件引用")]
    [InspectorName("3D 荧光棒控制器")]
    public CyalumeController3D controller3D;

    [InspectorName("荧光棒播放状态提供器")]
    public CyalumePlaybackProvider playbackProvider;

    [Header("初始化设置")]
    [InspectorName("启动时强制重新构建")]
    public bool forceRebuildOnStart;

    [InspectorName("输出详细日志")]
    public bool verboseLog = false;

    [Header("手动播放状态覆盖（可选）")]
    [InspectorName("使用手动播放状态")]
    public bool useManualPlaybackState;

    [InspectorName("手动图案 ID")]
    public int manualPatternId;

    [InspectorName("手动图案开始时间")]
    public float manualPatternStartTime;

    [InspectorName("手动播放速度")]
    public float manualPlaySpeed = 1f;

    [InspectorName("手动编舞类型")]
    public int manualChoreographyType;

    private IEnumerator Start()
    {
        if (controller3D == null)
            controller3D = GetComponent<CyalumeController3D>();

        // 避免误拾取到挂在角色模型层级上的控制器
        if (controller3D == null)
        {
            var candidates = GetComponentsInChildren<CyalumeController3D>(true);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && !IsCharacterHierarchy(candidates[i].gameObject))
                {
                    controller3D = candidates[i];
                    break;
                }
            }
        }

        if (controller3D == null)
        {
            var controllerHost = ResolveControllerHost();
            controller3D = controllerHost.AddComponent<CyalumeController3D>();

            if (verboseLog)
            {
                Debug.Log(
                    $"[CyalumeAutoBinder] Added missing CyalumeController3D " +
                    $"at runtime on '{controllerHost.name}'.");
            }
        }

        if (playbackProvider == null)
            playbackProvider = GetComponent<CyalumePlaybackProvider>();

        // 避免误拾取到挂在角色模型层级上的提供器
        if (playbackProvider == null)
        {
            var candidates = GetComponentsInChildren<CyalumePlaybackProvider>(true);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] != null && !IsCharacterHierarchy(candidates[i].gameObject))
                {
                    playbackProvider = candidates[i];
                    break;
                }
            }
        }

        if (playbackProvider == null)
        {
            var providerHost =
                controller3D != null ? controller3D.gameObject : gameObject;

            playbackProvider =
                providerHost.AddComponent<CyalumePlaybackProvider>();

            if (verboseLog)
            {
                Debug.Log(
                    $"[CyalumeAutoBinder] Added missing CyalumePlaybackProvider " +
                    $"at runtime on '{providerHost.name}'.");
            }
        }

        if (musicId <= 0 &&
            Gallop.Live.Director.instance != null &&
            Gallop.Live.Director.instance.live != null)
        {
            musicId = Gallop.Live.Director.instance.live.MusicId;
        }

        if (playbackProvider != null && musicId > 0)
            playbackProvider.InitializeForMusicId(musicId);

        if (controller3D == null)
        {
            Debug.LogWarning(
                "[CyalumeAutoBinder] CyalumeController3D not found.");

            yield break;
        }

        controller3D.SetVerboseLog(verboseLog);
        controller3D.SetMusicIdOverride(musicId);
        controller3D.SetPlaybackProvider(playbackProvider);

        if (useManualPlaybackState)
        {
            controller3D.SetManualPlaybackState(
                manualPatternId,
                manualPatternStartTime,
                manualPlaySpeed,
                manualChoreographyType);
        }
        else
        {
            controller3D.ClearManualPlaybackState();
        }

        if (verboseLog)
        {
            string controllerHostName =
                controller3D != null
                    ? controller3D.gameObject.name
                    : "<null>";

            string providerHostName =
                playbackProvider != null
                    ? playbackProvider.gameObject.name
                    : "<null>";

            Debug.Log(
                $"[CyalumeAutoBinder] Start: musicId={musicId}, " +
                $"provider={(playbackProvider != null)}@{providerHostName}, " +
                $"controller={(controller3D != null)}@{controllerHostName}");
        }

        yield return controller3D.SetupOfficialLike(forceRebuildOnStart);
    }

    /// <summary>
    /// 解析 CyalumeController3D 的合法宿主 GameObject。
    /// 严禁绑定在角色模型层级，严格校验 AssetHolder 中是否含有荧光棒资产；
    /// 若在 stage 和场景中未找到任何符合条件的宿主，默认安全回退到 CyalumeAutoBinder 自身挂载的 GameObject（即 mainLive 根节点）。
    /// </summary>
    private GameObject ResolveControllerHost()
    {
        // 1. 优先检查自身挂载的 AssetHolder
        var selfHolder = GetComponent<AssetHolder>();
        if (selfHolder != null && !IsCharacterHierarchy(selfHolder.gameObject) && HasCyalumeAssets(selfHolder))
        {
            return selfHolder.gameObject;
        }

        // 2. 检查子物体中的所有 AssetHolder（舞台 Stage 通常挂在 mainLive 的子层级）
        var childHolders = GetComponentsInChildren<AssetHolder>(true);
        if (childHolders != null)
        {
            for (int i = 0; i < childHolders.Length; i++)
            {
                var holder = childHolders[i];
                if (holder == null || holder.gameObject == null)
                    continue;

                // 严格过滤角色模型层级
                if (IsCharacterHierarchy(holder.gameObject))
                    continue;

                // 校验是否真正含有荧光棒资产条目
                if (HasCyalumeAssets(holder))
                    return holder.gameObject;
            }
        }

        // 3. 在场景所有对象中查找符合条件的 AssetHolder（针对舞台对象未作为子节点的边缘情况）
        var sceneHolders = FindObjectsOfType<AssetHolder>(true);
        if (sceneHolders != null)
        {
            for (int i = 0; i < sceneHolders.Length; i++)
            {
                var holder = sceneHolders[i];
                if (holder == null || holder.gameObject == null)
                    continue;

                if (IsCharacterHierarchy(holder.gameObject))
                    continue;

                if (HasCyalumeAssets(holder))
                    return holder.gameObject;
            }
        }

        // 4. 如果在 stage 和场景中未找到任何符合条件的宿主，默认安全回退到 CyalumeAutoBinder 自身挂载的 GameObject（即 mainLive 根节点）
        if (verboseLog)
        {
            Debug.Log("[CyalumeAutoBinder] 未在舞台或场景中找到合法荧光棒 AssetHolder，安全回退至 mainLive 根节点。");
        }
        return gameObject;
    }

    /// <summary>
    /// 严格过滤角色模型层级：
    /// 严禁在名字包含 pfb_bdy、pfb_hed、pfb_hair、pfb_chr，或者所属 GameObject/父级挂有 UmaContainerCharacter 的对象上绑定 CyalumeController3D。
    /// </summary>
    private static bool IsCharacterHierarchy(GameObject go)
    {
        if (go == null)
            return true;

        // 检查所属 GameObject 或其任意祖先节点是否挂有 UmaContainerCharacter
        if (go.GetComponentInParent<UmaContainerCharacter>() != null)
            return true;

        // 检查自身名称是否包含角色部件关键字
        string name = go.name;
        if (!string.IsNullOrEmpty(name))
        {
            if (name.IndexOf("pfb_bdy", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("pfb_hed", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("pfb_hair", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("pfb_chr", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        // 逐级向上检查祖先节点名称是否包含角色部件关键字
        Transform current = go.transform.parent;
        while (current != null)
        {
            string parentName = current.name;
            if (!string.IsNullOrEmpty(parentName))
            {
                if (parentName.IndexOf("pfb_bdy", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    parentName.IndexOf("pfb_hed", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    parentName.IndexOf("pfb_hair", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    parentName.IndexOf("pfb_chr", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            current = current.parent;
        }

        return false;
    }

    /// <summary>
    /// 校验 AssetHolder 真实内容：
    /// 检查其 _assetTable 中的 key 或 candidate 名称是否包含 "default"、"random"、"cyalume"，
    /// 只有确认包含荧光棒条目的 AssetHolder 才视为合法宿主。
    /// </summary>
    private static bool HasCyalumeAssets(AssetHolder holder)
    {
        if (holder == null || holder._assetTable == null || holder._assetTable.list == null)
            return false;

        for (int i = 0; i < holder._assetTable.list.Count; i++)
        {
            var pair = holder._assetTable.list[i];
            if (pair == null)
                continue;

            // 检查 Key 名称是否包含关键词
            string keyText = pair.Key != null ? pair.Key.ToString() : string.Empty;
            if (!string.IsNullOrEmpty(keyText))
            {
                if (keyText.IndexOf("default", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    keyText.IndexOf("random", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    keyText.IndexOf("cyalume", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // 检查候选资源对象 (GameObject 等) 的名称是否包含关键词
            if (pair.Value != null)
            {
                string valueName = pair.Value.name;
                if (!string.IsNullOrEmpty(valueName))
                {
                    if (valueName.IndexOf("default", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        valueName.IndexOf("random", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                        valueName.IndexOf("cyalume", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}