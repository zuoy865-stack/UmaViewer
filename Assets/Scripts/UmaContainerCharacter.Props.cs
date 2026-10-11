using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// UmaContainerCharacter 的道具管理分部类 (Props Partial)
/// 负责全局 Live 道具的挂载点骨骼查找、道具实例化与 AttachedProps 字典维护
/// </summary>
public partial class UmaContainerCharacter
{
    [Header("Props")]
    /// <summary>
    /// 当前马娘已挂载的手持或附属道具字典，Key 为道具名称/标识（不区分大小写），Value 为实例化的道具 GameObject
    /// </summary>
    public Dictionary<string, GameObject> AttachedProps = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 骨骼节点快速缓存字典，避免每一帧或频繁检索骨骼树时反复调用 GetComponentsInChildren 产生 GC 开销
    /// </summary>
    private readonly Dictionary<string, Transform> _boneCache = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 骨骼缓存是否已被有效初始化的标记
    /// </summary>
    private bool _isBoneCacheInitialized = false;

    // 道具挂载定位日志的有界计数
    private static int _attachPropProbeLogCount;

    /// <summary>
    /// 强制刷新或清空当前角色的骨骼节点缓存
    /// 当角色换装、重新加载模型或骨骼树发生层级结构变动时调用
    /// </summary>
    public void InvalidateBoneCache()
    {
        _boneCache.Clear();
        _isBoneCacheInitialized = false;
    }

    /// <summary>
    /// 确保内部骨骼缓存已建立，若尚未建立则递归扫描自身所有子 Transform
    /// </summary>
    private void EnsureBoneCache()
    {
        if (_isBoneCacheInitialized && _boneCache.Count > 0)
        {
            return;
        }

        _boneCache.Clear();
        var allTransforms = GetComponentsInChildren<Transform>(true);
        if (allTransforms != null)
        {
            for (int i = 0; i < allTransforms.Length; i++)
            {
                var t = allTransforms[i];
                if (t != null && !string.IsNullOrEmpty(t.name) && !_boneCache.ContainsKey(t.name))
                {
                    _boneCache[t.name] = t;
                }
            }
        }

        _isBoneCacheInitialized = true;
    }

