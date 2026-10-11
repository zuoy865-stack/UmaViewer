using System;
using System.Collections.Generic;
using UnityEngine;
using Gallop.Live.Cutt;

namespace Gallop.Live
{
    /// <summary>
    /// 天空模式枚举
    /// </summary>
    public enum StageSkyMode
    {
        OfficialTimeline = 0,       // 模式0：官方时间轴模式（基底球显，原版渐变云层，接收Timeline染色）
        InvertCloudAlpha = 1,       // 模式1：云层透明度反相模式（基底球显，云层Alpha反转 1-alpha）
        DirectExternalSky = 2,      // 模式2：外部大蓝天透视模式（禁用基底遮挡球，直接透出外部公共天球）
        InvertAlphaAndExternalSky = 3 // 模式3：云层反相 + 外部大蓝天透视组合模式
    }

    /// <summary>
    /// 舞台天空与云层透明度控制器
    /// 负责管理舞台天空底球(sky_base)、渐变云层(sky_grad)及公共天空(cmn_sky)的显示与材质替换
    /// </summary>
    public class StageSkyController : MonoBehaviour
    {
        [Header("当前天空模式")]
        [SerializeField] private StageSkyMode _currentMode = StageSkyMode.OfficialTimeline;

        [Header("缓存的天空渲染器")]
        [SerializeField] private Renderer _skyBaseRenderer;
        [SerializeField] private Renderer _skyGradRenderer;
        [SerializeField] private Renderer _cmnSkyRenderer;
        private readonly List<Renderer> _namedSkyRenderers = new List<Renderer>(8);

        // 原始材质与贴图缓存（用于无损复原）
        private Material _originalGradMaterial;
        private Texture _originalGradTexture;

        // 生成的反相云层材质与贴图
        private Material _invertedGradMaterial;
        private Texture2D _invertedGradTexture;

        // 是否已完成初始化
        private bool _isInitialized = false;

        // 专职 MaterialPropertyBlock，避免每帧产生材质克隆与 GC
        private MaterialPropertyBlock _basePropertyBlock;
        private MaterialPropertyBlock _gradPropertyBlock;
        private MaterialPropertyBlock _cmnPropertyBlock;
        private MaterialPropertyBlock _namedPropertyBlock;

        // Shader 属性 ID
        private static readonly int PropMulColor0 = Shader.PropertyToID("_MulColor0");
        private static readonly int PropColorPower = Shader.PropertyToID("_ColorPower");

        // 记录最后一次接收到的各天空部件颜色与发光强度，供模式切换时即时复原
        private Color _lastBaseColor = Color.white;
        private float _lastBasePower = 1f;
        private bool _hasBaseColor = false;

        private Color _lastGradColor = Color.white;
        private float _lastGradPower = 1f;
        private bool _hasGradColor = false;

        private Color _lastCmnColor = Color.white;
        private float _lastCmnPower = 1f;
        private bool _hasCmnColor = false;

        // 绑定的时间轴控制器引用
        private LiveTimelineControl _boundTimelineControl;

        #region 公开属性
        public StageSkyMode CurrentMode => _currentMode;
        public Renderer SkyBaseRenderer => _skyBaseRenderer;
        public Renderer SkyGradRenderer => _skyGradRenderer;
        public Renderer CmnSkyRenderer => _cmnSkyRenderer;
        public bool IsInitialized => _isInitialized;

        public Color LastBaseColor => _lastBaseColor;
        public float LastBasePower => _lastBasePower;
        public bool HasBaseColor => _hasBaseColor;

        public Color LastGradColor => _lastGradColor;
        public float LastGradPower => _lastGradPower;
        public bool HasGradColor => _hasGradColor;

        public Color LastCmnColor => _lastCmnColor;
        public float LastCmnPower => _lastCmnPower;
        public bool HasCmnColor => _hasCmnColor;
        #endregion

        private void Awake()
        {
            // 初始化 MaterialPropertyBlock 实例
            _basePropertyBlock = new MaterialPropertyBlock();
            _gradPropertyBlock = new MaterialPropertyBlock();
            _cmnPropertyBlock = new MaterialPropertyBlock();
            _namedPropertyBlock = new MaterialPropertyBlock();
        }

        private void Start()
        {
            // 若外部未提前显式调用 Initialize，则尝试自适应查找 StageController 并完成初始化
            if (!_isInitialized)
            {
                StageController stageController = GetComponentInParent<StageController>();
                if (stageController == null)
                {
                    stageController = FindObjectOfType<StageController>();
                }
                Initialize(stageController);
            }
        }

