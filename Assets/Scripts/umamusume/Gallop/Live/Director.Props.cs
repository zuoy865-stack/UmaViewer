using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Director 的道具（Live Props）扩展分部类
    /// 负责解析 Live 道具配置、搜集并加载道具与其动作 AssetBundle、将道具挂载到参演马娘对应的骨骼节点，
    /// 并将其注册到时间轴舞台物体字典与父级映射表中，使 Live 动画与时间轴 objectList 能联动驱动道具。
    /// </summary>
    public partial class Director
    {
        /// <summary>
        /// 道具运行时上下文数据，用于在时间轴播放期间高速缓存实例、Renderer 与动画组件以提升驱动效率
        /// </summary>
        private class PropRuntimeData
        {
            /// <summary>
            /// 对应时间轴 propsID 编号
            /// </summary>
            public int PropId;

            /// <summary>
            /// 道具配置名称（如 "mic_01"、"cyalume"）
            /// </summary>
            public string PropName;

            /// <summary>
            /// 道具 Prefab 原始名称
            /// </summary>
            public string PrefabName;

            /// <summary>
            /// 道具实例化 GameObject
            /// </summary>
            public GameObject Instance;

            /// <summary>
            /// 道具根节点的 Transform
            /// </summary>
            public Transform RootTransform;

            /// <summary>
            /// 道具身上的所有 Renderer 组件缓存（用于显隐控制和材质着色）
            /// </summary>
            public Renderer[] Renderers;

            /// <summary>
            /// 道具身上的 Animation 动作驱动组件
            /// </summary>
            public Animation AnimationComp;

            /// <summary>
            /// 已经尝试解析过的动作剪辑名。解析失败时只记一次，绝不在每帧重试
            /// （ResolveClip 会遍历所有已加载 AssetBundle，每帧重试等于每帧扫一遍资源库）。
            /// </summary>
            public string ResolvedClipName;

            /// <summary>
            /// 上一次已提交到 Animation 的剪辑名，用于判断是否需要重新 Play/Sample
            /// </summary>
            public string AppliedClipName;

            /// <summary>
            /// 是否已经提交过一次动画采样
            /// </summary>
            public bool HasAppliedAnim;

            /// <summary>
            /// 当前道具所属的参演马娘容器
            /// </summary>
            public UmaContainerCharacter OwnerCharacter;

            /// <summary>
            /// 初始默认挂载的目标骨骼 Transform
            /// </summary>
            public Transform DefaultAttachBone;

            /// <summary>
            /// 当前依附的父级 Transform
            /// </summary>
            public Transform CurrentAttachTransform;

            // 「道具挂到道具」时的具体挂点节点缓存（官方 _attachTargetPropNodeName 指向的节点），
            // 命中后缓存，避免每帧做 GetComponentsInChildren 遍历。
            public Transform AttachNode;
            public string AttachNodeName;
            public Transform AttachNodeParent;

            /// <summary>
            /// 参演角色槽位索引
            /// </summary>
            public int CharaSlotIndex;

            /// <summary>
            /// 上次提交给 MaterialPropertyBlock 的色彩缓存
            /// </summary>
            public Color LastColor;

            /// <summary>
            /// 上次提交给 MaterialPropertyBlock 的色彩强度倍率缓存
            /// </summary>
            public float LastColorPower = -1f;

            /// <summary>
            /// 是否已经应用过材质属性块
            /// </summary>
            public bool HasMaterialPropertyApplied = false;
        }

        /// <summary>
        /// 当前 Live 场次中所有处于激活状态的道具运行时集合
        /// </summary>
        private readonly List<PropRuntimeData> _activeLiveProps = new List<PropRuntimeData>();

        /// <summary>
        /// 按 propId 快速检索道具运行时对象的字典
        /// </summary>
        private readonly Dictionary<int, List<PropRuntimeData>> _propsByIdMap = new Dictionary<int, List<PropRuntimeData>>();

        /// <summary>
        /// 按 propsDataGroup 数组下标检索的兼容索引。
        /// 官方时间轴派发的是「道具编号」(major*100+minor)，不是数组下标；
        /// 只有编号查不到时才回退到这里，避免拿下标当编号驱动到别的道具（这正是货不对板的来源之一）。
        /// </summary>
        private readonly List<List<PropRuntimeData>> _propsByIndex = new List<List<PropRuntimeData>>();

        // 道具条件判定定位日志的有界计数
        private static int _propConditionProbeLogCount;
        private static int _propSettingFlagsProbeLogCount;

        // 道具挂点解析的定位日志计数
        private static int _propAttachNodeProbeLogCount;

        /// <summary>
        /// 解析「道具挂到另一个道具」时的具体挂点节点。
        /// 官方用 _attachTargetPropNodeName 指定父级道具内部的节点名，找不到才退回父级道具根节点。
        /// 结果按（节点名, 父级）缓存，避免每帧遍历子节点。
        /// </summary>
        private static Transform ResolveAttachNode(PropRuntimeData prop, PropRuntimeData parentProp, string nodeName)
        {
            Transform root = parentProp.RootTransform;

            if (string.IsNullOrEmpty(nodeName))
                return root;

            if (prop.AttachNode != null &&
                string.Equals(prop.AttachNodeName, nodeName, StringComparison.Ordinal) &&
                prop.AttachNodeParent == root)
            {
                return prop.AttachNode;
            }

            Transform found = root.Find(nodeName);

            if (found == null)
            {
                var all = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && string.Equals(all[i].name, nodeName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = all[i];
                        break;
                    }
                }
            }

            if (found == null)
            {
                if (_propAttachNodeProbeLogCount < 20)
                {
                    _propAttachNodeProbeLogCount++;
                    Debug.LogWarning($"[PropsAttach] 父级道具 '{root.name}' 内未找到挂点节点 '{nodeName}'，退回父级根节点");
                }

                found = root;
            }
            else if (_propAttachNodeProbeLogCount < 20)
            {
                _propAttachNodeProbeLogCount++;
                Debug.Log($"[PropsAttach] 道具挂到父级道具 '{root.name}' 的节点 '{found.name}'");
            }

            prop.AttachNode = found;
            prop.AttachNodeName = nodeName;
            prop.AttachNodeParent = root;

            return found;
        }

        // settingFlags 过滤用的复用缓冲，避免每帧分配
        private List<PropRuntimeData> _propTargetScratch;

        /// <summary>
        /// 按官方 settingFlags 位掩码过滤目标站位。
        ///
        /// 官方 Director 里的判据是：
        ///     if (key.settingFlags &lt; 1)  → 本条更新整体不适用；
        ///     if (((key.settingFlags &gt;&gt; charaIndex) &amp; 1) == 0) → 该站位不适用。
        /// 也就是说 settingFlags 是「按站位排列的位掩码」，决定本次道具的显隐/动画作用于哪些站位。
        ///
        /// 忽略它会把更新广播给所有持有同编号道具的站位 —— 表现为「某站位多出一件手持物、另一个站位少一件」。
        /// settingFlags &lt; 1 时视为数据未提供，不做过滤（保持旧行为，避免整体挂空）。
        /// </summary>
        private List<PropRuntimeData> FilterPropTargetsByFlags(List<PropRuntimeData> targets, int settingFlags)
        {
            if (targets == null || targets.Count == 0)
                return null;

            // 注意：这里【不能】因为 targets.Count == 1 就跳过过滤。
            // 位掩码描述的是"本次更新属于哪个站位"，与持有者的数量无关：
            // 单目标 + 位掩码指向别的站位时，官方会整条跳过，而跳过过滤就会把
            // 别的站位的隐藏指令误加到这一件上（"某站位的手持物凭空消失"就是这么来的）。
            if (settingFlags < 1)
                return targets;

            if (_propTargetScratch == null)
                _propTargetScratch = new List<PropRuntimeData>(targets.Count);
            else
                _propTargetScratch.Clear();

            for (int i = 0; i < targets.Count; i++)
            {
                var prop = targets[i];
                if (prop == null)
                    continue;

                int slot = prop.CharaSlotIndex;
                if (slot < 0 || slot >= 31)
                    continue;

                if (((settingFlags >> slot) & 1) != 0)
                    _propTargetScratch.Add(prop);
            }

            return _propTargetScratch.Count > 0 ? _propTargetScratch : null;
        }

        /// <summary>
        /// 把条件组内容压成一行，便于在日志里直接看出「这件道具为什么挂/不挂在这个站位」。
        /// </summary>
        private static string DescribePropConditions(LiveTimelinePropsSettings.PropsConditionGroup[] groups)
        {
            if (groups == null || groups.Length == 0)
                return "(无条件组 → 直接挂)";

            var sb = new System.Text.StringBuilder();

            for (int g = 0; g < groups.Length && g < 3; g++)
            {
                var group = groups[g];
                if (group == null)
                {
                    sb.Append("g").Append(g).Append("=null; ");
                    continue;
                }

                sb.Append("g").Append(g).Append('{');
                if (group.satisfiesAllConditions) sb.Append("ALL,");
                if (group.IsInvalid) sb.Append("EXCLUDE,");

                var cd = group.propsConditionData;
                if (cd == null)
                {
                    sb.Append("noCond}");
                }
                else
                {
                    for (int c = 0; c < cd.Length; c++)
                    {
                        if (cd[c] == null)
                            continue;

                        sb.Append(cd[c].Type).Append('=').Append(cd[c].Value).Append(';');
                    }
                    sb.Append('}');
                }

                sb.Append(' ');
            }

            if (groups.Length > 3)
                sb.Append("(其余 ").Append(groups.Length - 3).Append(" 组略)");

            return sb.ToString();
        }

        /// <summary>
        /// 专用于道具材质颜色与着色强度刷新的 MaterialPropertyBlock，避免每帧产生 GC 内存开销
        /// </summary>
        private MaterialPropertyBlock _propPropertyBlock;

        /// <summary>
        /// 惰性获取复用的道具材质属性块
        /// </summary>
        private MaterialPropertyBlock PropPropertyBlock => _propPropertyBlock ?? (_propPropertyBlock = new MaterialPropertyBlock());

        /// <summary>
        /// Shader 着色器属性 ID 静态预哈希缓存，消除字符串检索开销
        /// </summary>
        private static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
        private static readonly int ShaderPropCharaColor = Shader.PropertyToID("_CharaColor");
        private static readonly int ShaderPropColorPower = Shader.PropertyToID("_ColorPower");
        private static readonly int ShaderPropSaturation = Shader.PropertyToID("_Saturation");

        /// <summary>
        /// 扫描并收集当前 Live 所需的角色道具与道具动作 AssetBundle 资源条目
        /// 涵盖角色手持道具模型包（3d/chara/prop/）、当前 Live 专属道具动作包（3d/motion/live/prop/son{musicId}）
        /// 以及通用公共道具动作包（3d/motion/live/prop/cmn）
        /// </summary>
        /// <param name="live">Live 歌曲配置条目</param>
        /// <returns>待预载的道具及动作 AssetBundle 资源列表</returns>
        public static List<UmaDatabaseEntry> CollectLivePropsEntries(LiveEntry live)
        {
            var result = new List<UmaDatabaseEntry>();
            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return result;

            // 1. 角色道具模型/预制体资源前缀（手持道具如麦克风、折扇、魔法棒、荧光棒等）
            const string propCharaPrefix = "3d/chara/prop/";

            // 2. 当前 Live 专属的道具动作包前缀（如麦克风折叠展开、甩扇动作等）
            string propMotionPrefix = (live != null && live.MusicId > 0)
                ? $"3d/motion/live/prop/son{live.MusicId}"
                : null;

            // 3. 通用公共道具动作包前缀
            const string cmnPropMotionPrefix = "3d/motion/live/prop/cmn";

            foreach (var kv in main.AbList)
            {
                UmaDatabaseEntry entry = kv.Value;
                if (entry == null || !entry.IsAssetBundle)
                    continue;

                // 检查资源 key 相对路径以及 entry.Name 是否匹配道具模型前缀
                bool isCharaProp = (kv.Key != null && kv.Key.StartsWith(propCharaPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                   (entry.Name != null && entry.Name.StartsWith(propCharaPrefix, StringComparison.OrdinalIgnoreCase));

                // 检查资源 key 相对路径以及 entry.Name 是否匹配当前歌曲专属道具动作前缀
                bool isPropMotion = propMotionPrefix != null && (
                    (kv.Key != null && kv.Key.StartsWith(propMotionPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (entry.Name != null && entry.Name.StartsWith(propMotionPrefix, StringComparison.OrdinalIgnoreCase)));

                // 检查是否匹配公共道具动作前缀
                bool isCmnPropMotion = (kv.Key != null && kv.Key.StartsWith(cmnPropMotionPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                       (entry.Name != null && entry.Name.StartsWith(cmnPropMotionPrefix, StringComparison.OrdinalIgnoreCase));

                if (isCharaProp || isPropMotion || isCmnPropMotion)
                {
                    result.Add(entry);
                }
            }

            return result
                .Where(e => e != null && !string.IsNullOrEmpty(e.Name))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// 根据当前 Live 轨道配置（propsSettings）初始化并挂载所有演出道具，
        /// 建立运行时映射以供时间轴驱动，并订阅时间轴道具刷新事件。
        /// </summary>
        public void InitializeLiveProps()
        {
            // 在装配新一轮道具前，先清理旧实例与字典映射，杜绝内存泄漏和场景幽灵残留
            ClearLiveProps();

            if (_liveTimelineControl == null || _liveTimelineControl.data == null)
                return;

            var propsSettings = _liveTimelineControl.data.propsSettings;
            if (propsSettings == null || propsSettings.propsDataGroup == null || propsSettings.propsDataGroup.Length == 0)
                return;

            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return;

            Debug.Log($"[Director.Props] 正在初始化 Live 道具配置，道具组总数：{propsSettings.propsDataGroup.Length}");

            for (int propIndex = 0; propIndex < propsSettings.propsDataGroup.Length; propIndex++)
            {
                var propData = propsSettings.propsDataGroup[propIndex];
                if (propData == null || !propData.isCharaProps)
                    continue;

                // 1. 根据 majorId、minorId 或 propsName 定位道具 AssetBundle 条目
                UmaDatabaseEntry propEntry = FindPropAssetBundleEntry(propData, main.AbList);
                if (propEntry == null)
                {
                    Debug.LogWarning($"[Director.Props] 未匹配到道具 AssetBundle 资源: propsName='{propData.propsName}', majorId={propData.charaPropsMajorId}, minorId={propData.charaPropsMinorId}");
                    continue;
                }

                // 2. 加载 AssetBundle 并提取道具 Prefab
                AssetBundle bundle = UmaAssetManager.LoadAssetBundle(propEntry, true);
                if (bundle == null)
                {
                    Debug.LogWarning($"[Director.Props] 加载道具 AssetBundle 失败: {propEntry.Name}");
                    continue;
                }

                GameObject prefab = LoadPropPrefab(bundle, propData, propEntry);
                if (prefab == null)
                {
                    Debug.LogWarning($"[Director.Props] 道具 AssetBundle 中未找到有效预制体: {propEntry.Name}");
                    continue;
                }

                // 3. 获取初始挂载骨骼关节点名称（默认回退为 Hand_Attach_R）
                string attachJointName = "Hand_Attach_R";
                if (propData.attachJointNames != null && propData.attachJointNames.Length > 0)
                {
                    for (int j = 0; j < propData.attachJointNames.Length; j++)
                    {
                        if (!string.IsNullOrEmpty(propData.attachJointNames[j]))
                        {
                            attachJointName = propData.attachJointNames[j];
                            break;
                        }
                    }
                }

                // 4. 官方道具编号 = major * 100 + minor（见 ResourcePath.GetPropHighId / GetPropLowId）。
                //    时间轴派发的 propsID 就是这个编号，必须按它登记，不能用数组下标。
                int timelinePropId = MakeTimelinePropId(propData);

                // 4. 遍历参演角色容器，匹配条件并挂载道具
                if (CharaContainerScript == null || CharaContainerScript.Count == 0)
                    continue;

                for (int slotIndex = 0; slotIndex < CharaContainerScript.Count; slotIndex++)
                {
                    var container = CharaContainerScript[slotIndex];
                    if (container == null)
                        continue;

                    // 检查当前参演角色是否满足道具挂载条件（槽位/角色ID/服装等）
                    bool propConditionMatched = IsPropConditionMatched(slotIndex, container, propData.propsConditionGroup);

                    // 定位日志：把（道具 × 站位）的条件判定结果与条件内容打出来，
                    // 直接回答「为什么这个站位多了/少了这件手持物」。
                    if (_propConditionProbeLogCount < 200)
                    {
                        _propConditionProbeLogCount++;
                        Debug.Log($"[PropsCond] prop='{propData.propsName}' id={propData.charaPropsMajorId}_{propData.charaPropsMinorId:D2} " +
                                  $"slot={slotIndex} " +
                                  $"chara={(container.CharaEntry != null ? container.CharaEntry.Id : -1)} " +
                                  $"-> include={propConditionMatched}  " +
                                  $"conditions={DescribePropConditions(propData.propsConditionGroup)}");
                    }

                    if (!propConditionMatched)
                        continue;

                    // 实例化并挂载道具到角色对应骨骼节点
                    string propKey = !string.IsNullOrEmpty(propData.propsName) ? propData.propsName : prefab.name;
                    GameObject instance = container.AttachProp(prefab, attachJointName, propKey);
                    if (instance == null)
                        continue;

                    // 5. 构造并登记 PropRuntimeData，以便时间轴后续高速驱动
                    var runtimeData = new PropRuntimeData
                    {
                        PropId = timelinePropId,
                        PropName = propData.propsName,
                        PrefabName = prefab.name,
                        Instance = instance,
                        RootTransform = instance.transform,
                        Renderers = instance.GetComponentsInChildren<Renderer>(true),
                        AnimationComp = instance.GetComponent<Animation>(),
                        OwnerCharacter = container,
                        DefaultAttachBone = container.FindAttachBone(attachJointName),
                        CurrentAttachTransform = instance.transform.parent,
                        CharaSlotIndex = slotIndex
                    };

                    _activeLiveProps.Add(runtimeData);

                    if (!_propsByIdMap.TryGetValue(timelinePropId, out var propList))
                    {
                        propList = new List<PropRuntimeData>();
                        _propsByIdMap[timelinePropId] = propList;
                    }
                    propList.Add(runtimeData);

                    // 兼容索引：仅当时间轴给的是数组下标时才会被用到（见 ResolvePropTargets）
                    while (_propsByIndex.Count <= propIndex)
                        _propsByIndex.Add(null);
                    if (_propsByIndex[propIndex] == null)
                        _propsByIndex[propIndex] = new List<PropRuntimeData>();
                    _propsByIndex[propIndex].Add(runtimeData);

                    // 6. 注册到 StageObjectMap 与 StageParentMap，使时间轴 objectList 也能够联动
                    RegisterPropToStageMaps(instance, propData.propsName, prefab.name);
                }
            }

            // 7. 订阅时间轴派发的道具属性与空间挂载姿态更新事件
            BindPropTimelineEvents();

            Debug.Log($"[Director.Props] Live 道具组装与事件订阅完成，活跃道具总数：{_activeLiveProps.Count}");
        }

        /// <summary>
        /// 订阅 LiveTimelineControl 的道具刷新事件
        /// </summary>
        private void BindPropTimelineEvents()
        {
            if (_liveTimelineControl == null)
                return;

            _liveTimelineControl.OnUpdateProps -= UpdateProps;
            _liveTimelineControl.OnUpdateProps += UpdateProps;

            _liveTimelineControl.OnUpdatePropsAttach -= UpdatePropsAttach;
            _liveTimelineControl.OnUpdatePropsAttach += UpdatePropsAttach;
        }

        /// <summary>
        /// 取消订阅 LiveTimelineControl 的道具刷新事件
        /// </summary>
        private void UnbindPropTimelineEvents()
        {
            if (_liveTimelineControl == null)
                return;

            _liveTimelineControl.OnUpdateProps -= UpdateProps;
            _liveTimelineControl.OnUpdatePropsAttach -= UpdatePropsAttach;
        }

        /// <summary>
        /// 响应时间轴派发的道具属性与动画刷新事件
        /// 实时驱动道具身上 Renderer 的显隐状态、着色材质参数以及 Animation 组件的播放进度
        /// </summary>
        /// <param name="propId">道具编号 (Props ID)</param>
        /// <param name="rendererEnable">道具渲染器启用状态（显隐）</param>
        /// <param name="color">道具主色调</param>
        /// <param name="colorPower">色彩强度倍率</param>
        /// <param name="clip">绑定的动画剪辑资源</param>
        /// <param name="animTime">动画播放时间偏移量（秒）</param>
        /// <param name="clipName">动画剪辑名称</param>
        public void UpdateProps(
            int propId,
            bool rendererEnable,
            Color color,
            float colorPower,
            AnimationClip clip,
            float animTime,
            string clipName,
            int settingFlags)
        {
            if (_activeLiveProps.Count == 0)
                return;

            // 定位受影响的道具运行时列表：先按官方道具编号匹配（其次回退数组下标兼容索引），
            // 再按官方 settingFlags 位掩码过滤到具体站位。
            List<PropRuntimeData> resolvedProps = ResolvePropTargets(propId);
            List<PropRuntimeData> targetProps = FilterPropTargetsByFlags(resolvedProps, settingFlags);

            if (_propSettingFlagsProbeLogCount < 40)
            {
                _propSettingFlagsProbeLogCount++;
                Debug.Log($"[PropsFlags] propId={propId} settingFlags={settingFlags}(0x{settingFlags:X}) " +
                          $"resolved={(resolvedProps != null ? resolvedProps.Count : 0)} " +
                          $"applied={(targetProps != null ? targetProps.Count : 0)} rendererEnable={rendererEnable}");
            }

            if (targetProps == null || targetProps.Count == 0)
                return;

            for (int i = 0; i < targetProps.Count; i++)
            {
                var prop = targetProps[i];
                if (prop == null || prop.Instance == null)
                    continue;

                // 1. 显隐控制：驱动所有 Renderer.enabled 与 GameObject.activeSelf
                if (prop.Renderers != null && prop.Renderers.Length > 0)
                {
                    for (int r = 0; r < prop.Renderers.Length; r++)
                    {
                        var renderer = prop.Renderers[r];
                        if (renderer != null && renderer.enabled != rendererEnable)
                        {
                            renderer.enabled = rendererEnable;
                        }
                    }
                }
                else if (prop.Instance.activeSelf != rendererEnable)
                {
                    prop.Instance.SetActive(rendererEnable);
                }

                // 2. 材质色彩与强度控制：仅在色彩或强度实质发生变化时提交 SetPropertyBlock，避免无意义开销
                if (rendererEnable && prop.Renderers != null && prop.Renderers.Length > 0)
                {
                    bool colorChanged = !prop.HasMaterialPropertyApplied ||
                                        prop.LastColor != color ||
                                        Mathf.Abs(prop.LastColorPower - colorPower) > 0.0001f;

                    if (colorChanged)
                    {
                        PropPropertyBlock.Clear();
                        PropPropertyBlock.SetColor(ShaderPropColor, color);
                        PropPropertyBlock.SetColor(ShaderPropCharaColor, color);
                        PropPropertyBlock.SetFloat(ShaderPropColorPower, colorPower);
                        PropPropertyBlock.SetFloat(ShaderPropSaturation, colorPower);

                        for (int r = 0; r < prop.Renderers.Length; r++)
                        {
                            var renderer = prop.Renderers[r];
                            if (renderer != null)
                            {
                                renderer.SetPropertyBlock(PropPropertyBlock);
                            }
                        }

                        prop.LastColor = color;
                        prop.LastColorPower = colorPower;
                        prop.HasMaterialPropertyApplied = true;
                    }
                }

                // 3. 按照官方技术规范驱动道具 Legacy Animation 播放进度并重构采样状态
                if (prop.AnimationComp != null)
                {
                    string targetClipName = null;

                    // 若传入了有效的 AnimationClip 资源
                    if (clip != null)
                    {
                        targetClipName = clip.name;
                        if (prop.AnimationComp.GetClip(targetClipName) == null)
                        {
                            prop.AnimationComp.AddClip(clip, targetClipName);
                        }
                    }
                    else if (!string.IsNullOrEmpty(clipName))
                    {
                        targetClipName = clipName;
                        if (prop.AnimationComp.GetClip(targetClipName) == null)
                        {
                            // 同一个名字只解析一次。ResolveClip 内部会遍历所有已加载的 AssetBundle
                            // 并扫描 AbList/AbMotions，若每帧重试就是每帧扫一遍资源库 —— 明确的掉帧来源。
                            if (!string.Equals(prop.ResolvedClipName, targetClipName, StringComparison.Ordinal))
                            {
                                prop.ResolvedClipName = targetClipName;

                                int musicId = (live != null) ? live.MusicId : 0;
                                var resolvedClip = LiveTimelineMotionClipResolver.ResolveClip(targetClipName, musicId);
                                if (resolvedClip != null)
                                {
                                    prop.AnimationComp.AddClip(resolvedClip, targetClipName);
                                }
                                else
                                {
                                    Debug.LogWarning($"[Director.Props] 道具动画剪辑解析失败(不再重试): {targetClipName}");
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(targetClipName))
                    {
                        var animState = prop.AnimationComp[targetClipName];
                        if (animState != null)
                        {
                            // 只有剪辑变了或采样时间真的推进了才重新 Play/Sample。
                            // Animation.Sample() 会整体重算骨架，每帧对每个道具无条件做一遍代价很高；
                            // 静止道具（animTime 不变）从此完全不再重复采样。
                            bool needApply = !prop.HasAppliedAnim ||
                                             !string.Equals(prop.AppliedClipName, targetClipName, StringComparison.Ordinal) ||
                                             Mathf.Abs(animState.time - animTime) > 0.0001f;

                            if (needApply)
                            {
                                animState.time = animTime;
                                animState.enabled = true;
                                animState.weight = 1f;
                                prop.AnimationComp.Play(targetClipName);
                                prop.AnimationComp.Sample();
                                animState.enabled = false;

                                prop.AppliedClipName = targetClipName;
                                prop.HasAppliedAnim = true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 响应时间轴派发的道具空间挂载姿态更新事件
        /// 支持根据关节点名称动态重定向挂载目标（骨骼切换），并应用插值后的空间局部位置、旋转及缩放
        /// </summary>
        /// <param name="propId">被挂载的道具编号</param>
        /// <param name="jointName">依附的目标骨骼关节点名称</param>
        /// <param name="offsetPos">局部位置偏移（经时间轴插值）</param>
        /// <param name="offsetRot">局部旋转四元数偏移（经时间轴插值）</param>
        /// <param name="offsetScale">局部缩放偏移（经时间轴插值）</param>
        /// <param name="attachType">挂载目标主体类型（角色骨骼或父级道具）</param>
        /// <param name="targetPropId">当挂载到父级道具时的目标道具编号</param>
        public void UpdatePropsAttach(
            int propId,
            string jointName,
            Vector3 offsetPos,
            Quaternion offsetRot,
            Vector3 offsetScale,
            LiveTimelineKeyPropsAttachData.AttachType attachType,
            int targetPropId,
            string attachTargetPropNodeName,
            int settingFlags)
        {
            if (_activeLiveProps.Count == 0)
                return;

            List<PropRuntimeData> targetProps = FilterPropTargetsByFlags(ResolvePropTargets(propId), settingFlags);

            if (targetProps == null || targetProps.Count == 0)
                return;

            for (int i = 0; i < targetProps.Count; i++)
            {
                var prop = targetProps[i];
                if (prop == null || prop.Instance == null || prop.RootTransform == null)
                    continue;

                // 1. 关节点动态重定向（挂载目标切换）
                Transform targetParentTransform = null;

                if (attachType == LiveTimelineKeyPropsAttachData.AttachType.Chara)
                {
                    if (!string.IsNullOrEmpty(jointName) && prop.OwnerCharacter != null)
                    {
                        targetParentTransform = prop.OwnerCharacter.FindAttachBone(jointName);
                    }
                    else
                    {
                        targetParentTransform = prop.DefaultAttachBone;
                    }
                }
                else if (attachType == LiveTimelineKeyPropsAttachData.AttachType.Prop)
                {
                    if (targetPropId >= 0 && _propsByIdMap.TryGetValue(targetPropId, out var parentPropList) && parentPropList.Count > 0)
                    {
                        // 挂载到同一槽位或首个目标道具上
                        int targetIdx = (prop.CharaSlotIndex < parentPropList.Count) ? prop.CharaSlotIndex : 0;
                        var parentProp = parentPropList[targetIdx];
                        if (parentProp != null && parentProp.RootTransform != null)
                        {
                            // 官方会先用 _attachTargetPropNodeName 在父级道具内部定位具体挂点，
                            // 找不到才退回父级道具根节点。此前直接用根节点，
                            // 于是"挂到话筒架的夹具节点上"变成了"挂到话筒架根节点" → 位置错乱。
                            targetParentTransform = ResolveAttachNode(prop, parentProp, attachTargetPropNodeName);
                        }
                    }
                }

                // 若计算出的目标父级有效且与当前父节点不同，重新执行 SetParent
                if (targetParentTransform != null && prop.RootTransform.parent != targetParentTransform)
                {
                    prop.RootTransform.SetParent(targetParentTransform, false);
                    prop.CurrentAttachTransform = targetParentTransform;
                }

                // 2. 骨骼身高与局部缩放逆补偿：
                // 当角色骨骼具有缩放（lossyScale != 1）时，计算 1f / lossyScale 进行反向补偿，
                // 确保折扇等道具在角色手部（Hand_Attach_R, Hand_Attach_L 等）不发生位移漂移与缩放畸变。
                Transform parentTransform = prop.RootTransform.parent;
                if (parentTransform != null)
                {
                    Vector3 parentLossyScale = parentTransform.lossyScale;
                    if (Mathf.Abs(parentLossyScale.x - 1f) > 0.0001f ||
                        Mathf.Abs(parentLossyScale.y - 1f) > 0.0001f ||
                        Mathf.Abs(parentLossyScale.z - 1f) > 0.0001f)
                    {
                        Vector3 invScale = new Vector3(
                            Mathf.Abs(parentLossyScale.x) > 0.0001f ? (1f / parentLossyScale.x) : 1f,
                            Mathf.Abs(parentLossyScale.y) > 0.0001f ? (1f / parentLossyScale.y) : 1f,
                            Mathf.Abs(parentLossyScale.z) > 0.0001f ? (1f / parentLossyScale.z) : 1f
                        );

                        prop.RootTransform.localPosition = Vector3.Scale(offsetPos, invScale);
                        prop.RootTransform.localRotation = offsetRot;
                        prop.RootTransform.localScale = Vector3.Scale(offsetScale, invScale);
                    }
                    else
                    {
                        prop.RootTransform.localPosition = offsetPos;
                        prop.RootTransform.localRotation = offsetRot;
                        prop.RootTransform.localScale = offsetScale;
                    }
                }
                else
                {
                    prop.RootTransform.localPosition = offsetPos;
                    prop.RootTransform.localRotation = offsetRot;
                    prop.RootTransform.localScale = offsetScale;
                }
            }
        }

        /// <summary>
        /// 在 Live 退出、切歌或重置时清理销毁所有道具实例，解绑时间轴事件，释放字典引用，杜绝内存泄漏
        /// </summary>
        public void ClearLiveProps()
        {
            // 1. 取消时间轴事件订阅
            UnbindPropTimelineEvents();

            // 2. 遍历所有参演马娘，清理其 AttachedProps 字典及挂载实例
            if (CharaContainerScript != null)
            {
                for (int i = 0; i < CharaContainerScript.Count; i++)
                {
                    var container = CharaContainerScript[i];
                    if (container != null)
                    {
                        container.DetachAllProps(true);
                    }
                }
            }

            // 3. 销毁 Director 自身登记的所有道具实例
            for (int i = 0; i < _activeLiveProps.Count; i++)
            {
                var prop = _activeLiveProps[i];
                if (prop != null && prop.Instance != null)
                {
                    Destroy(prop.Instance);
                }
            }

            _activeLiveProps.Clear();
            _propsByIdMap.Clear();

            // 4. 从 StageController 的对象映射表中剔除已失效的道具引用
            if (_stageController != null && _stageController.StageObjectMap != null)
            {
                var staleKeys = new List<string>();
                foreach (var kv in _stageController.StageObjectMap)
                {
                    if (kv.Value == null)
                    {
                        staleKeys.Add(kv.Key);
                    }
                }
                for (int i = 0; i < staleKeys.Count; i++)
                {
                    _stageController.StageObjectMap.Remove(staleKeys[i]);
                    if (_stageController.StageParentMap != null)
                    {
                        _stageController.StageParentMap.Remove(staleKeys[i]);
                    }
                }
            }

            if (_liveTimelineControl != null && _liveTimelineControl.StageObjectMap != null)
            {
                var staleKeys = new List<string>();
                foreach (var kv in _liveTimelineControl.StageObjectMap)
                {
                    if (kv.Value == null)
                    {
                        staleKeys.Add(kv.Key);
                    }
                }
                for (int i = 0; i < staleKeys.Count; i++)
                {
                    _liveTimelineControl.StageObjectMap.Remove(staleKeys[i]);
                }
            }
        }

        // 官方 ResourcePath 常量（AbList 的键是小写规范化路径，查表前统一转小写）
        private const string CharaPropPathFormat     = "3d/chara/prop/prop{0:0000}_{1:00}/pfb_chr_prop{0:0000}_{1:00}";
        private const string CharaToonPropPathFormat = "3d/chara/toonprop/toon_prop{0:0000}_{1:00}/pfb_toon_prop{0:0000}_{1:00}";
        private const string CharaRichPropPathFormat = "3d/chara/richprop/rich_prop{0:0000}_{1:00}/pfb_rich_prop{0:0000}_{1:00}";
        private const string LivePropPathFormat      = "3d/env/live/common/prop/pfb_env_live_cmn_prop{0}";

        /// <summary>
        /// 解析实际要加载的道具编号。按官方 ApplyLoadPropsId 语义：马娘全部为女性，
        /// 因此只有 isUseGenderDiffPropsId 打开时才替换为女性专用编号。
        /// </summary>
        private static void ResolveLoadPropIds(LiveTimelinePropsSettings.PropsDataGroup propData, out int majorId, out int minorId)
        {
            majorId = propData.charaPropsMajorId;
            minorId = propData.charaPropsMinorId;

            if (propData.isUseGenderDiffPropsId && propData.isCharaProps && propData.FemaleCharaPropsMajorId > 0)
            {
                majorId = propData.FemaleCharaPropsMajorId;
                minorId = propData.FemaleCharaPropsMinorId;
            }
        }

        /// <summary>
        /// 官方时间轴用的道具编号：major * 100 + minor。
        /// 由 ResourcePath.GetPropHighId(id / 100) 与 GetPropLowId(id % 100) 可反推该约定。
        /// </summary>
        private static int MakeTimelinePropId(LiveTimelinePropsSettings.PropsDataGroup propData)
        {
            ResolveLoadPropIds(propData, out int majorId, out int minorId);

            if (majorId <= 0)
                return -1;

            return majorId * 100 + minorId;
        }

        /// <summary>
        /// 构建官方的道具资源路径。落盘真实布局（取自本机 meta 库）：
        ///   3d/chara/prop/prop1001_01/pfb_chr_prop1001_01
        ///   3d/chara/toonprop/toon_prop1007_00/pfb_toon_prop1007_00
        ///   3d/chara/richprop/rich_prop1001_00/pfb_rich_prop1001_00
        /// 关键：prefab 名里 prop 与编号之间【没有】下划线。
        /// 旧实现拼成 pfb_chr_prop_1001_01，精确匹配永远不中，只能掉进子串模糊匹配从而拿错道具。
        /// </summary>
        private static string BuildPropAssetPath(LiveTimelinePropsSettings.PropsDataGroup propData)
        {
            if (!propData.isCharaProps)
            {
                return string.IsNullOrEmpty(propData.propsName)
                    ? null
                    : string.Format(LivePropPathFormat, propData.propsName);
            }

            ResolveLoadPropIds(propData, out int majorId, out int minorId);

            if (majorId <= 0)
                return null;

            if (propData.IsToonProp)
            {
                return propData.IsRichProp
                    ? string.Format(CharaRichPropPathFormat, majorId, minorId)
                    : string.Format(CharaToonPropPathFormat, majorId, minorId);
            }

            return string.Format(CharaPropPathFormat, majorId, minorId);
        }

        /// <summary>
        /// 解析时间轴派发的 propId 对应哪些道具运行时对象。
        /// 优先按官方道具编号匹配；只有编号查不到时才回退到数组下标兼容索引，
        /// 避免「下标恰好等于另一个道具的编号」时驱动到错误道具。
        /// </summary>
        private List<PropRuntimeData> ResolvePropTargets(int propId)
        {
            if (propId < 0)
                return _activeLiveProps;

            if (_propsByIdMap.TryGetValue(propId, out var mappedList) && mappedList != null && mappedList.Count > 0)
                return mappedList;

            if (propId < _propsByIndex.Count && _propsByIndex[propId] != null)
                return _propsByIndex[propId];

            return null;
        }

        /// <summary>
        /// 在全局资源列表中定位道具 AssetBundle 条目
        /// </summary>
        private static UmaDatabaseEntry FindPropAssetBundleEntry(LiveTimelinePropsSettings.PropsDataGroup propData, Dictionary<string, UmaDatabaseEntry> abList)
        {
            if (abList == null || propData == null) return null;

            string officialPath = BuildPropAssetPath(propData);
            if (string.IsNullOrEmpty(officialPath))
                return null;

            string officialKey = officialPath.Replace('\\', '/').ToLowerInvariant();

            // A. 官方路径精确匹配（AbList 的键是小写规范化路径）
            if (abList.TryGetValue(officialKey, out var exactEntry) && exactEntry != null)
                return exactEntry;

            // B. 严格尾段匹配：必须完整命中「文件夹/prefab 名」这一对，允许 sourceresources/ 之类的前缀目录。
            //    绝不再用子串模糊匹配 —— 那会命中相邻编号或同目录下的材质资源，直接导致货不对板。
            foreach (var kv in abList)
            {
                if (kv.Key == null || kv.Value == null)
                    continue;

                string candidate = kv.Key.Replace('\\', '/').ToLowerInvariant();

                if (candidate.EndsWith(officialKey, StringComparison.Ordinal) ||
                    candidate.EndsWith("/" + officialKey, StringComparison.Ordinal))
                {
                    return kv.Value;
                }
            }

            Debug.LogWarning($"[Director.Props] 未按官方路径找到道具资源: propsName='{propData.propsName}' " +
                             $"toon={propData.IsToonProp} rich={propData.IsRichProp} path='{officialPath}'");

            return null;
        }

        /// <summary>
        /// 从 AssetBundle 中提取最匹配的道具预制体 GameObject
        /// </summary>
        private static GameObject LoadPropPrefab(AssetBundle bundle, LiveTimelinePropsSettings.PropsDataGroup propData, UmaDatabaseEntry entry)
        {
            if (bundle == null) return null;

            GameObject[] prefabs = bundle.LoadAllAssets<GameObject>();
            if (prefabs == null || prefabs.Length == 0)
                return null;

            // 期望的预制体名 = 官方路径的最后一段（例如 pfb_chr_prop1001_01）
            string pfbName = null;
            string officialPath = BuildPropAssetPath(propData);
            if (!string.IsNullOrEmpty(officialPath))
            {
                int slash = officialPath.LastIndexOf('/');
                pfbName = (slash >= 0 && slash + 1 < officialPath.Length)
                    ? officialPath.Substring(slash + 1)
                    : officialPath;
            }

            // 包内只有一个预制体时直接采用
            if (prefabs.Length == 1 && prefabs[0] != null)
                return prefabs[0];

            // 1. 优先匹配官方预制体名称
            if (!string.IsNullOrEmpty(pfbName))
            {
                var match = prefabs.FirstOrDefault(p => p != null && string.Equals(p.name, pfbName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 2. 匹配 propsName
            if (!string.IsNullOrEmpty(propData.propsName))
            {
                var match = prefabs.FirstOrDefault(p => string.Equals(p.name, propData.propsName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 3. 匹配 bundle 资源文件名
            if (entry != null && !string.IsNullOrEmpty(entry.Name))
            {
                string expectedFileName = Path.GetFileName(entry.Name);
                var match = prefabs.FirstOrDefault(p => string.Equals(p.name, expectedFileName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            // 4. 不再静默回退到「第一个 GameObject」——那正是货不对板的来源之一。
            //    把候选名打出来，便于判断到底是名字对不上，还是包里确实没有目标预制体。
            Debug.LogError($"[Director.Props] 无法在道具包中确定预制体: bundle='{(entry != null ? entry.Name : "<null>")}' " +
                           $"expected='{pfbName}' 候选=[{string.Join(", ", prefabs.Where(p => p != null).Select(p => p.name))}]");

            return prefabs.FirstOrDefault(p => p != null);
        }

        /// <summary>
        /// 评估参演槽位与马娘是否满足道具挂载条件组（对齐官方 CharacterObject 里取道具上下文的判定语义）。
        ///
        /// 官方语义（这是决定「道具挂到哪个站位」的唯一依据）：
        ///   include 初值为 false；没有条件组时直接 include = true；
        ///   否则逐组求 satisfied，**第一个 satisfied 的组立即决定结果**：include = !group.IsInvalid，然后 break；
        ///   没有任何组 satisfied → include 保持 false（不挂）。
        ///
        /// satisfied 的判定：
        ///   - 只有一个条件时：Default 记为「不命中」（官方是空 break）；
        ///   - 有多个条件时：Default / 未知类型记为「命中」；
        ///   - satisfiesAllConditions ? matchCount >= conditionCount : matchCount >= 1。
        ///
        /// 此前实现的偏差（就是「道具挂错站位」的来源）：
        ///   1) 把 IsInvalid 当成"无效组直接跳过" —— 它的真实含义是【命中即排除】，
        ///      结果本该被排除的道具照挂（例如某站位多出一件手持物）；
        ///   2) 单独一个 Default 条件被当成命中 → 该组"通过"→ 直接挂；
        ///   3) DressId 条件被无条件视为满足 → 服装限定的道具挂到所有站位；
        ///   4) 任意组命中就 return true，而不是"首个命中组决定 + IsInvalid 反转"。
        /// </summary>
        private static bool IsPropConditionMatched(int slotIndex, UmaContainerCharacter container, LiveTimelinePropsSettings.PropsConditionGroup[] conditionGroups)
        {
            // 无条件组 → 直接使用该道具
            if (conditionGroups == null || conditionGroups.Length == 0)
                return true;

            int charaId = (container != null && container.CharaEntry != null) ? container.CharaEntry.Id : -1;

            // 本工程目前拿不到可用的服装 ID（UmaContainerCharacter 上的 GetDressID 属于另一个类型且返回 0），
            // 因此 DressId 条件继续保持宽松处理，不会因此把道具挂空。
            int dressId = -1;

            for (int g = 0; g < conditionGroups.Length; g++)
            {
                var group = conditionGroups[g];
                if (group == null)
                    continue;

                var conditions = group.propsConditionData;
                int conditionCount = conditions != null ? conditions.Length : 0;
                int matchCount = 0;

                if (conditionCount == 1)
                {
                    var cond = conditions[0];
                    if (cond == null)
                        continue;

                    switch (cond.Type)
                    {
                        case LiveTimelinePropsSettings.PropsConditionType.Default:
                            // 官方：单独一个 Default 不增加命中数
                            break;

                        case LiveTimelinePropsSettings.PropsConditionType.CharaPosition:
                            if (cond.Value == slotIndex) matchCount++;
                            break;

                        case LiveTimelinePropsSettings.PropsConditionType.CharaId:
                            if (charaId >= 0 && cond.Value == charaId) matchCount++;
                            break;

                        case LiveTimelinePropsSettings.PropsConditionType.DressId:
                            if (IsDressConditionMatched(dressId, cond.Value)) matchCount++;
                            break;

                        default:
                            matchCount++;
                            break;
                    }
                }
                else if (conditionCount > 1)
                {
                    for (int c = 0; c < conditionCount; c++)
                    {
                        var cond = conditions[c];
                        if (cond == null)
                            continue;

                        switch (cond.Type)
                        {
                            case LiveTimelinePropsSettings.PropsConditionType.CharaPosition:
                                if (cond.Value == slotIndex) matchCount++;
                                break;

                            case LiveTimelinePropsSettings.PropsConditionType.CharaId:
                                if (charaId >= 0 && cond.Value == charaId) matchCount++;
                                break;

                            case LiveTimelinePropsSettings.PropsConditionType.DressId:
                                if (IsDressConditionMatched(dressId, cond.Value)) matchCount++;
                                break;

                            default:
                                // 官方：多条件时 Default / 未知类型直接算满足
                                matchCount++;
                                break;
                        }
                    }
                }

                bool satisfied = group.satisfiesAllConditions
                    ? matchCount >= conditionCount
                    : matchCount >= 1;

                if (!satisfied)
                    continue;

                // 官方：第一个命中的条件组立即决定是否使用该 Prop（IsInvalid = 命中即排除）
                return !group.IsInvalid;
            }

            // 没有任何条件组命中 → 不挂（官方 include 初值就是 false）
            return false;
        }

        /// <summary>
        /// DressId 条件判定。拿不到有效服装 ID（&lt;= 0）时按"满足"处理，
        /// 保持与旧行为一致，避免因为服装 ID 语义不一致而把道具整体挂空。
        /// </summary>
        private static bool IsDressConditionMatched(int dressId, int conditionValue)
        {
            if (dressId <= 0)
                return true;

            return dressId == conditionValue;
        }

        /// <summary>
        /// 将实例化的道具注册到 LiveTimelineControl 与 StageController 的对象和父级映射表中
        /// </summary>
        private void RegisterPropToStageMaps(GameObject instance, string propsName, string prefabName)
        {
            if (instance == null) return;

            void RegisterKey(string key)
            {
                if (string.IsNullOrEmpty(key)) return;

                // 1. 注册到 _liveTimelineControl.StageObjectMap
                if (_liveTimelineControl != null)
                {
                    if (_liveTimelineControl.StageObjectMap == null)
                    {
                        _liveTimelineControl.StageObjectMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                    }
                    _liveTimelineControl.StageObjectMap[key] = instance;
                }

                // 2. 注册到 _stageController.StageObjectMap 与 StageParentMap
                if (_stageController != null)
                {
                    if (_stageController.StageObjectMap == null)
                    {
                        _stageController.StageObjectMap = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                    }
                    _stageController.StageObjectMap[key] = instance;

                    if (_stageController.StageParentMap == null)
                    {
                        _stageController.StageParentMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                    }
                    _stageController.StageParentMap[key] = instance.transform.parent;
                }
            }

            // 分别使用 propsName、prefabName 以及 instance.name 注册
            RegisterKey(propsName);
            RegisterKey(prefabName);
            RegisterKey(instance.name);

            string cleanName = instance.name.Replace("(Clone)", "");
            RegisterKey(cleanName);
        }

        /// <summary>
        /// 判定当前 Live 是否启用了指定编号的演出变体 (Variation)
        /// 默认提供安全回退实现（返回 true），避免因未配置变体条件导致道具或镜头轨道被阻断。
        /// </summary>
        /// <param name="checkVariationId">需要判定的变体编号</param>
        /// <returns>若变体有效则返回 true</returns>
        public static bool IsEnableVariationId(int checkVariationId)
        {
            // UmaViewer 当前回退机制：默认允许所有变体生效，确保所有道具与特效正常展示
            return true;
        }
    }
}
