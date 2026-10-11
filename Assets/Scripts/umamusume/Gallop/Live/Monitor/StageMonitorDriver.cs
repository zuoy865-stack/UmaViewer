using System;
using System.Collections.Generic;
using System.Reflection;
using Gallop.Live.Cutt;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gallop.Live
{
    public partial class StageMonitorDriver : MonoBehaviour
    {
        [Header("绑定与调试")]
        public bool verboseLog = false, includeInactiveRenderers = true, rebuildCacheOnEnable = true, rebuildCacheWhenTargetMissing = true, autoInitializeProvider = true;

        [Header("播放与匹配")]
        public bool assignMaskTextureToFilterTex = false, clearFadeTextureWhenUnused = true, applyRenderQueue = true, applyBlendModeProperties = true;
        public string monitorShaderName = "Gallop/3D/Live/Stage/Monitor";

        [Header("着色器属性名称")]
        public string mainTexProperty = "_MainTex", filterTexProperty = "_FilterTex", fadeTexProperty = "_FadeTex";
        public string alphaProperty = "_Alpha", colorFadeProperty = "_ColorFade", baseColorProperty = "_BaseColor";
        public string monitorWidthProperty = "_MonitorWidth", monitorHeightProperty = "_MonitorHeight", crossFadeRateProperty = "_CrossFadeRate";
        public string srcBlendModeProperty = "_SrcBlendMode", dstBlendModeProperty = "_DstBlendMode", srcBlendProperty = "_SrcBlend", dstBlendProperty = "_DstBlend", zWriteProperty = "_ZWrite";

        private LiveTimelineControl _ctl;
        private StageController _stage;
        private MonitorUvMovieProvider _provider;
        private bool _hasBuiltCache, _hasMonitorTimelineData, _providerContextReady;
        private int _lastPreparedMusicId = -1, _lastPreparedStageInstanceId = int.MinValue;

        private readonly List<MonitorMaterialBinding> _bindings = new List<MonitorMaterialBinding>(32);
        private readonly Dictionary<string, List<MonitorMaterialBinding>> _bindingCache = new Dictionary<string, List<MonitorMaterialBinding>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingBindingLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingClipLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rebuildAttemptedForMissingBinding = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MonitorMaterialBinding> _resolveBuffer = new List<MonitorMaterialBinding>(8);
        private readonly HashSet<MonitorMaterialBinding> _activeBindingsThisFrame = new HashSet<MonitorMaterialBinding>();
        private static readonly List<MonitorMaterialBinding> EmptyBindingList = new List<MonitorMaterialBinding>(0);

        private void OnEnable()
        {
            BindIfPossible();
            if (rebuildCacheOnEnable)
                RebuildCache();
        }

        private void OnDisable()
        {
            // 退出或禁用时将所有屏幕恢复完全透明并隐藏，杜绝遮挡夕阳与残留黑屏
            SetAllBindingsIdle();
            Unbind();
            ClearCaches();
        }

        private void LateUpdate()
        {
            if (_ctl == null || _stage == null || _provider == null)
                BindIfPossible();

            // 若控制器、舞台或 Provider 未就绪，或无时间轴监视器数据，确保全部监视器处于透明隐藏空闲状态
            if (_ctl == null || _stage == null || _provider == null || !_hasMonitorTimelineData)
            {
                SetAllBindingsIdle();
                return;
            }

            // Provider 尚未准备就绪时同样保持空闲透明，杜绝出现初始大黑板遮挡夕阳
            if (!EnsureProviderReady())
            {
                SetAllBindingsIdle();
                return;
            }

            if (!_hasBuiltCache)
                RebuildCache();

            ApplyMonitorTimeline();
        }

        private void BindIfPossible()
        {
            Director dir = Director.instance;
            if (!dir)
                return;

            LiveTimelineControl newCtl = dir._liveTimelineControl;
            StageController newStage = dir._stageController;
            if (newCtl == null || newStage == null) return;

            MonitorUvMovieProvider newProvider = GetComponent<MonitorUvMovieProvider>();
            if (newProvider == null) newProvider = dir.GetComponent<MonitorUvMovieProvider>();
            if (newProvider == null) newProvider = FindObjectOfType<MonitorUvMovieProvider>();
            if (newProvider == null && autoInitializeProvider)
            {
                newProvider = dir.gameObject.GetComponent<MonitorUvMovieProvider>();
                if (newProvider == null) newProvider = dir.gameObject.AddComponent<MonitorUvMovieProvider>();
            }

            bool changed = _ctl != newCtl || _stage != newStage || _provider != newProvider;
            _ctl = newCtl; _stage = newStage; _provider = newProvider;
            if (!changed) return;

            ClearCaches();
            _hasMonitorTimelineData = HasMonitorTimelineData(_ctl);
            _providerContextReady = false;
            _lastPreparedMusicId = -1;
            _lastPreparedStageInstanceId = int.MinValue;

            if (verboseLog) Debug.Log($"[StageMonitorDriver] bound, hasMonitorTimelineData={_hasMonitorTimelineData}");
        }

        private void Unbind()
        {
            _ctl = null; _stage = null; _provider = null;
            _hasMonitorTimelineData = false; _providerContextReady = false;
            _lastPreparedMusicId = -1; _lastPreparedStageInstanceId = int.MinValue;
        }

        private void ClearCaches()
        {
            _bindings.Clear();
            _bindingCache.Clear();
            _missingBindingLogged.Clear();
            _missingClipLogged.Clear();
            _rebuildAttemptedForMissingBinding.Clear();
            _resolveBuffer.Clear();
            _activeBindingsThisFrame.Clear();
            _hasBuiltCache = false;
        }

        private bool EnsureProviderReady()
        {
            if (_provider == null || !autoInitializeProvider) return _provider != null;
            int musicId = Director.instance?.live?.MusicId ?? 0;
            if (musicId <= 0) return false;

            bool musicChanged = _lastPreparedMusicId != musicId || _provider.LoadedMusicId != musicId;
            bool stageChanged = _stage != null && _lastPreparedStageInstanceId != _stage.GetInstanceID();
            bool alreadyLoaded = _provider.LoadedMusicId == musicId && _provider.HasTriedLoad;
            if (!alreadyLoaded || musicChanged)
            {
                bool forceReload = _provider.LoadedMusicId > 0 && _provider.LoadedMusicId != musicId;
                bool loaded = _provider.InitializeForMusicId(musicId, forceReload);
                _providerContextReady = false;
                if (verboseLog) Debug.Log($"[StageMonitorDriver] provider initialize musicId={musicId}, loaded={loaded}, clips={_provider.clips?.Count ?? 0}");
            }

            _lastPreparedMusicId = musicId;
            bool synced = false;
            if (!_providerContextReady || stageChanged || _provider.ContextSlotCount == 0)
            {
                synced = _provider.RebuildContextSlotsFromLiveSettings(_ctl, musicId, force: stageChanged || musicChanged);
                _providerContextReady = synced || _provider.ContextSlotCount > 0;
            }

            if (_stage != null) _lastPreparedStageInstanceId = _stage.GetInstanceID();
            return _provider.ContextSlotCount > 0;
        }

        private static bool HasMonitorTimelineData(LiveTimelineControl timelineControl)
        {
            if (timelineControl?.data?.worksheetList == null) return false;
            List<LiveTimelineWorkSheet> worksheets = timelineControl.data.worksheetList;
            for (int i = 0; i < worksheets.Count; i++)
            {
                LiveTimelineWorkSheet workSheet = worksheets[i];
                if (workSheet?.monitorControlList != null && workSheet.monitorControlList.Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 判断指定渲染器是否属于点唱机屏幕节点：节点名为 monitor 或包含 monitor_audio（兼顾父节点包含 monitor_audio）
        /// </summary>
        private static bool IsAudioMonitorRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            string name = renderer.name;
            if (string.Equals(name, "monitor", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.IndexOf("monitor_audio", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            Transform p = renderer.transform.parent;
            while (p != null)
            {
                if (p.name.IndexOf("monitor_audio", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                p = p.parent;
            }
            return false;
        }

        public void RebuildCache()
        {
            _bindings.Clear();
            _bindingCache.Clear();
            _missingBindingLogged.Clear();
            _activeBindingsThisFrame.Clear();
            _hasBuiltCache = true;
            if (_stage == null) return;

            Renderer[] renderers = _stage.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                Material[] materials;
                try { materials = renderer.materials; }
                catch (Exception ex)
                {
                    if (verboseLog) Debug.LogWarning($"[StageMonitorDriver] failed to read materials from {renderer.name}: {ex.Message}");
                    continue;
                }
                if (materials == null || materials.Length == 0) continue;

                // 适配点唱机屏幕节点：如果节点名为 monitor 或包含 monitor_audio，即使材质为 Default（mtl_env_live10146_default000）也纳入绑定
                bool isAudioMonitor = IsAudioMonitorRenderer(renderer);

                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null) continue;

                    bool isMonitorMat = IsMonitorMaterial(material);
                    if (!isMonitorMat && !isAudioMonitor) continue;

                    // 若属于点唱机屏幕节点且缺少监视器着色器，则动态查找 Gallop/3D/Live/Stage/Monitor 赋予它
                    if (isAudioMonitor)
                    {
                        if (material.shader == null || material.shader.name.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Shader monitorShader = Shader.Find(monitorShaderName);
                            if (monitorShader != null) material.shader = monitorShader;
                        }
                    }

                    // 如果材质名称包含 StageMonitorBlendTransparent 或 monitor，确保具备半透明混合能力
                    EnsureTransparentBlend(material);

                    MonitorMaterialBinding binding = new MonitorMaterialBinding
                    {
                        renderer = renderer, material = material,
                        rendererKey = NormalizeName(renderer.name), materialKey = NormalizeName(material.name),
                        rendererCompact = CompactName(renderer.name), materialCompact = CompactName(material.name),
                        isAudioMonitor = isAudioMonitor
                    };
                    binding.groupKey = BuildGroupKey(binding);

                    // 记录原始主纹理与 UV 缩放偏移，供平滑回退时完整保留原始 MainTex
                    if (HasTextureProperty(material, mainTexProperty))
                    {
                        binding.baseMainTex = material.GetTexture(mainTexProperty);
                        binding.baseMainScale = material.GetTextureScale(mainTexProperty);
                        binding.baseMainOffset = material.GetTextureOffset(mainTexProperty);
                    }

                    if (!string.IsNullOrEmpty(filterTexProperty) && material.HasProperty(filterTexProperty))
                    {
                        binding.baseFilterScale = material.GetTextureScale(filterTexProperty);
                        binding.baseFilterOffset = material.GetTextureOffset(filterTexProperty);
                    }

                    // 记录原始基准透明度，若初始为 0 则保底为 1f，便于后续视频播放或平滑回退时正确显示
                    float initialAlpha = 1f;
                    if (TryHasProperty(material, alphaProperty))
                    {
                        initialAlpha = material.GetFloat(alphaProperty);
                        if (initialAlpha <= 0.001f) initialAlpha = 1f;
                    }
                    binding.baseAlpha = initialAlpha;

                    if (TryHasProperty(material, colorFadeProperty)) binding.baseColorFade = material.GetColor(colorFadeProperty);
                    if (TryHasProperty(material, baseColorProperty)) binding.baseColor = material.GetColor(baseColorProperty);

                    if (TryHasProperty(material, srcBlendModeProperty)) { binding.hasSrcBlendMode = true; binding.baseSrcBlendMode = material.GetFloat(srcBlendModeProperty); }
                    else if (TryHasProperty(material, srcBlendProperty)) { binding.hasSrcBlendMode = true; binding.baseSrcBlendMode = material.GetFloat(srcBlendProperty); }

                    if (TryHasProperty(material, dstBlendModeProperty)) { binding.hasDstBlendMode = true; binding.baseDstBlendMode = material.GetFloat(dstBlendModeProperty); }
                    else if (TryHasProperty(material, dstBlendProperty)) { binding.hasDstBlendMode = true; binding.baseDstBlendMode = material.GetFloat(dstBlendProperty); }

                    // 点唱机屏幕初始保持基准不透明度（1f）与启用 Renderer，普通舞台大屏或无贴图网格初始强制透明隐藏杜绝遮挡夕阳与死黑方块
                    if (isAudioMonitor && binding.baseMainTex != null)
                    {
                        TrySetFloat(material, alphaProperty, initialAlpha);
                        if (renderer != null) renderer.enabled = true;
                    }
                    else
                    {
                        TrySetFloat(material, alphaProperty, 0f);
                        if (renderer != null) renderer.enabled = false;
                    }

                    _bindings.Add(binding);
                }
            }

            _bindings.Sort((a, b) =>
            {
                int cmp = string.Compare(a.groupKey, b.groupKey, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                cmp = string.Compare(a.materialKey, b.materialKey, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                return string.Compare(a.rendererKey, b.rendererKey, StringComparison.OrdinalIgnoreCase);
            });

            if (verboseLog) Debug.Log($"[StageMonitorDriver] cache rebuilt: bindings={_bindings.Count}");
        }

        /// <summary>
        /// 增强半透明混合兼容：
        /// 在材质绑定或初始化阶段，如果材质名称包含 StageMonitorBlendTransparent 或 monitor，
        /// 确保其具备透明混合能力（_SrcBlend = SrcAlpha, _DstBlend = OneMinusSrcAlpha, _ZWrite = 0），杜绝退化为不透明黑色。
        /// </summary>
        private void EnsureTransparentBlend(Material material)
        {
            if (material == null) return;
            string matName = material.name ?? string.Empty;
            string shaderName = material.shader != null ? material.shader.name : string.Empty;
            bool isTransparentMonitor = matName.IndexOf("StageMonitorBlendTransparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        matName.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        shaderName.IndexOf("StageMonitorBlendTransparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        shaderName.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isTransparentMonitor)
            {
                // 配置半透明混合模式：SrcBlend = SrcAlpha (5), DstBlend = OneMinusSrcAlpha (10)
                TrySetFloat(material, srcBlendModeProperty, (float)BlendMode.SrcAlpha);
                TrySetFloat(material, srcBlendProperty, (float)BlendMode.SrcAlpha);
                TrySetFloat(material, dstBlendModeProperty, (float)BlendMode.OneMinusSrcAlpha);
                TrySetFloat(material, dstBlendProperty, (float)BlendMode.OneMinusSrcAlpha);

                // 深度写入置 0，防止遮挡后方天空背景与景物
                TrySetFloat(material, zWriteProperty, 0f);

                // 确保渲染队列位于 Transparent 层级
                if (material.renderQueue < (int)RenderQueue.Transparent)
                    material.renderQueue = (int)RenderQueue.Transparent;
            }
        }

        /// <summary>
        /// 将指定监视器绑定平滑回退至原始静态贴图显示：
        /// 仅当材质具备有效的基准主纹理（baseMainTex != null）时才保持 Renderer.enabled = true 与不透明度；
        /// 若缺乏主纹理（baseMainTex == null），强制置为透明并关闭 Renderer，杜绝在背景中渲染出死黑方块。
        /// </summary>
        private void SetBindingFallback(MonitorMaterialBinding binding)
        {
            if (binding == null) return;

            // 核心防黑保护：若材质无有效基准纹理，绝不以实体不透明渲染，直接透明隐藏
            if (binding.baseMainTex == null)
            {
                if (binding.renderer != null && binding.renderer.enabled) binding.renderer.enabled = false;
                if (binding.material != null)
                {
                    TrySetFloat(binding.material, alphaProperty, 0f);
                    EnsureTransparentBlend(binding.material);
                }
                if (binding.hasAppliedState)
                {
                    MonitorShaderState state = binding.appliedState;
                    state.alpha = 0f;
                    binding.appliedState = state;
                }
                return;
            }

            if (binding.renderer != null && !binding.renderer.enabled) binding.renderer.enabled = true;
            if (binding.material != null)
            {
                float fallbackAlpha = binding.baseAlpha > 0.001f ? binding.baseAlpha : 1f;
                TrySetFloat(binding.material, alphaProperty, fallbackAlpha);
                if (HasTextureProperty(binding.material, mainTexProperty))
                {
                    binding.material.SetTexture(mainTexProperty, binding.baseMainTex);
                    binding.material.SetTextureScale(mainTexProperty, binding.baseMainScale);
                    binding.material.SetTextureOffset(mainTexProperty, binding.baseMainOffset);
                }
            }
            if (binding.hasAppliedState)
            {
                MonitorShaderState state = binding.appliedState;
                state.alpha = binding.baseAlpha > 0.001f ? binding.baseAlpha : 1f;
                binding.appliedState = state;
            }
        }

        /// <summary>
        /// 将指定监视器绑定置于空闲状态：
        /// 点唱机屏幕平滑回退保留原始显示；普通舞台大屏则隐藏 Renderer 并置 _Alpha = 0f，确保 100% 透明透光。
        /// </summary>
        private void SetBindingIdle(MonitorMaterialBinding binding)
        {
            if (binding == null) return;
            if (binding.isAudioMonitor)
            {
                SetBindingFallback(binding);
                return;
            }
            if (binding.renderer != null && binding.renderer.enabled) binding.renderer.enabled = false;
            if (binding.material != null)
            {
                TrySetFloat(binding.material, alphaProperty, 0f);
                EnsureTransparentBlend(binding.material);
            }
            if (binding.hasAppliedState)
            {
                MonitorShaderState state = binding.appliedState;
                state.alpha = 0f;
                binding.appliedState = state;
            }
        }

        /// <summary>
        /// 将所有监视器绑定置于空闲状态（点唱机屏幕回退，普通大屏透明隐藏）
        /// </summary>
        private void SetAllBindingsIdle()
        {
            for (int i = 0; i < _bindings.Count; i++) SetBindingIdle(_bindings[i]);
        }

        private void ApplyMonitorTimeline()
        {
            if (_provider == null)
                return;

            bool hasPool = _provider.clips != null && _provider.clips.Count > 0;
            bool hasSlots = _provider.ContextSlotCount > 0;
            if (!hasPool && !hasSlots)
            {
                SetAllBindingsIdle();
                return;
            }

            LiveTimelineData data = _ctl.data;
            if (data == null || data.worksheetList == null)
            {
                SetAllBindingsIdle();
                return;
            }

            float currentLiveTime = _ctl.currentLiveTime;
            float currentFrame = currentLiveTime * LiveTimelineControl.kTargetFpsF;

            _activeBindingsThisFrame.Clear();

            List<LiveTimelineWorkSheet> worksheets = data.worksheetList;
            for (int wsIndex = 0; wsIndex < worksheets.Count; wsIndex++)
            {
                LiveTimelineWorkSheet workSheet = worksheets[wsIndex];
                if (workSheet == null || workSheet.monitorControlList == null)
                    continue;

                List<LiveTimelineMonitorControlData> monitorList = workSheet.monitorControlList;
                for (int i = 0; i < monitorList.Count; i++)
                {
                    LiveTimelineMonitorControlData monitorData = monitorList[i];
                    if (monitorData == null || monitorData.keys == null)
                        continue;

                    LiveTimelineKeyMonitorControlDataList keys = monitorData.keys;
                    if (keys.Count <= 0)
                        continue;

                    if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable))
                        continue;

                    if (!keys.EnablePlayModeTimeline(_ctl.PlayMode))
                        continue;

                    LiveTimelineControl.FindTimelineKey(out LiveTimelineKey curKeyBase, out LiveTimelineKey nextKeyBase, keys, currentFrame);
                    LiveTimelineKeyMonitorControlData curKey = curKeyBase as LiveTimelineKeyMonitorControlData;
                    if (curKey == null) continue;
                    LiveTimelineKeyMonitorControlData nextKey = nextKeyBase as LiveTimelineKeyMonitorControlData;
                    string bindingName = !string.IsNullOrWhiteSpace(monitorData.SafeName) ? monitorData.SafeName : monitorData.name;

                    if (verboseLog)
                    {
                        Debug.Log($"[StageMonitorDriver] timeline hit: monitor='{bindingName}', frame={currentFrame:F2}, dispID={curKey.dispID}");
                    }

                    List<MonitorMaterialBinding> targets = ResolveBindings(bindingName);
                    if (targets.Count == 0 && rebuildCacheWhenTargetMissing && _bindings.Count > 0 && _rebuildAttemptedForMissingBinding.Add(bindingName))
                    {
                        RebuildCache();
                        targets = ResolveBindings(bindingName);
                    }
                    if (targets.Count == 0) continue;

                    // 目标 1：严格检查有效视频播放条件（dispID > 0 且存在有效主纹理）或实时摄像机画面（MonitorCamera/MultiCamera）
                    MonitorShaderState state = default;
                    bool isPlayable = false;

                    // 检查当前关键帧是否标记使用 MonitorCamera 或在 dispID == 0 且多机位处于出画状态时接入多机位实时画面
                    bool useMonitorCam = curKey.IsMonitorCameraFlag() || curKey.IsForcedUseMonitorCamera;
                    RenderTexture cameraRT = null;
                    if (useMonitorCam && Director.instance != null && Director.instance.MonitorCameraTexture != null)
                    {
                        cameraRT = Director.instance.MonitorCameraTexture;
                    }
                    else if (curKey.dispID == 0 && Director.instance != null && Director.instance.IsMultiCameraActive && Director.instance.MultiCameraToMonitorTexture != null)
                    {
                        cameraRT = Director.instance.MultiCameraToMonitorTexture;
                    }

                    if (cameraRT != null)
                    {
                        state.main.texture = cameraRT;
                        state.hasMainTexture = true;
                        state.alpha = curKey.blendFactor > 0.001f ? curKey.blendFactor : 1f;
                        state.width = curKey.size.x;
                        state.height = curKey.size.y;
                        state.colorFade = curKey.colorFade;
                        state.baseColor = curKey.BaseColor.a > 0.001f ? curKey.BaseColor : Color.white;
                        state.srcBlendMode = curKey.SrcBlendMode;
                        state.dstBlendMode = curKey.DstBlendMode;
                        state.useBlendMode = curKey.IsEnabledBlendMode;
                        state.renderQueue = curKey.RenderQueueNo;
                        state.hasRenderQueue = curKey.IsRenderQueue != 0;
                        isPlayable = true;
                    }
                    else
                    {
                        isPlayable = curKey.dispID > 0 &&
                                     TryBuildShaderState(monitorData, curKey, nextKey, currentFrame, out state) &&
                                     state.hasMainTexture && state.main.texture != null;
                    }

                    if (isPlayable)
                    {
                        // 触发有效视频/摄像机播放：恢复 Renderer.enabled = true，并按时间轴参数写入主纹理与 Alpha
                        for (int j = 0; j < targets.Count; j++)
                        {
                            MonitorMaterialBinding target = targets[j];
                            if (target == null) continue;
                            if (target.renderer != null && !target.renderer.enabled) target.renderer.enabled = true;
                            ApplyShaderState(target, state);
                            _activeBindingsThisFrame.Add(target);
                        }
                    }
                    else
                    {
                        // 完善平滑回退：当当前 Live 缺乏专属 UVMovie 切片（dispID <= 0 或 contextSlots 为空）时：
                        // 仅点唱机等自备UI封面的屏幕网格执行 SetBindingFallback 维持原始贴图；
                        // 普通舞台大屏若无实时摄像机或有效视频输入，必须 SetBindingIdle 保持 100% 透明透光，严禁误刷纯白默认底图形成白色方块
                        for (int j = 0; j < targets.Count; j++)
                        {
                            MonitorMaterialBinding target = targets[j];
                            if (target != null && !_activeBindingsThisFrame.Contains(target))
                            {
                                if (target.isAudioMonitor)
                                {
                                    SetBindingFallback(target);
                                    _activeBindingsThisFrame.Add(target);
                                }
                                else
                                {
                                    SetBindingIdle(target);
                                }
                            }
                        }
                    }
                }
            }

            // 对所有在当前时间轴帧中未激活有效播放的监视器部件统一收底，执行透明与隐藏保护
            for (int i = 0; i < _bindings.Count; i++)
            {
                MonitorMaterialBinding binding = _bindings[i];
                if (binding != null && !_activeBindingsThisFrame.Contains(binding))
                    SetBindingIdle(binding);
            }
        }

        private bool TryBuildShaderState(
            LiveTimelineMonitorControlData monitorData,
            LiveTimelineKeyMonitorControlData curKey,
            LiveTimelineKeyMonitorControlData nextKey,
            float currentFrame,
            out MonitorShaderState state)
        {
            state = default;
            bool canInterpolate = nextKey != null && nextKey.interpolateType != LiveCameraInterpolateType.None;
            float ratio = canInterpolate ? LiveTimelineControl.CalculateInterpolationValue(curKey, nextKey, currentFrame) : 0f;

            Vector2 size = canInterpolate ? Vector2.Lerp(curKey.size, nextKey.size, ratio) : curKey.size;
            float blendFactor = canInterpolate ? Mathf.Lerp(curKey.blendFactor, nextKey.blendFactor, ratio) : curKey.blendFactor;
            Color colorFade = canInterpolate ? Color.Lerp(curKey.colorFade, nextKey.colorFade, ratio) : curKey.colorFade;
            Color baseColor = canInterpolate ? Color.Lerp(curKey.BaseColor, nextKey.BaseColor, ratio) : curKey.BaseColor;
            float crossFadeRate = canInterpolate ? Mathf.Lerp(curKey.CrossFadeRate, nextKey.CrossFadeRate, ratio) : curKey.CrossFadeRate;
            float filterTexScale = canInterpolate ? Mathf.Lerp(curKey.FilterTexScale, nextKey.FilterTexScale, ratio) : curKey.FilterTexScale;

            float localTime = (currentFrame - curKey.frame) * LiveTimelineControl.kFrameToSec;
            bool isReversePlay = curKey.IsReversePlayFlag();
            if (curKey.speed < 0f) isReversePlay = !isReversePlay;
            float playbackSpeed = Mathf.Abs(curKey.speed) <= 0f ? 1f : Mathf.Abs(curKey.speed);

            MonitorUvMovieContextSlot primarySlot = ResolvePrimarySlot(monitorData, curKey);
            MonitorUvMovieContextSlot fadeSlot = ResolveFadeSlot(monitorData, curKey);

            if (primarySlot?.clip != null && TryBuildTextureState(primarySlot, primarySlot.clip, localTime, playbackSpeed, isReversePlay, curKey.playStartOffsetFrame, curKey.LightImageNo, out MonitorTextureState mainTexture))
            {
                state.main = mainTexture;
                state.hasMainTexture = true;
            }

            if (fadeSlot?.clip != null && TryBuildTextureState(fadeSlot, fadeSlot.clip, localTime, playbackSpeed, isReversePlay, curKey.playStartOffsetFrame, curKey.LightImageNo2, out MonitorTextureState fadeTexture))
            {
                state.fade = fadeTexture;
                state.hasFadeTexture = true;
            }

            if (assignMaskTextureToFilterTex && state.hasMainTexture)
                state.filterTexture = state.main.maskTexture;

            // 目标 1：计算时间轴透明度，优先使用插值后的 blendFactor，如果有效播放且未配置则保底为 1f
            float timelineAlpha = blendFactor;
            if (timelineAlpha <= 0.0001f && curKey.dispID > 0 && curKey.blendFactor <= 0.0001f && (nextKey == null || nextKey.blendFactor <= 0.0001f))
            {
                timelineAlpha = 1f;
            }
            state.alpha = Mathf.Clamp01(timelineAlpha);
            state.colorFade = colorFade;
            state.useBaseColor = !IsColorEffectivelyClear(baseColor);
            state.baseColor = state.useBaseColor ? baseColor : Color.white;
            state.width = size.x;
            state.height = size.y;
            state.crossFadeRate = crossFadeRate;
            state.filterTexScale = Mathf.Max(0.0001f, filterTexScale <= 0f ? 1f : filterTexScale);
            state.srcBlendMode = curKey.SrcBlendMode;
            state.dstBlendMode = curKey.DstBlendMode;
            state.useBlendMode = curKey.IsEnabledBlendMode;
            state.renderQueue = curKey.RenderQueueNo;
            state.hasRenderQueue = curKey.IsRenderQueue != 0;

            return state.hasMainTexture;
        }

        private MonitorUvMovieContextSlot ResolvePrimarySlot(LiveTimelineMonitorControlData monitorData, LiveTimelineKeyMonitorControlData key)
        {
            if (_provider == null || key == null) return null;
            int originalDispId = key.dispID;
            int effectiveDispId = ResolveEffectivePrimaryDispId(key);

            if (TryGetPlayableContextSlot(effectiveDispId, out MonitorUvMovieContextSlot slot)) return slot;
            if (effectiveDispId != originalDispId && TryGetPlayableContextSlot(originalDispId, out slot)) return slot;
            if ((effectiveDispId > 0 || originalDispId > 0) && TryGetFallbackContextSlot(out slot)) return slot;

            string timelineName = monitorData != null ? (!string.IsNullOrWhiteSpace(monitorData.SafeName) ? monitorData.SafeName : monitorData.name) : "<unnamed>";
            string missKey = $"{timelineName}|primary|slotId={effectiveDispId}";
            if (_missingClipLogged.Add(missKey) && verboseLog)
                Debug.LogWarning($"[StageMonitorDriver] primary official slot not found for '{timelineName}' (slotId={effectiveDispId})");
            return null;
        }

        private int ResolveEffectivePrimaryDispId(LiveTimelineKeyMonitorControlData key)
        {
            if (key?.ChangeUVSettingArray == null || key.ChangeUVSettingArray.Length == 0) return key?.dispID ?? -1;
            for (int i = 0; i < key.ChangeUVSettingArray.Length; i++)
            {
                LiveTimelineMonitorChangeUVSetting change = key.ChangeUVSettingArray[i];
                if (change != null && change.IsEnabled && change.DispID > 0 && DoesChangeConditionMatchCurrentCharacters(change.ConditionArray))
                    return change.DispID;
            }
            return key.dispID;
        }

        private MonitorUvMovieContextSlot ResolveFadeSlot(LiveTimelineMonitorControlData monitorData, LiveTimelineKeyMonitorControlData key)
        {
            if (_provider == null || key == null || key.DispID2 <= 0) return null;
            if (TryGetPlayableContextSlot(key.DispID2, out MonitorUvMovieContextSlot slot)) return slot;
            if (TryGetFallbackContextSlot(out slot)) return slot;

            string timelineName = monitorData != null ? (!string.IsNullOrWhiteSpace(monitorData.SafeName) ? monitorData.SafeName : monitorData.name) : "<unnamed>";
            string missKey = $"{timelineName}|fade|slotId={key.DispID2}";
            if (_missingClipLogged.Add(missKey) && verboseLog)
                Debug.LogWarning($"[StageMonitorDriver] fade slot not found for '{timelineName}' (slotId={key.DispID2})");
            return null;
        }

        private bool TryGetPlayableContextSlot(int slotId, out MonitorUvMovieContextSlot slot)
        {
            slot = null;
            if (_provider == null || slotId <= 0) return false;
            return _provider.TryGetContextSlot(slotId, out slot) && slot != null && slot.isEnabledLoad && slot.clip != null;
        }

        private bool TryGetFallbackContextSlot(out MonitorUvMovieContextSlot slot) => TryGetPlayableContextSlot(1, out slot);

        private bool TryBuildTextureState(MonitorUvMovieContextSlot slot, MonitorUvMovieClipData clip, float localTime, float playbackSpeed,
            bool isReversePlay, int startOffsetFrame, int lightImageNo, out MonitorTextureState state)
        {
            state = default;
            if (clip == null) return false;

            if (slot != null && !slot.useStandardMode && clip.lightTexture != null)
            {
                state.texture = clip.lightTexture;
                state.maskTexture = clip.lightMaskTexture;
                state.imageIndex = Mathf.Max(0, lightImageNo);
                state.offset = clip.texturePixelOffset;
                state.scale = Vector2.one - clip.texturePixelScale;
                return true;
            }

            if (clip.metadata == null) return false;
            MonitorUvMovieFrameInfo frameInfo = clip.metadata.FrameInfo ?? new MonitorUvMovieFrameInfo();
            int totalFrameCount = Mathf.Max(clip.FrameCount, 0);
            if (totalFrameCount <= 0) return false;

            float fps = Mathf.Max(clip.Fps, 1f);
            float moviePlaySec = totalFrameCount / fps;
            float startOffsetSec = Mathf.Max(0f, frameInfo.StartOffsetSec);
            float startLoopSec = Mathf.Max(0f, frameInfo.StartLoopSec);
            float endLoopSec = frameInfo.EndLoopSec > 0f ? frameInfo.EndLoopSec : moviePlaySec;
            endLoopSec = Mathf.Clamp(endLoopSec, 0f, moviePlaySec);
            if (endLoopSec <= startLoopSec) endLoopSec = moviePlaySec;

            float playStartSec = startOffsetFrame > 0 ? startOffsetFrame / fps : 0f;
            float setTime = Mathf.Max(0f, localTime) * Mathf.Max(0f, playbackSpeed);
            if (playStartSec > 0f) setTime += playStartSec;
            setTime = Mathf.Max(0f, setTime - startOffsetSec);

            float sampleTime = ResolveSampleTime(setTime, moviePlaySec, frameInfo.IsLoop, startLoopSec, endLoopSec, frameInfo.LoopCount, isReversePlay);
            BuildFrameUv(clip, sampleTime, fps, totalFrameCount, out int imageIndex, out int atlasIndex, out Vector2 frameOffset, out Vector2 frameScale);

            if (!clip.TryGetFrameTexture(imageIndex, out Texture2D texture) || texture == null) return false;
            clip.TryGetMaskTexture(imageIndex, out Texture2D maskTexture);

            state.texture = texture;
            state.maskTexture = maskTexture;
            state.imageIndex = imageIndex;
            state.offset = frameOffset + clip.texturePixelOffset;
            state.scale = frameScale - clip.texturePixelScale;
            return true;
        }

        private static float ResolveSampleTime(float setTime, float moviePlaySec, bool isLoop, float startLoopSec, float endLoopSec, int loopCount, bool isReversePlay)
        {
            float loopStart = isReversePlay ? Mathf.Max(0f, moviePlaySec - endLoopSec) : startLoopSec;
            float loopEnd = isReversePlay ? Mathf.Max(loopStart, moviePlaySec - startLoopSec) : endLoopSec;
            float time = setTime;

            if (isLoop)
            {
                float loopLength = Mathf.Max(loopEnd - loopStart, 1f / 60f);
                if (time > loopEnd)
                {
                    float after = time - loopEnd;
                    if (loopCount > 0)
                    {
                        float totalLoopSec = loopLength * loopCount;
                        if (after > totalLoopSec) after -= totalLoopSec;
                    }
                    time = loopStart + Mathf.Repeat(after, loopLength);
                }
            }
            else if (time > moviePlaySec)
            {
                time = moviePlaySec;
            }

            return isReversePlay ? Mathf.Max(0f, moviePlaySec - time) : time;
        }

        private static void BuildFrameUv(MonitorUvMovieClipData clip, float sampleTime, float fps, int totalFrameCount,
            out int imageIndex, out int atlasIndex, out Vector2 frameOffset, out Vector2 frameScale)
        {
            int frameIndex = Mathf.Clamp((int)(sampleTime * fps), 0, totalFrameCount - 1);
            int framesPerImage = clip.metadata != null ? Mathf.Max(clip.metadata.EffectiveFramePerImage, 1) : 1;
            int framesPerWidth = clip.metadata != null ? Mathf.Max(clip.metadata.EffectiveFramePerWidth, 1) : 1;

            imageIndex = Mathf.Clamp(frameIndex / framesPerImage, 0, Mathf.Max(clip.frameTextures.Count - 1, 0));
            atlasIndex = Mathf.Clamp(frameIndex - imageIndex * framesPerImage, 0, Mathf.Max(framesPerImage - 1, 0));

            frameScale = (clip.metadata?.FrameInfo != null && clip.metadata.FrameInfo.Size.x > 0f && clip.metadata.FrameInfo.Size.y > 0f)
                ? clip.metadata.FrameInfo.Size
                : new Vector2(1f / framesPerWidth, 1f / Mathf.Max(1, Mathf.CeilToInt((float)framesPerImage / framesPerWidth)));

            int column = atlasIndex % framesPerWidth;
            int row = atlasIndex / framesPerWidth;
            frameOffset = new Vector2(column * frameScale.x, row * frameScale.y);
        }

        private void ApplyShaderState(MonitorMaterialBinding binding, MonitorShaderState state)
        {
            Material material = binding.material;
            if (material == null) return;

            if (state.hasMainTexture && HasTextureProperty(material, mainTexProperty))
            {
                if (!binding.hasAppliedState || binding.appliedState.main.texture != state.main.texture ||
                    !Approximately(binding.appliedState.main.scale, state.main.scale) || !Approximately(binding.appliedState.main.offset, state.main.offset))
                {
                    material.SetTexture(mainTexProperty, state.main.texture);
                    material.SetTextureScale(mainTexProperty, state.main.scale);
                    material.SetTextureOffset(mainTexProperty, state.main.offset);
                }
            }

            if (HasTextureProperty(material, fadeTexProperty))
            {
                if (state.hasFadeTexture)
                {
                    if (!binding.hasAppliedState || !binding.appliedState.hasFadeTexture || binding.appliedState.fade.texture != state.fade.texture ||
                        !Approximately(binding.appliedState.fade.scale, state.fade.scale) || !Approximately(binding.appliedState.fade.offset, state.fade.offset))
                    {
                        material.SetTexture(fadeTexProperty, state.fade.texture);
                        material.SetTextureScale(fadeTexProperty, state.fade.scale);
                        material.SetTextureOffset(fadeTexProperty, state.fade.offset);
                    }
                }
                else if (clearFadeTextureWhenUnused && (!binding.hasAppliedState || binding.appliedState.hasFadeTexture))
                {
                    material.SetTexture(fadeTexProperty, null);
                    material.SetTextureScale(fadeTexProperty, Vector2.one);
                    material.SetTextureOffset(fadeTexProperty, Vector2.zero);
                }
            }

            if (HasTextureProperty(material, filterTexProperty))
            {
                Vector2 filterScale = binding.baseFilterScale * state.filterTexScale;
                if (assignMaskTextureToFilterTex && (!binding.hasAppliedState || binding.appliedState.filterTexture != state.filterTexture))
                    material.SetTexture(filterTexProperty, state.filterTexture);
                if (!binding.hasAppliedState || !Approximately(binding.appliedState.filterTexScale, state.filterTexScale))
                    material.SetTextureScale(filterTexProperty, filterScale);
                if (!binding.hasAppliedState)
                    material.SetTextureOffset(filterTexProperty, binding.baseFilterOffset);
            }

            // 目标 1：根据时间轴计算出的 Alpha 与材质基准透明度合成，确保有效视频播放时显示正常画面
            float effectiveBaseAlpha = binding.baseAlpha > 0.001f ? binding.baseAlpha : 1f;
            float appliedAlpha = Mathf.Clamp01(effectiveBaseAlpha * (state.alpha > 0f ? state.alpha : 1f));
            Color appliedColorFade = state.colorFade;
            Color appliedBaseColor = state.useBaseColor ? state.baseColor : binding.baseColor;

            if (!binding.hasAppliedState || !Approximately(binding.appliedState.alpha, appliedAlpha)) TrySetFloat(material, alphaProperty, appliedAlpha);
            if (!binding.hasAppliedState || !Approximately(binding.appliedState.colorFade, appliedColorFade)) TrySetColor(material, colorFadeProperty, appliedColorFade);
            if (!binding.hasAppliedState || !Approximately(binding.appliedState.baseColor, appliedBaseColor)) TrySetColor(material, baseColorProperty, appliedBaseColor);
            if (!binding.hasAppliedState || !Approximately(binding.appliedState.width, state.width)) TrySetFloat(material, monitorWidthProperty, state.width);
            if (!binding.hasAppliedState || !Approximately(binding.appliedState.height, state.height)) TrySetFloat(material, monitorHeightProperty, state.height);
            if (!binding.hasAppliedState || !Approximately(binding.appliedState.crossFadeRate, state.crossFadeRate)) TrySetFloat(material, crossFadeRateProperty, state.crossFadeRate);

            if (applyBlendModeProperties)
            {
                if (state.useBlendMode)
                {
                    if (!binding.hasAppliedState || !binding.appliedState.useBlendMode || binding.appliedState.srcBlendMode != state.srcBlendMode)
                    {
                        if (!TrySetFloat(material, srcBlendModeProperty, state.srcBlendMode)) TrySetFloat(material, srcBlendProperty, state.srcBlendMode);
                    }
                    if (!binding.hasAppliedState || !binding.appliedState.useBlendMode || binding.appliedState.dstBlendMode != state.dstBlendMode)
                    {
                        if (!TrySetFloat(material, dstBlendModeProperty, state.dstBlendMode)) TrySetFloat(material, dstBlendProperty, state.dstBlendMode);
                    }
                }
                else
                {
                    // 默认确保半透明混合能力并禁用 ZWrite，杜绝退化为不透明黑板
                    EnsureTransparentBlend(material);

                    if (binding.hasSrcBlendMode && (!binding.hasAppliedState || binding.appliedState.useBlendMode || binding.appliedState.srcBlendMode != Mathf.RoundToInt(binding.baseSrcBlendMode)))
                    {
                        if (!TrySetFloat(material, srcBlendModeProperty, binding.baseSrcBlendMode)) TrySetFloat(material, srcBlendProperty, binding.baseSrcBlendMode);
                    }
                    if (binding.hasDstBlendMode && (!binding.hasAppliedState || binding.appliedState.useBlendMode || binding.appliedState.dstBlendMode != Mathf.RoundToInt(binding.baseDstBlendMode)))
                    {
                        if (!TrySetFloat(material, dstBlendModeProperty, binding.baseDstBlendMode)) TrySetFloat(material, dstBlendProperty, binding.baseDstBlendMode);
                    }
                }
            }

            if (applyRenderQueue && state.hasRenderQueue && (!binding.hasAppliedState || !binding.appliedState.hasRenderQueue || binding.appliedState.renderQueue != state.renderQueue))
            {
                material.renderQueue = state.renderQueue;
            }

            MonitorShaderState storedState = state;
            storedState.alpha = appliedAlpha;
            storedState.baseColor = appliedBaseColor;
            binding.appliedState = storedState;
            binding.hasAppliedState = true;
        }

        /// <summary>
        /// 判定材质是否具备监视器屏幕特征（包含特定 Shader、名称或关键着色器贴图属性）
        /// </summary>
        private bool IsMonitorMaterial(Material material)
        {
            if (material == null) return false;
            string shaderName = material.shader != null ? material.shader.name : string.Empty;
            if (!string.IsNullOrEmpty(shaderName) && shaderName.IndexOf(monitorShaderName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if ((material.name ?? string.Empty).IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            int score = 0;
            if (HasTextureProperty(material, mainTexProperty)) score++;
            if (HasTextureProperty(material, filterTexProperty)) score++;
            if (HasTextureProperty(material, fadeTexProperty)) score++;
            if (TryHasProperty(material, alphaProperty)) score++;
            if (TryHasProperty(material, colorFadeProperty)) score++;
            return score >= 3;
        }
    }
}