        private void Update()
        {
            // 自动侦测并绑定当前 Live 的时间轴控制器
            if (_boundTimelineControl == null && Director.instance != null && Director.instance._liveTimelineControl != null)
            {
                BindTimelineControl(Director.instance._liveTimelineControl);
            }

            // 监听 F8 按键，循环切换 4 种天空模式
            if (Input.GetKeyDown(KeyCode.F8))
            {
                CycleNextMode();
            }
        }

        /// <summary>
        /// 绑定时间轴控制器并订阅 BgColor1 事件
        /// </summary>
        /// <param name="ctl">时间轴控制器实例</param>
        public void BindTimelineControl(LiveTimelineControl ctl)
        {
            if (_boundTimelineControl == ctl)
                return;

            UnbindTimelineControl();
            _boundTimelineControl = ctl;

            if (_boundTimelineControl != null)
            {
                _boundTimelineControl.OnUpdateBgColor1 += OnUpdateBgColor1;
            }
        }

        /// <summary>
        /// 解绑时间轴控制器，防止事件悬空或内存泄漏
        /// </summary>
        public void UnbindTimelineControl()
        {
            if (_boundTimelineControl != null)
            {
                _boundTimelineControl.OnUpdateBgColor1 -= OnUpdateBgColor1;
                _boundTimelineControl = null;
            }
        }

        /// <summary>
        /// 响应时间轴 BgColor1 更新事件，精准识别天空部件并以 MaterialPropertyBlock 驱动
        /// </summary>
        /// <param name="updateInfo">时间轴下发的 BgColor1 数据</param>
        public void OnUpdateBgColor1(ref BgColor1UpdateInfo updateInfo)
        {
            StageColorLane lane = StageColorLane.FromTimeline(updateInfo.TimelineName, updateInfo.TimelineNameHash);
            if (lane.Kind != StageColorLaneKind.Sky)
                return;

            Renderer target = ResolveSkyRenderer(lane);
            if (target == null)
                return;

            Color col = updateInfo.color;
            float power = updateInfo.colorPower > 0f ? updateInfo.colorPower : 1f;
            ApplyColorToRenderer(target, BlockFor(target), col, power);
            RememberSkyColor(lane.SkyPart, col, power);
        }

        private Renderer ResolveSkyRenderer(in StageColorLane lane)
        {
            switch (lane.SkyPart)
            {
                case StageSkyPart.Base:
                    return _skyBaseRenderer;
                case StageSkyPart.Grad:
                    return _skyGradRenderer;
                case StageSkyPart.Common:
                    return _cmnSkyRenderer;
                default:
                    return FindNamedSkyRenderer(lane);
            }
        }

        private Renderer FindNamedSkyRenderer(in StageColorLane lane)
        {
            Renderer exact = MatchSkyByHashOrName(_skyBaseRenderer, lane);
            if (exact != null) return exact;
            exact = MatchSkyByHashOrName(_skyGradRenderer, lane);
            if (exact != null) return exact;
            exact = MatchSkyByHashOrName(_cmnSkyRenderer, lane);
            if (exact != null) return exact;

            for (int i = 0; i < _namedSkyRenderers.Count; i++)
            {
                exact = MatchSkyByHashOrName(_namedSkyRenderers[i], lane);
                if (exact != null)
                    return exact;
            }

            return null;
        }

        private static Renderer MatchSkyByHashOrName(Renderer renderer, in StageColorLane lane)
        {
            if (renderer == null)
                return null;

            string rendererName = StageColorBinder.StripCloneToken(renderer.name);
            string objectName = renderer.gameObject != null ? StageColorBinder.StripCloneToken(renderer.gameObject.name) : rendererName;

            if (!string.IsNullOrEmpty(lane.StrippedName))
            {
                if (string.Equals(rendererName, lane.StrippedName, StringComparison.OrdinalIgnoreCase))
                    return renderer;
                if (string.Equals(objectName, lane.StrippedName, StringComparison.OrdinalIgnoreCase))
                    return renderer;
            }

            if (lane.FnvHash != 0)
            {
                if (FNVHash.Generate(rendererName) == lane.FnvHash)
                    return renderer;
                if (!string.IsNullOrEmpty(objectName) && FNVHash.Generate(objectName) == lane.FnvHash)
                    return renderer;
            }

            return null;
        }

        private MaterialPropertyBlock BlockFor(Renderer renderer)
        {
            if (renderer == _skyBaseRenderer) return _basePropertyBlock;
            if (renderer == _skyGradRenderer) return _gradPropertyBlock;
            if (renderer == _cmnSkyRenderer) return _cmnPropertyBlock;
            if (_namedPropertyBlock == null)
                _namedPropertyBlock = new MaterialPropertyBlock();
            return _namedPropertyBlock;
        }

