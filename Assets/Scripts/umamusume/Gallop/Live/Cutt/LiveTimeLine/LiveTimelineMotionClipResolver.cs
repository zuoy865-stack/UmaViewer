using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 时间轴角色动作剪辑 (AnimationClip) 动态解析器
    /// 包含四级自适应动作嗅探与加载链路：
    /// 1. 内存已加载 AssetBundle 嗅探
    /// 2. AbList 规范化路径 O(1) 精确匹配
    /// 3. AbMotions 专属与全局路径模糊打分匹配
    /// 4. Bundle 安全加载与多策略 AnimationClip 智能提取
    /// </summary>
    public static class LiveTimelineMotionClipResolver
    {
        // 多级内存动作缓存字典：Key 为 "{musicId}_{targetMotionName}"
        private static readonly Dictionary<string, AnimationClip> _clipCache = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);

        // 缓存同步锁，保障多线程与并发调用安全
        private static readonly object _cacheLock = new object();

        // 解析失败的负缓存。ResolveClip 会遍历所有已加载的 AssetBundle 并扫描 AbList/AbMotions，
        // 单次代价很高；一旦调用方在每帧重试（典型场景：道具动画名解析不到），
        // 就会变成「每帧把整个资源库扫一遍」。这里把已确认解析不到的名字记下来，避免重复付费。
        private static readonly HashSet<string> _failedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 清空所有内存缓存的 AnimationClip
        /// 通常在场景切换或 Live 结束时由清理流程调用
        /// </summary>
        public static void ClearCache()
        {
            lock (_cacheLock)
            {
                _clipCache.Clear();
                _failedKeys.Clear();
            }
        }

        /// <summary>
        /// 只清空解析失败的负缓存。建议在 Live 资源预载完成、正式开播之前调用一次：
        /// 既不会因为预载顺序误判成失败，也不会让失败重试变成每帧开销。
        /// </summary>
        public static void ClearFailedCache()
        {
            lock (_cacheLock)
            {
                _failedKeys.Clear();
            }
        }

        private static void CacheFailure(string cacheKey)
        {
            lock (_cacheLock)
            {
                _failedKeys.Add(cacheKey);
            }
        }

        /// <summary>
        /// 解析并获取指定的 AnimationClip
        /// </summary>
        /// <param name="motionName">动作名（如 anm_live_0101_pos01 或完整资源名）</param>
        /// <param name="musicId">当前 Live 歌曲编号（若为 0 则不限定专属歌曲目录）</param>
        /// <returns>解析成功的 AnimationClip 实例，若未找到则返回 null</returns>
        public static AnimationClip ResolveClip(string motionName, int musicId = 0)
        {
            if (string.IsNullOrEmpty(motionName))
            {
                return null;
            }

            // 提取纯文件名去除可能附带的扩展名，统一作为目标动作名
            string targetName = Path.GetFileNameWithoutExtension(motionName);
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            string cacheKey = $"{musicId}_{targetName}";

            // 0. 检查多级内存缓存与负缓存
            lock (_cacheLock)
            {
                if (_clipCache.TryGetValue(cacheKey, out var cachedClip) && cachedClip != null)
                {
                    return cachedClip;
                }

                if (_failedKeys.Contains(cacheKey))
                {
                    return null;
                }
            }

            AnimationClip resolvedClip = null;

            // 第一级：优先从当前内存中所有已加载的 AssetBundle 中嗅探 AnimationClip
            resolvedClip = SniffLoadedAssetBundles(motionName, targetName);
            if (resolvedClip != null)
            {
                CacheClip(cacheKey, resolvedClip);
                return resolvedClip;
            }

            // 第二级：通过 UmaViewerMain.Instance.AbList 针对规范化路径进行 O(1) 字典精确匹配
            UmaDatabaseEntry exactEntry = MatchExactPath(motionName, targetName, musicId);
            if (exactEntry != null)
            {
                resolvedClip = ExtractClipFromEntry(exactEntry, motionName, targetName);
                if (resolvedClip != null)
                {
                    CacheClip(cacheKey, resolvedClip);
                    return resolvedClip;
                }
            }

            // 第三级：若直查未命中，在 AbMotions 列表中按优先级次序进行模糊与前缀打分匹配
            UmaDatabaseEntry scoredEntry = MatchEntryFromAbMotions(targetName, musicId);
            if (scoredEntry != null)
            {
                // 第四级：命中 UmaDatabaseEntry 后，通过 UmaAssetManager.LoadAssetBundle 加载 Bundle 并智能提取
                resolvedClip = ExtractClipFromEntry(scoredEntry, motionName, targetName);
                if (resolvedClip != null)
                {
                    CacheClip(cacheKey, resolvedClip);
                    return resolvedClip;
                }
            }

            CacheFailure(cacheKey);
            return null;
        }

        /// <summary>
        /// 确定性解析：只走「内存 AB 嗅探 → AbList 规范化路径精确匹配」两级，**不做 AbMotions 模糊打分**。
        /// 通道 2/3（面部·眼部补正、手持物姿态）以及 swap 替换动作必须走这个入口：
        /// 它们的动作名与主通道不同构，模糊打分很容易命中别的通道甚至别的角色的剪辑。
        /// </summary>
        /// <param name="motionName">动作名</param>
        /// <param name="musicId">当前 Live 歌曲编号（若为 0 则不限定专属歌曲目录）</param>
        /// <returns>解析成功的 AnimationClip 实例，若未找到则返回 null</returns>
        public static AnimationClip ResolveClipExact(string motionName, int musicId = 0)
        {
            if (string.IsNullOrEmpty(motionName))
            {
                return null;
            }

            string targetName = Path.GetFileNameWithoutExtension(motionName);
            if (string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            string cacheKey = $"exact_{musicId}_{targetName}";

            lock (_cacheLock)
            {
                if (_clipCache.TryGetValue(cacheKey, out var cachedClip) && cachedClip != null)
                {
                    return cachedClip;
                }

                if (_failedKeys.Contains(cacheKey))
                {
                    return null;
                }
            }

            AnimationClip resolvedClip = SniffLoadedAssetBundles(motionName, targetName);

            if (resolvedClip == null)
            {
                UmaDatabaseEntry exactEntry = MatchExactPath(motionName, targetName, musicId);
                if (exactEntry != null)
                {
                    resolvedClip = ExtractClipFromEntry(exactEntry, motionName, targetName);
                }
            }

            if (resolvedClip != null)
            {
                CacheClip(cacheKey, resolvedClip);
            }
            else
            {
                CacheFailure(cacheKey);
            }

            return resolvedClip;
        }

        /// <summary>
        /// 尝试解析动作剪辑，附带布尔返回值
        /// </summary>
        /// <param name="motionName">动作名</param>
        /// <param name="musicId">歌曲编号</param>
        /// <param name="clip">输出的 AnimationClip</param>
        /// <returns>是否成功解析出非空剪辑</returns>
        public static bool TryResolveClip(string motionName, int musicId, out AnimationClip clip)
        {
            clip = ResolveClip(motionName, musicId);
            return clip != null;
        }

        /// <summary>
        /// 第一级：从 Unity 内存中已加载的 AssetBundle 列表中直接嗅探并提取动作剪辑
        /// </summary>
        private static AnimationClip SniffLoadedAssetBundles(string motionName, string targetName)
        {
            try
            {
                var loadedBundles = AssetBundle.GetAllLoadedAssetBundles();
                foreach (var bundle in loadedBundles)
                {
                    if (bundle == null) continue;

                    try
                    {
                        // 1. 优先按目标纯文件名提取
                        AnimationClip clip = bundle.LoadAsset<AnimationClip>(targetName);
                        if (clip != null) return clip;

                        // 2. 按原 motionName 提取（若不一致）
                        if (!string.Equals(motionName, targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            clip = bundle.LoadAsset<AnimationClip>(motionName);
                            if (clip != null) return clip;
                        }
                    }
                    catch
                    {
                        // 个别已释放或异常的 Bundle 忽略处理，继续检查其他 Bundle
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiveTimelineMotionClipResolver] 嗅探内存 AssetBundle 时捕获异常: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 第二级：针对标准规范路径在 AbList 中进行 O(1) 字典查找
        /// 覆盖专属 live body、专属 cutt、全局 live body、全局 cutt 等常见路径候选
        /// </summary>
        private static UmaDatabaseEntry MatchExactPath(string motionName, string targetName, int musicId)
        {
            if (UmaViewerMain.Instance == null || UmaViewerMain.Instance.AbList == null)
            {
                return null;
            }

            var abList = UmaViewerMain.Instance.AbList;
            List<string> candidates = new List<string>(16);

            // 当具有有效 musicId 时，优先构造专属目录候选
            if (musicId > 0)
            {
                string sonBody = $"3d/motion/live/body/son{musicId}/";
                string sonCutt = $"3d/motion/live/cutt/son{musicId}/";

                candidates.Add(sonBody + targetName);
                candidates.Add(sonCutt + targetName);
                candidates.Add(sonBody + targetName + ".anim");
                candidates.Add(sonCutt + targetName + ".anim");

                if (!string.Equals(motionName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(sonBody + motionName);
                    candidates.Add(sonCutt + motionName);
                }
            }

            // 全局公共路径候选
            candidates.Add($"3d/motion/live/body/{targetName}");
            candidates.Add($"3d/motion/live/cutt/{targetName}");
            candidates.Add($"3d/motion/live/{targetName}");
            candidates.Add($"3d/motion/live/body/{targetName}.anim");
            candidates.Add($"3d/motion/live/cutt/{targetName}.anim");

            if (!string.Equals(motionName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"3d/motion/live/body/{motionName}");
                candidates.Add($"3d/motion/live/cutt/{motionName}");
                candidates.Add($"3d/motion/live/{motionName}");
            }

            // motionName 本身可能已经包含完整相对路径
            candidates.Add(motionName);

            // 逐项进行 O(1) 字典直查
            for (int i = 0; i < candidates.Count; i++)
            {
                if (abList.TryGetValue(candidates[i], out var entry) && entry != null && entry.IsAssetBundle)
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// 第三级：在 AbMotions 动作资源列表中按优先级打分机制进行模糊与前缀匹配
        /// 优先级序列：
        /// 1. 专属 body (3d/motion/live/body/son{musicId}) -> 基础权重 +4000
        /// 2. 专属 cutt (3d/motion/live/cutt/son{musicId}) -> 基础权重 +3000
        /// 3. 全局 body (3d/motion/live/body 或 3d/motion/body) -> 基础权重 +2000
        /// 4. 全局 live (3d/motion/live) -> 基础权重 +1000
        /// 匹配度打分：
        /// 文件名完全一致 +1000、尾部完整匹配 +900、前缀一致 +500、包含关键字 +200
        /// </summary>
        private static UmaDatabaseEntry MatchEntryFromAbMotions(string targetName, int musicId)
        {
            if (UmaViewerMain.Instance == null || UmaViewerMain.Instance.AbMotions == null || UmaViewerMain.Instance.AbMotions.Count == 0)
            {
                return null;
            }

            string sonBodyPrefix = musicId > 0 ? $"3d/motion/live/body/son{musicId}" : null;
            string sonCuttPrefix = musicId > 0 ? $"3d/motion/live/cutt/son{musicId}" : null;

            UmaDatabaseEntry bestEntry = null;
            int bestScore = 0;

            var abMotions = UmaViewerMain.Instance.AbMotions;
            int total = abMotions.Count;

            for (int i = 0; i < total; i++)
            {
                var entry = abMotions[i];
                if (entry == null || string.IsNullOrEmpty(entry.Name) || !entry.IsAssetBundle)
                    continue;

                string entryPath = entry.Name;
                string entryFileName = Path.GetFileNameWithoutExtension(entryPath);

                // 1. 匹配度算法判断
                int matchScore = 0;
                if (string.Equals(entryFileName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    matchScore = 1000; // 文件名完全一致
                }
                else if (entryPath.EndsWith("/" + targetName, StringComparison.OrdinalIgnoreCase) ||
                         entryPath.EndsWith("/" + targetName + ".anim", StringComparison.OrdinalIgnoreCase))
                {
                    matchScore = 900;  // 路径以 /targetName 结尾
                }
                else if (entryFileName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    matchScore = 500;  // 文件名前缀匹配
                }
                else if (entryFileName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchScore = 200;  // 文件名内部包含
                }
                else if (entryPath.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchScore = 100;  // 路径内部包含
                }

                if (matchScore == 0)
                    continue;

                // 2. 优先级层级权重
                int tierScore = 0;
                if (sonBodyPrefix != null && entryPath.StartsWith(sonBodyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    tierScore = 4000;
                }
                else if (sonCuttPrefix != null && entryPath.StartsWith(sonCuttPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    tierScore = 3000;
                }
                else if (entryPath.StartsWith("3d/motion/live/body", StringComparison.OrdinalIgnoreCase) ||
                         entryPath.StartsWith("3d/motion/body", StringComparison.OrdinalIgnoreCase))
                {
                    tierScore = 2000;
                }
                else if (entryPath.StartsWith("3d/motion/live", StringComparison.OrdinalIgnoreCase))
                {
                    tierScore = 1000;
                }

                int totalScore = tierScore + matchScore;
                if (totalScore > bestScore)
                {
                    bestScore = totalScore;
                    bestEntry = entry;

                    // 若已达到专属 body 且文件名完全吻合的最高分数，直接提前返回
                    if (bestScore >= 5000)
                    {
                        break;
                    }
                }
            }

            return bestEntry;
        }

        /// <summary>
        /// 第四级：命中 UmaDatabaseEntry 后，通过 UmaAssetManager.LoadAssetBundle 加载 Bundle 并智能提取 AnimationClip
        /// </summary>
        private static AnimationClip ExtractClipFromEntry(UmaDatabaseEntry entry, string motionName, string targetName)
        {
            if (entry == null)
            {
                return null;
            }

            try
            {
                // 使用 neverUnload: true 常驻内存加载动作 Bundle
                AssetBundle bundle = UmaAssetManager.LoadAssetBundle(entry, neverUnload: true);
                if (bundle == null)
                {
                    return null;
                }

                // 策略 1：直接按 targetName 加载
                AnimationClip clip = bundle.LoadAsset<AnimationClip>(targetName);
                if (clip != null) return clip;

                // 策略 2：按原始 motionName 加载
                if (!string.IsNullOrEmpty(motionName) && !string.Equals(motionName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    clip = bundle.LoadAsset<AnimationClip>(motionName);
                    if (clip != null) return clip;
                }

                // 策略 3：按 entry.Name 纯文件名加载
                string entryFileName = Path.GetFileNameWithoutExtension(entry.Name);
                if (!string.IsNullOrEmpty(entryFileName) && !string.Equals(entryFileName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    clip = bundle.LoadAsset<AnimationClip>(entryFileName);
                    if (clip != null) return clip;
                }

                // 策略 4：遍历 Bundle 中所有 AnimationClip 进行智能打分筛选
                var allClips = bundle.LoadAllAssets<AnimationClip>();
                if (allClips != null && allClips.Length > 0)
                {
                    // 优先名称完全一致的 clip
                    clip = allClips.FirstOrDefault(c => c != null && string.Equals(c.name, targetName, StringComparison.OrdinalIgnoreCase));
                    if (clip != null) return clip;

                    // 其次名称包含目标关键字的 clip
                    clip = allClips.FirstOrDefault(c => c != null && c.name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (clip != null) return clip;

                    // 兜底返回第一个有效 clip
                    clip = allClips.FirstOrDefault(c => c != null);
                    if (clip != null) return clip;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiveTimelineMotionClipResolver] 从 Bundle ({entry.Name}) 提取动作剪辑异常: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 将提取到的 AnimationClip 写入内存缓存
        /// </summary>
        private static void CacheClip(string cacheKey, AnimationClip clip)
        {
            if (clip == null) return;
            lock (_cacheLock)
            {
                _clipCache[cacheKey] = clip;
            }
        }
    }
}
