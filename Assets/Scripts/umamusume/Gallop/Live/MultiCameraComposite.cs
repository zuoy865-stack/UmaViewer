using System;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 多机位分屏合成：吃 layer 的遮罩/分割线和过渡权重，把两路纹理合成到目标 RT。
    /// 相机启停不在这里决定，避免和 Switcher 抢开关。
    /// </summary>
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    public class MultiCameraComposite : MonoBehaviour
    {
        public enum DivideLineType
        {
            Fade = 0,
            Color = 1
        }

        #region 分屏渲染核心参数
        [Header("多机位图层配置")]
        [SerializeField] private int _multiCameraNo = 0;
        [SerializeField] private DivideLineType _lineType = DivideLineType.Color;
        [Range(0f, 0.2f)]
        [SerializeField] private float _lineThickness = 0.015f;
        [SerializeField] private Color _lineColor = Color.white;
        [Range(0f, 1f)]
        [SerializeField] private float _fadeValue = 1f;
        [SerializeField] private Vector4 _transformParameter = Vector4.zero;
        [SerializeField] private float _maskRoll = 0f;
        [SerializeField] private Vector3 _offsetMinPosition = Vector3.zero;
        [SerializeField] private Vector3 _offsetMaxPosition = Vector3.zero;
        [SerializeField] private bool _isScreenDivide = true;
        [SerializeField] private float _lineAntialiasing = 0.002f;
        [SerializeField] private bool _isCompositeActive = false;
        [SerializeField] private float _layerFadeValue = 1f;
        #endregion

        #region 相机与离屏纹理引用
        [Header("多机位输入源")]
        [SerializeField] private RenderTexture _mainTexture;
        [SerializeField] private RenderTexture _subTexture;
        [SerializeField] private Camera _cameraMain;
        [SerializeField] private Camera _cameraSub;
        #endregion

        #region 内部缓存与后处理材质
        private Material _compositeMaterial;
        private RenderTexture[] _doubleBufferRT = new RenderTexture[2];
        private int _currentBufferIndex = 0;

        private static readonly int PropMainTex = Shader.PropertyToID("_MainTex");
        private static readonly int PropSubTex = Shader.PropertyToID("_SubTex");
        private static readonly int PropDivideLineColor = Shader.PropertyToID("_DivideLineColor");
        private static readonly int PropDivideLineParam = Shader.PropertyToID("_DivideLineParam");
        private static readonly int PropDivideLineSetting = Shader.PropertyToID("_DivideLineSetting");

        private const string ShaderName = "Hidden/UmaViewer/MultiCameraComposite";
        #endregion

        #region 公开属性访问器
        public bool IsScreenDivide
        {
            get => _isScreenDivide;
            set => _isScreenDivide = value;
        }

        public Camera RenderCamera => _cameraSub;

        public float LineAntialiasing
        {
            get => _lineAntialiasing;
            set => _lineAntialiasing = value;
        }

        public int MultiCameraNo
        {
            get => _multiCameraNo;
            set => _multiCameraNo = value;
        }

        public DivideLineType LineType
        {
            get => _lineType;
            set => _lineType = value;
        }

        public float LineThickness
        {
            get => _lineThickness;
            set => _lineThickness = Mathf.Max(0f, value);
        }

        public Color LineColor
        {
            get => _lineColor;
            set => _lineColor = value;
        }

        public float FadeValue
        {
            get => _fadeValue;
            set => _fadeValue = Mathf.Clamp01(value);
        }

        /// <summary>
        /// layer 轨道自己的淡入值。最终出画权重由 LiveCameraTransitionDriver 合并后再 CommitDisplayWeight。
        /// </summary>
        public float LayerFadeValue
        {
            get => _layerFadeValue;
            set => _layerFadeValue = Mathf.Clamp01(value);
        }

        /// <summary>
        /// 提交已经合并过的显示权重。过渡中两路都要画，所以这里不再去关相机。
        /// </summary>
        public void CommitDisplayWeight(float fadeValue)
        {
            _fadeValue = Mathf.Clamp01(fadeValue);
            _isCompositeActive = _subTexture != null && _fadeValue > LiveCameraTransitionDriver.WeightEpsilon;
        }

        public void ApplySwitcherFade(float fadeValue)
        {
            CommitDisplayWeight(fadeValue);
        }

        public Vector4 TransformParameter
        {
            get => _transformParameter;
            set => _transformParameter = value;
        }

        public float MaskRoll
        {
            get => _maskRoll;
            set => _maskRoll = value;
        }

        public Vector3 OffsetMinPosition
        {
            get => _offsetMinPosition;
            set => _offsetMinPosition = value;
        }

        public Vector3 OffsetMaxPosition
        {
            get => _offsetMaxPosition;
            set => _offsetMaxPosition = value;
        }

        public bool IsCompositeActive
        {
            get => _isCompositeActive;
            set => _isCompositeActive = value;
        }

        public RenderTexture MainTexture
        {
            get => _mainTexture;
            set => _mainTexture = value;
        }

        public RenderTexture SubTexture
        {
            get => _subTexture;
            set => _subTexture = value;
        }

        public Camera CameraMain
        {
            get => _cameraMain;
            set => _cameraMain = value;
        }

        public Camera CameraSub
        {
            get => _cameraSub;
            set => _cameraSub = value;
        }
        #endregion

        #region 生命周期
        private void Awake()
        {
            EnsureMaterial();
        }

        private void OnEnable()
        {
            EnsureMaterial();
        }

        private void OnDisable()
        {
            ReleaseDoubleBuffers();
        }

        private void OnDestroy()
        {
            ReleaseDoubleBuffers();
            if (_compositeMaterial != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_compositeMaterial);
                }
                else
                {
                    DestroyImmediate(_compositeMaterial);
                }
                _compositeMaterial = null;
            }
        }
        #endregion

        #region 材质与双缓冲管理
        private bool EnsureMaterial()
        {
            if (_compositeMaterial != null)
            {
                return true;
            }

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                shader = Shader.Find("Hidden/MultiCameraComposite");
            }

            if (shader != null && shader.isSupported)
            {
                _compositeMaterial = new Material(shader)
                {
                    name = "Mtl_MultiCameraComposite_Runtime",
                    hideFlags = HideFlags.DontSave
                };
                return true;
            }

            return false;
        }

        public void EnsureDoubleBuffers(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                if (_doubleBufferRT[i] == null || _doubleBufferRT[i].width != width || _doubleBufferRT[i].height != height)
                {
                    if (_doubleBufferRT[i] != null)
                    {
                        _doubleBufferRT[i].Release();
                        DestroyImmediate(_doubleBufferRT[i]);
                    }

                    _doubleBufferRT[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                    {
                        name = $"MultiCam_DoubleBuffer_{i}",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.DontSave
                    };
                    _doubleBufferRT[i].Create();
                }
            }
        }

        private void ReleaseDoubleBuffers()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_doubleBufferRT[i] != null)
                {
                    _doubleBufferRT[i].Release();
                    if (Application.isPlaying)
                    {
                        Destroy(_doubleBufferRT[i]);
                    }
                    else
                    {
                        DestroyImmediate(_doubleBufferRT[i]);
                    }
                    _doubleBufferRT[i] = null;
                }
            }
        }
        #endregion

        #region 参数同步与接口驱动
        public void SetCameraTextures(RenderTexture mainTex, RenderTexture subTex)
        {
            _mainTexture = mainTex;
            _subTexture = subTex;
            // 只绑定纹理，出画权重仍由时间轴 / Driver 决定
            _isCompositeActive = subTex != null && _fadeValue > LiveCameraTransitionDriver.WeightEpsilon;
        }

        /// <summary>
        /// 接收 layer 轨道的分屏遮罩、分割线和图层 fade。不在这里开关相机。
        /// </summary>
        public void UpdateLayerParameters(
            int cameraNo,
            DivideLineType lineType,
            float lineThickness,
            Color lineColor,
            float fadeValue,
            Vector4 transformParameter,
            float maskRoll,
            Vector3 offsetMinPos,
            Vector3 offsetMaxPos
        )
        {
            _multiCameraNo = cameraNo;
            _lineType = lineType;
            _lineThickness = Mathf.Max(0f, lineThickness);
            _lineColor = lineColor;
            _layerFadeValue = Mathf.Clamp01(fadeValue);
            _transformParameter = transformParameter;
            _maskRoll = maskRoll;
            _offsetMinPosition = offsetMinPos;
            _offsetMaxPosition = offsetMaxPos;
        }

        /// <summary>
        /// 只开关本路副机位。主机位由 Director 自己的主镜头管线负责，避免误把第 0 路当成全局唯一出画。
        /// </summary>
        public void SetCamerasEnabled(bool enabled)
        {
            bool shouldEnableSub = enabled && _subTexture != null;
            if (_cameraSub != null && _cameraSub.enabled != shouldEnableSub)
            {
                _cameraSub.enabled = shouldEnableSub;
            }
        }

        public void ResetParameters()
        {
            _multiCameraNo = 0;
            _lineType = DivideLineType.Color;
            _lineThickness = 0.015f;
            _lineColor = Color.white;
            _fadeValue = 0f;
            _layerFadeValue = 1f;
            _isScreenDivide = true;
            _lineAntialiasing = 0.002f;
            _transformParameter = Vector4.zero;
            _maskRoll = 0f;
            _offsetMinPosition = Vector3.zero;
            _offsetMaxPosition = Vector3.zero;
            _isCompositeActive = false;
        }

        public Material CompositeMaterial
        {
            get
            {
                EnsureMaterial();
                return _compositeMaterial;
            }
        }
        #endregion

        #region 屏幕后处理与通用合成管线
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Composite(source, destination);
        }

        /// <summary>
        /// 把 base 与本路离屏 RT 合成到 dest。权重为 0 时直通 base，不顺手关相机。
        /// </summary>
        public void CompositeTextures(RenderTexture mainTex, RenderTexture subTex, RenderTexture dest)
        {
            RenderTexture effectiveSub = (subTex != null) ? subTex : _subTexture;
            RenderTexture effectiveMain = (mainTex != null) ? mainTex : _mainTexture;

            if (!_isCompositeActive || _fadeValue <= LiveCameraTransitionDriver.WeightEpsilon)
            {
                if (effectiveMain != null)
                {
                    Graphics.Blit(effectiveMain, dest);
                }
                return;
            }

            if (effectiveSub == null)
            {
                if (effectiveMain != null)
                {
                    Graphics.Blit(effectiveMain, dest);
                }
                return;
            }

            if (effectiveMain == null)
            {
                Graphics.Blit(effectiveSub, dest);
                return;
            }

            if (!EnsureMaterial() || _compositeMaterial == null)
            {
                Graphics.Blit(effectiveMain, dest);
                return;
            }

            float effectiveRoll = Mathf.Abs(_maskRoll) > 0.0001f ? _maskRoll : _transformParameter.z * 360f;
            float rollRad = effectiveRoll * Mathf.Deg2Rad;
            float cosRoll = Mathf.Cos(rollRad);
            float sinRoll = Mathf.Sin(rollRad);

            // 赛马娘时间轴原生罗盘方位角体系：0° 为正上 (Up)，顺时针为正。
            // 分割线法向量在 UV 空间的投影视角为：Nx = sin(rollRad), Ny = cos(rollRad)。
            // 使得 LeftUp (-45°) 法向量朝左上，RightUp (45°) 法向量朝右上，Down (180°) 法向量朝下。
            _compositeMaterial.SetTexture(PropMainTex, effectiveMain);
            _compositeMaterial.SetTexture(PropSubTex, effectiveSub);
            _compositeMaterial.SetColor(PropDivideLineColor, _lineColor);
            _compositeMaterial.SetVector(PropDivideLineParam, new Vector4(
                _transformParameter.x,
                _transformParameter.y,
                sinRoll,
                cosRoll
            ));
            _compositeMaterial.SetVector(PropDivideLineSetting, new Vector4(
                (float)_lineType,
                _isScreenDivide ? Mathf.Max(0.001f, _lineThickness) : -1f,
                _fadeValue,
                _lineAntialiasing > 0f ? _lineAntialiasing : 0.002f
            ));

            Graphics.Blit(effectiveMain, dest, _compositeMaterial);
        }

        public void Composite(RenderTexture source, RenderTexture destination)
        {
            CompositeTextures(source != null ? source : _mainTexture, _subTexture, destination);
        }
        #endregion

        #region 静态纯数学视口与带符号距离场计算工具
        public static float CalculateDistanceToDivideLine(Vector2 uv, Vector2 offset, float rollDeg)
        {
            Vector2 center = new Vector2(0.5f, 0.5f) + offset;
            Vector2 diff = uv - center;

            float rad = rollDeg * Mathf.Deg2Rad;
            float cosRoll = Mathf.Cos(rad);
            float sinRoll = Mathf.Sin(rad);

            return diff.x * cosRoll + diff.y * sinRoll;
        }

        public static bool IsPointInLayer(Vector2 uv, Vector2 offset, float rollDeg)
        {
            return CalculateDistanceToDivideLine(uv, offset, rollDeg) >= 0f;
        }

        public static float CalculateDivideLineAlpha(Vector2 uv, Vector2 offset, float rollDeg, float thickness, float antialiasing = 0.002f)
        {
            float dist = Mathf.Abs(CalculateDistanceToDivideLine(uv, offset, rollDeg));
            float halfThickness = Mathf.Max(0.0001f, thickness * 0.5f);
            float aa = Mathf.Max(0.0001f, antialiasing);

            if (dist <= halfThickness)
            {
                return 1f;
            }
            else if (dist < halfThickness + aa)
            {
                return 1f - Mathf.SmoothStep(halfThickness, halfThickness + aa, dist);
            }
            else
            {
                return 0f;
            }
        }

        public static Rect CalculateSplitViewport(int cameraIndex, int totalCameras, Vector2 offset)
        {
            if (totalCameras <= 1)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }

            float splitX = Mathf.Clamp01(0.5f + offset.x);
            if (cameraIndex == 0)
            {
                return new Rect(0f, 0f, splitX, 1f);
            }

            return new Rect(splitX, 0f, 1f - splitX, 1f);
        }
        #endregion
    }
}