    /// <summary>
    /// 根据关节点名称递归查找当前马娘适合挂载道具的目标骨骼 Transform 节点。
    /// 支持精确识别手部（Hand_Attach_L/R, Wrist_L/R, Hand_L/R）、腰部（Waist/Pelvis/Spine）、
    /// 头部（Head/Head_Attach）、肘部（Elbow_L/R, Forearm_L/R）以及立式麦克风节点（Mic_Attach_00 等）。
    /// 当指定节点未找到时，针对包含 _R/_L 的关节点提供智能由近及远的分级容错回退机制，最终平稳兜底至 UpBodyBone 或自身的 transform。
    /// </summary>
    /// <param name="jointName">挂载关节点名称，例如 "Hand_Attach_R"、"Hand_Attach_L"、"Waist"、"Mic_Attach_00"</param>
    /// <returns>找到的目标挂点 Transform，若未找到则安全兜底返回 UpBodyBone 或自身的 transform，绝不返回 null</returns>
    public Transform FindAttachBone(string jointName)
    {
        // 确保骨骼缓存就绪
        EnsureBoneCache();

        // 1. 优先在骨骼树中精确查找（不区分大小写）
        if (!string.IsNullOrEmpty(jointName))
        {
            if (_boneCache.TryGetValue(jointName, out Transform exactBone) && exactBone != null)
            {
                return exactBone;
            }

            // 若带前缀/后缀命名有微小差异，进行包含式模糊匹配
            foreach (var kv in _boneCache)
            {
                if (kv.Value != null && kv.Key.IndexOf(jointName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return kv.Value;
                }
            }
        }

        // 2. 针对右侧关节点（包含 _R, _r, 或以 R 区分左右）的多级智能容错回退：
        // 优先顺序：Hand_Attach_R -> Wrist_R -> Hand_R -> Elbow_R -> Forearm_R -> Arm_R
        bool isRightSide = !string.IsNullOrEmpty(jointName) && (
            jointName.IndexOf("_R", StringComparison.OrdinalIgnoreCase) >= 0 ||
            jointName.EndsWith("R", StringComparison.OrdinalIgnoreCase) ||
            jointName.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0
        );

        if (isRightSide)
        {
            string[] rightFallbacks = new string[]
            {
                "Hand_Attach_R",
                "Wrist_R",
                "Hand_R",
                "Elbow_R",
                "Forearm_R",
                "Arm_R"
            };

            for (int i = 0; i < rightFallbacks.Length; i++)
            {
                if (_boneCache.TryGetValue(rightFallbacks[i], out Transform bone) && bone != null)
                {
                    return bone;
                }
            }

            // 模糊搜寻名称中含有 Wrist_R 或 Hand_R 的节点
            foreach (var kv in _boneCache)
            {
                if (kv.Value != null && (
                    kv.Key.IndexOf("Wrist_R", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    kv.Key.IndexOf("Hand_R", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return kv.Value;
                }
            }
        }

        // 3. 针对左侧关节点（包含 _L, _l, 或以 L 区分左右）的多级智能容错回退：
        // 优先顺序：Hand_Attach_L -> Wrist_L -> Hand_L -> Elbow_L -> Forearm_L -> Arm_L
        bool isLeftSide = !string.IsNullOrEmpty(jointName) && (
            jointName.IndexOf("_L", StringComparison.OrdinalIgnoreCase) >= 0 ||
            jointName.EndsWith("L", StringComparison.OrdinalIgnoreCase) ||
            jointName.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0
        );

        if (isLeftSide)
        {
            string[] leftFallbacks = new string[]
            {
                "Hand_Attach_L",
                "Wrist_L",
                "Hand_L",
                "Elbow_L",
                "Forearm_L",
                "Arm_L"
            };

            for (int i = 0; i < leftFallbacks.Length; i++)
            {
                if (_boneCache.TryGetValue(leftFallbacks[i], out Transform bone) && bone != null)
                {
                    return bone;
                }
            }

            // 模糊搜寻名称中含有 Wrist_L 或 Hand_L 的节点
            foreach (var kv in _boneCache)
            {
                if (kv.Value != null && (
                    kv.Key.IndexOf("Wrist_L", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    kv.Key.IndexOf("Hand_L", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return kv.Value;
                }
            }
        }

        // 4. 针对腰部节点的容错回退（Waist -> Waist_Attach -> Pelvis -> Spine）
        bool isWaist = !string.IsNullOrEmpty(jointName) && (
            jointName.IndexOf("Waist", StringComparison.OrdinalIgnoreCase) >= 0 ||
            jointName.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0 ||
            jointName.IndexOf("Hip", StringComparison.OrdinalIgnoreCase) >= 0
        );

        if (isWaist)
        {
            string[] waistFallbacks = new string[]
            {
                "Waist",
                "Waist_Attach",
                "Pelvis",
                "Spine"
            };

            for (int i = 0; i < waistFallbacks.Length; i++)
            {
                if (_boneCache.TryGetValue(waistFallbacks[i], out Transform bone) && bone != null)
                {
                    return bone;
                }
            }
        }

        // 5. 针对头部节点的容错回退（Head -> Head_Attach -> HeadBone）
        bool isHead = !string.IsNullOrEmpty(jointName) && jointName.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isHead)
        {
            if (HeadBone != null && HeadBone.transform != null)
            {
                return HeadBone.transform;
            }

            string[] headFallbacks = new string[]
            {
                "Head_Attach",
                "Head"
            };

            for (int i = 0; i < headFallbacks.Length; i++)
            {
                if (_boneCache.TryGetValue(headFallbacks[i], out Transform bone) && bone != null)
                {
                    return bone;
                }
            }
        }

        // 6. 针对立式麦克风节点（Mic_Attach_00 等）的容错回退
        bool isMic = !string.IsNullOrEmpty(jointName) && jointName.IndexOf("Mic", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isMic)
        {
            foreach (var kv in _boneCache)
            {
                if (kv.Value != null && kv.Key.IndexOf("Mic", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return kv.Value;
                }
            }
        }

        // 7. 终极兜底回退：返回 UpBodyBone，若无则返回自身的 transform
        if (UpBodyBone != null && UpBodyBone.transform != null)
        {
            return UpBodyBone.transform;
        }

        return transform;
    }

    /// <summary>
    /// 将道具 Prefab 实例化并挂载到指定的关节点骨骼上，
    /// 正确设置局部变换位姿与 Layer。若道具包含动画或骨骼网格，自动配置或确保 Animation 组件存在，
    /// 并将其登记入 AttachedProps 字典以便后续驱动与统一管理。
    /// </summary>
    /// <param name="propPrefab">道具预制体 GameObject</param>
    /// <param name="jointName">挂载目标骨骼名称，例如 "Hand_Attach_R"</param>
    /// <param name="propKey">道具字典 Key 标识（若为空则自动回退使用预制体名称）</param>
    /// <returns>实例化后的道具 GameObject，若预制体为空则安全返回 null</returns>
    public GameObject AttachProp(GameObject propPrefab, string jointName, string propKey)
    {
        if (propPrefab == null)
        {
            return null;
        }

        // 1. 递归查找到目标挂载骨骼
        Transform targetBone = FindAttachBone(jointName);

        // 定位日志：谁把什么道具挂到了哪根骨骼上。
        // 用来回答「某个站位为什么多出一件手持物 / 少了一件」—— 是哪条路径挂的、挂到哪根骨骼。
        if (_attachPropProbeLogCount < 80)
        {
            _attachPropProbeLogCount++;
            Debug.Log($"[AttachProp] 角色='{(CharaEntry != null ? CharaEntry.Name : "<null>")}'(id={(CharaEntry != null ? CharaEntry.Id : -1)}) " +
                      $"prefab='{propPrefab.name}' key='{propKey}' joint='{jointName}' " +
                      $"bone='{(targetBone != null ? targetBone.name : "<null>")}' " +
                      $"骨骼缓存={(_boneCache != null ? _boneCache.Count : -1)}");
        }

        // 2. 实例化道具并挂载至目标骨骼
        GameObject propInstance = Instantiate(propPrefab, targetBone);
        if (propInstance == null)
        {
            return null;
        }

        // 3. 重置局部位姿，并进行骨骼身高与局部缩放逆补偿
        propInstance.transform.localPosition = Vector3.zero;
        propInstance.transform.localRotation = Quaternion.identity;
        if (targetBone != null)
        {
            Vector3 boneLossy = targetBone.lossyScale;
            if (Mathf.Abs(boneLossy.x - 1f) > 0.0001f ||
                Mathf.Abs(boneLossy.y - 1f) > 0.0001f ||
                Mathf.Abs(boneLossy.z - 1f) > 0.0001f)
            {
                propInstance.transform.localScale = new Vector3(
                    Mathf.Abs(boneLossy.x) > 0.0001f ? (1f / boneLossy.x) : 1f,
                    Mathf.Abs(boneLossy.y) > 0.0001f ? (1f / boneLossy.y) : 1f,
                    Mathf.Abs(boneLossy.z) > 0.0001f ? (1f / boneLossy.z) : 1f
                );
            }
            else
            {
                propInstance.transform.localScale = Vector3.one;
            }
        }
        else
        {
            propInstance.transform.localScale = Vector3.one;
        }

        // 4. 递归同步道具层级 Layer，使其与角色挂载骨骼或角色根节点保持一致
        int targetLayer = targetBone != null ? targetBone.gameObject.layer : gameObject.layer;
        SetLayerRecursively(propInstance, targetLayer);

        // 5. 检查道具是否包含动画组件或骨骼网格，自动补齐或配置 Animation 组件以支持时间轴采样驱动
        EnsurePropAnimationComponent(propInstance);

        // 6. 确定道具标识 Key 并注册入 AttachedProps 字典
        string validKey = !string.IsNullOrEmpty(propKey) ? propKey : propPrefab.name;

        // 若字典中已存在同名道具实例，先安全分离并销毁旧实例，杜绝幽灵道具重叠
        if (AttachedProps.TryGetValue(validKey, out GameObject existingProp) && existingProp != null)
        {
            Destroy(existingProp);
        }

        AttachedProps[validKey] = propInstance;

        return propInstance;
    }

    /// <summary>
    /// 递归设置 GameObject 及其所有子物体的 Layer 层级
    /// </summary>
    private static void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null) return;

        target.layer = layer;
        var transforms = target.GetComponentsInChildren<Transform>(true);
        if (transforms != null)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    transforms[i].gameObject.layer = layer;
                }
            }
        }
    }

    /// <summary>
    /// 确保道具身上存在 Animation 组件，并配置好安全驱动属性
    /// 若预制体自身或子节点带有 SkinnedMeshRenderer 或 Animator，自动添加 Animation 组件
    /// </summary>
    private static void EnsurePropAnimationComponent(GameObject propInstance)
    {
        if (propInstance == null) return;

        Animation anim = propInstance.GetComponent<Animation>();
        if (anim == null)
        {
            bool hasSkinnedMesh = propInstance.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
            bool hasAnimator = propInstance.GetComponentInChildren<Animator>(true) != null;
            bool hasSubAnimation = propInstance.GetComponentInChildren<Animation>(true) != null;

            if (hasSkinnedMesh || hasAnimator || hasSubAnimation)
            {
                anim = propInstance.AddComponent<Animation>();
            }
        }

        if (anim != null)
        {
            // 道具动作由时间轴或 Director 统一手动采样驱动，关闭自动循环播放
            anim.playAutomatically = false;
            // 确保动画在任何视锥角度或视距下都得到持续采样，防止摄像机切镜头时动作冻结
            anim.cullingType = AnimationCullingType.AlwaysAnimate;
        }
    }

    /// <summary>
    /// 根据 Key 查找已挂载的道具 GameObject
    /// </summary>
    /// <param name="propKey">道具标识 Key</param>
    /// <returns>找到的道具 GameObject，若未找到或已被销毁则返回 null</returns>
    public GameObject GetAttachedProp(string propKey)
    {
        if (string.IsNullOrEmpty(propKey))
        {
            return null;
        }

        if (AttachedProps.TryGetValue(propKey, out GameObject prop) && prop != null)
        {
            return prop;
        }

        return null;
    }

    /// <summary>
    /// 检测指定 Key 的道具是否已经挂载且处于有效存活状态
    /// </summary>
    /// <param name="propKey">道具标识 Key</param>
    /// <returns>若道具存在且存活则返回 true，否则返回 false</returns>
    public bool HasAttachedProp(string propKey)
    {
        return GetAttachedProp(propKey) != null;
    }

    /// <summary>
    /// 从当前角色上分离指定 Key 的道具，并可选择是否销毁其实例
    /// </summary>
    /// <param name="propKey">道具标识 Key</param>
    /// <param name="destroyInstance">是否同时调用 Destroy 销毁道具 GameObject（默认为 true）</param>
    /// <returns>若成功分离则返回 true，若未找到对应道具则返回 false</returns>
    public bool DetachProp(string propKey, bool destroyInstance = true)
    {
        if (string.IsNullOrEmpty(propKey))
        {
            return false;
        }

        if (AttachedProps.TryGetValue(propKey, out GameObject propInstance))
        {
            AttachedProps.Remove(propKey);

            if (propInstance != null)
            {
                if (destroyInstance)
                {
                    Destroy(propInstance);
                }
                else
                {
                    propInstance.transform.SetParent(null);
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 分离并安全清理当前角色身上挂载的所有道具，清空 AttachedProps 字典
    /// 在角色重置、切歌或销毁时调用，杜绝场景物体残留与内存泄漏
    /// </summary>
    /// <param name="destroyInstance">是否销毁道具 GameObject 实例（默认为 true）</param>
    public void DetachAllProps(bool destroyInstance = true)
    {
        if (AttachedProps == null || AttachedProps.Count == 0)
        {
            return;
        }

        foreach (var kv in AttachedProps)
        {
            if (kv.Value != null)
            {
                if (destroyInstance)
                {
                    Destroy(kv.Value);
                }
                else
                {
                    kv.Value.transform.SetParent(null);
                }
            }
        }

        AttachedProps.Clear();
    }
}