        private void RememberSkyColor(StageSkyPart part, Color col, float power)
        {
            if (part == StageSkyPart.Base)
            {
                _lastBaseColor = col;
                _lastBasePower = power;
                _hasBaseColor = true;
            }
            else if (part == StageSkyPart.Grad)
            {
                _lastGradColor = col;
                _lastGradPower = power;
                _hasGradColor = true;
            }
            else if (part == StageSkyPart.Common)
            {
                _lastCmnColor = col;
                _lastCmnPower = power;
                _hasCmnColor = true;
            }
        }

        /// <summary>
        /// 使用 MaterialPropertyBlock 将时间轴颜色与发光强度直接注入目标渲染器
        /// 避免生成 Material 实例克隆，且无论 sharedMaterial 是原版还是反相材质均能实时精准生效
        /// </summary>
        private void ApplyColorToRenderer(Renderer r, MaterialPropertyBlock block, Color color, float power)
        {
            if (r == null || block == null)
                return;

            r.GetPropertyBlock(block);
            block.SetColor(PropMulColor0, color);
            if (power > 0f)
                block.SetFloat(PropColorPower, power);
            r.SetPropertyBlock(block);
        }

        /// <summary>
        /// 初始化控制器，缓存渲染器并生成反相材质
        /// </summary>
        /// <param name="stageController">关联的舞台控制器（允许为空，为空时搜索自身及其子节点）</param>
        public void Initialize(StageController stageController)
        {
            // 收集所有候选渲染器
            List<Renderer> candidateRenderers = new List<Renderer>();

            if (stageController != null)
            {
                candidateRenderers.AddRange(stageController.GetComponentsInChildren<Renderer>(true));
            }

            // 同时也搜索自身节点树下的渲染器，确保不遗漏
            Renderer[] selfRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in selfRenderers)
            {
                if (!candidateRenderers.Contains(r))
                {
                    candidateRenderers.Add(r);
                }
            }

            // 根据命名规则识别并缓存天空渲染器
            foreach (Renderer r in candidateRenderers)
            {
                if (r == null) continue;

                string rName = r.name;
                string goName = r.gameObject.name;

                if (_skyBaseRenderer == null && (rName.IndexOf("sky_base", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 goName.IndexOf("sky_base", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _skyBaseRenderer = r;
                }
                else if (_skyGradRenderer == null && (rName.IndexOf("sky_grad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                      goName.IndexOf("sky_grad", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _skyGradRenderer = r;
                }
                else if (_cmnSkyRenderer == null && (rName.IndexOf("cmn_sky", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     goName.IndexOf("cmn_sky", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _cmnSkyRenderer = r;
                }
            }

            // 若公共天球不在舞台节点树下，进行场景全局搜索兜底
            if (_cmnSkyRenderer == null)
            {
                Renderer[] allSceneRenderers = FindObjectsOfType<Renderer>(true);
                foreach (Renderer r in allSceneRenderers)
                {
                    if (r == null) continue;
                    string rName = r.name;
                    string goName = r.gameObject.name;
                    if (rName.IndexOf("cmn_sky", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        goName.IndexOf("cmn_sky", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cmnSkyRenderer = r;
                        break;
                    }
                }
            }

            CollectNamedSkyRenderers(candidateRenderers);

            // 缓存云层网格的原始材质与贴图
            if (_skyGradRenderer != null && _skyGradRenderer.sharedMaterial != null)
            {
                _originalGradMaterial = _skyGradRenderer.sharedMaterial;
                _originalGradTexture = _originalGradMaterial.mainTexture;
                if (_originalGradTexture == null && _originalGradMaterial.HasProperty("_MainTex"))
                {
                    _originalGradTexture = _originalGradMaterial.GetTexture("_MainTex");
                }

                // 创建 Alpha 反转贴图与克隆材质
                SetupInvertedMaterial();
            }

            // 10147/1175 也走官方时间轴，不自动反相云层 Alpha。
            _currentMode = StageSkyMode.OfficialTimeline;

            _isInitialized = true;

            // 应用当前模式配置
            ApplyMode(_currentMode);

            // 尝试即时绑定当前 Live 时间轴
            if (Director.instance != null && Director.instance._liveTimelineControl != null)
            {
                BindTimelineControl(Director.instance._liveTimelineControl);
            }
        }

        /// <summary>
        /// 判定当前是否为 10147 舞台（例如 1175 Live《ハロー・ポ拉里斯》专属舞台）
        /// 支持通过 Director 当前 Live 歌曲/背景编号、StageController 与父节点命名、候选渲染器与材质多维度自适应识别
        /// </summary>
        /// <param name="stageController">关联的舞台控制器实例</param>
        /// <param name="candidateRenderers">收集到的候选天空与场景渲染器列表</param>
        /// <returns>若判定为 10147 舞台则返回 true</returns>
        private void CollectNamedSkyRenderers(List<Renderer> candidateRenderers)
        {
            _namedSkyRenderers.Clear();
            if (candidateRenderers == null)
                return;

            for (int i = 0; i < candidateRenderers.Count; i++)
            {
                Renderer r = candidateRenderers[i];
                if (r == null)
                    continue;
                if (r == _skyBaseRenderer || r == _skyGradRenderer || r == _cmnSkyRenderer)
                    continue;
                if (!StageColorLane.NameLooksLikeSky(r.name) && !StageColorLane.NameLooksLikeSky(r.gameObject.name))
                    continue;
                if (!_namedSkyRenderers.Contains(r))
                    _namedSkyRenderers.Add(r);
            }
        }

        public bool IsStage10147(StageController stageController, IEnumerable<Renderer> candidateRenderers = null)
        {
            // 1. 检测 Director 当前 Live 歌曲或背景 ID
            if (Director.instance != null && Director.instance.live != null)
            {
                var live = Director.instance.live;
                if (live.MusicId == 1175) return true;
                if (!string.IsNullOrEmpty(live.BackGroundId) && live.BackGroundId.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            // 2. 检测 StageController 对象命名及其父节点命名
            if (stageController != null)
            {
                if (stageController.gameObject.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (stageController.transform.parent != null && stageController.transform.parent.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            // 3. 检测自身对象与父节点命名
            if (gameObject.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (transform.parent != null && transform.parent.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 4. 遍历候选渲染器物体与材质命名（如 pfb_env_live10147_sky000, mtl_env_live10147_sky001 等）
            if (candidateRenderers != null)
            {
                foreach (Renderer r in candidateRenderers)
                {
                    if (r == null) continue;
                    if (r.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (r.gameObject.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                    Material sharedMat = null;
                    try { sharedMat = r.sharedMaterial; } catch { }
                    if (sharedMat != null && sharedMat.name.IndexOf("10147", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 基于 RenderTexture 拷贝不可读贴图并反转 Alpha 通道
        /// </summary>
        private void SetupInvertedMaterial()
        {
            if (_originalGradMaterial == null || _originalGradTexture == null)
            {
                return;
            }

            // 生成反相 Alpha 的贴图
            _invertedGradTexture = CreateInvertedAlphaTexture(_originalGradTexture);

            // 克隆原始材质并替换主贴图
            _invertedGradMaterial = new Material(_originalGradMaterial);
            _invertedGradMaterial.name = $"{_originalGradMaterial.name}_InvertAlpha";

            if (_invertedGradTexture != null)
            {
                _invertedGradMaterial.mainTexture = _invertedGradTexture;
                if (_invertedGradMaterial.HasProperty("_MainTex"))
                {
                    _invertedGradMaterial.SetTexture("_MainTex", _invertedGradTexture);
                }
            }
        }

        /// <summary>
        /// 使用 RenderTexture 将贴图读取为可读 Texture2D，并执行像素 Alpha 反转
        /// 避免因 AssetBundle 贴图 isReadable == false 导致直接读取像素报错
        /// </summary>
        /// <param name="srcTex">原始贴图资源</param>
        /// <returns>反相后的 Texture2D 对象</returns>
        private Texture2D CreateInvertedAlphaTexture(Texture srcTex)
        {
            if (srcTex == null)
            {
                Debug.LogWarning("[StageSkyController] 源贴图为空，无法生成反相 Alpha 贴图。");
                return null;
            }

            int width = srcTex.width;
            int height = srcTex.height;

            // 分配临时渲染纹理
            RenderTexture rt = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear
            );

            RenderTexture previousRT = RenderTexture.active;
            Texture2D newTex = null;

            try
            {
                // 将原始贴图 Blit 到 RenderTexture 中
                Graphics.Blit(srcTex, rt);
                RenderTexture.active = rt;

                // 创建可读的 Texture2D 并抓取像素
                newTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                newTex.name = $"{srcTex.name}_InvertedAlpha";
                newTex.wrapMode = srcTex.wrapMode;
                newTex.filterMode = srcTex.filterMode;
                newTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                newTex.Apply();

                // 遍历像素数组，执行 Alpha 反转 (255 - a)
                Color32[] colors = newTex.GetPixels32();
                for (int i = 0; i < colors.Length; i++)
                {
                    colors[i].a = (byte)(255 - colors[i].a);
                }
                newTex.SetPixels32(colors);
                newTex.Apply();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StageSkyController] 反转贴图 Alpha 失败: {ex.Message}");
                if (newTex != null)
                {
                    Destroy(newTex);
                    newTex = null;
                }
            }
            finally
            {
                // 恢复之前的活动 RenderTexture 并释放临时资源
                RenderTexture.active = previousRT;
                RenderTexture.ReleaseTemporary(rt);
            }

            return newTex;
        }

        /// <summary>
        /// 应用指定的天空模式
        /// </summary>
        /// <param name="mode">目标模式</param>
        public void ApplyMode(StageSkyMode mode)
        {
            _currentMode = mode;

            // 1. 基底球渲染器显隐控制（模式 2、3 禁用基底遮挡球，直接透出外部天空）
            if (_skyBaseRenderer != null)
            {
                if (mode == StageSkyMode.DirectExternalSky || mode == StageSkyMode.InvertAlphaAndExternalSky)
                {
                    _skyBaseRenderer.enabled = false;
                }
                else
                {
                    _skyBaseRenderer.enabled = true;
                    // 重新开启时立即应用缓存的最新颜色与强度
                    if (_hasBaseColor)
                    {
                        ApplyColorToRenderer(_skyBaseRenderer, _basePropertyBlock, _lastBaseColor, _lastBasePower);
                    }
                }
            }

            // 2. 渐变云层材质切换（模式 1、3 使用 Alpha 反转材质，其余使用原始材质）
            if (_skyGradRenderer != null)
            {
                if (mode == StageSkyMode.InvertCloudAlpha || mode == StageSkyMode.InvertAlphaAndExternalSky)
                {
                    if (_invertedGradMaterial != null)
                    {
                        _skyGradRenderer.sharedMaterial = _invertedGradMaterial;
                    }
                }
                else
                {
                    if (_originalGradMaterial != null)
                    {
                        _skyGradRenderer.sharedMaterial = _originalGradMaterial;
                    }
                }

                // 核心保障：材质替换后立即将最新变色通过 PropertyBlock 重新注入，保证毫秒级无缝衔接
                if (_hasGradColor)
                {
                    ApplyColorToRenderer(_skyGradRenderer, _gradPropertyBlock, _lastGradColor, _lastGradPower);
                }
            }

            // 3. 公共天球实时属性维持
            if (_cmnSkyRenderer != null && _hasCmnColor)
            {
                ApplyColorToRenderer(_cmnSkyRenderer, _cmnPropertyBlock, _lastCmnColor, _lastCmnPower);
            }

            // 输出中文日志说明当前生效的模式
            Debug.Log($"[StageSkyController] 当前天空模式已切换为: {GetModeDescription(mode)}");
        }

        /// <summary>
        /// 循环切换至下一个模式
        /// </summary>
        public void CycleNextMode()
        {
            int nextModeIndex = ((int)_currentMode + 1) % 4;
            ApplyMode((StageSkyMode)nextModeIndex);
        }

        /// <summary>
        /// 获取模式的中文详细描述说明
        /// </summary>
        public static string GetModeDescription(StageSkyMode mode)
        {
            switch (mode)
            {
                case StageSkyMode.OfficialTimeline:
                    return "模式0：官方时间轴模式（基底球显，原版渐变云层，接收Timeline染色）";
                case StageSkyMode.InvertCloudAlpha:
                    return "模式1：云层透明度反相模式（基底球显，云层Alpha反转 1-alpha）";
                case StageSkyMode.DirectExternalSky:
                    return "模式2：外部大蓝天透视模式（禁用基底遮挡球，直接透出外部公共天球）";
                case StageSkyMode.InvertAlphaAndExternalSky:
                    return "模式3：云层反相 + 外部大蓝天透视组合模式";
                default:
                    return mode.ToString();
            }
        }

        private void OnDisable()
        {
            UnbindTimelineControl();
        }

        private void OnDestroy()
        {
            UnbindTimelineControl();

            // 释放动态生成的材质和贴图，防止内存泄漏
            if (_invertedGradMaterial != null)
            {
                Destroy(_invertedGradMaterial);
                _invertedGradMaterial = null;
            }

            if (_invertedGradTexture != null)
            {
                Destroy(_invertedGradTexture);
                _invertedGradTexture = null;
            }
        }
    }
}
