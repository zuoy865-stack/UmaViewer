using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 舞台镜头光晕的最小驱动。场景里若已有 StageLensFlareDriver，视觉由它接管，
    /// 本组件只保留参数接口，避免再被当成空壳损坏脚本。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Gallop/Live/UnityLensFlareController")]
    public class UnityLensFlareController : MonoBehaviour
    {
        public const float STANDDARD_COLOR_POWER_DEFAULT = 3f;
        public const float UNDER_LIMIT_COLOR_POWER_DEFAULT = 0f;

        public Color Color = Color.white;
        public bool IsEnabledShaderColor = true;
        public bool IsAutoBrightness = true;

        private MeshRenderer _meshRenderer;
        private MaterialPropertyBlock _mpb;
        private bool _driverOwnsVisual;
        private float _standardColorPower = STANDDARD_COLOR_POWER_DEFAULT;
        private float _underLimitColorPower = UNDER_LIMIT_COLOR_POWER_DEFAULT;
        private bool _initialized;
        private static int _driverSearchFrame = -1;
        private static bool _driverPresent;

        public void Initialize(string nameSuffixToken = "")
        {
            Initialize(
                STANDDARD_COLOR_POWER_DEFAULT,
                UNDER_LIMIT_COLOR_POWER_DEFAULT,
                nameSuffixToken);
        }

        public void Initialize(
            float standard,
            float under,
            string nameSuffixToken = "")
        {
            _standardColorPower = Mathf.Max(0.0001f, standard);
            _underLimitColorPower = Mathf.Max(0f, under);
            CacheRenderer();
            _driverOwnsVisual = DriverOwnsVisual();
            _initialized = true;
        }

        private void Awake()
        {
            if (!_initialized)
                Initialize();
        }

        private void LateUpdate()
        {
            if (!_driverOwnsVisual)
                _driverOwnsVisual = DriverOwnsVisual();

            if (_driverOwnsVisual)
                return;

            ApplyLocalColor();
        }

        private static bool DriverOwnsVisual()
        {
            if (_driverPresent)
                return true;

            // 全场景每帧只搜一次，避免每个光晕物体都 Find。
            if (_driverSearchFrame == Time.frameCount)
                return _driverPresent;

            _driverSearchFrame = Time.frameCount;
            _driverPresent = FindObjectOfType<StageLensFlareDriver>() != null;
            return _driverPresent;
        }

        private void CacheRenderer()
        {
            if (_meshRenderer == null)
                _meshRenderer = GetComponent<MeshRenderer>();

            if (_mpb == null)
                _mpb = new MaterialPropertyBlock();
        }

        private void ApplyLocalColor()
        {
            CacheRenderer();
            if (_meshRenderer == null || !IsEnabledShaderColor)
                return;

            Color color = Color;
            if (IsAutoBrightness)
            {
                float brightness = Mathf.Clamp01(
                    color.maxColorComponent / _standardColorPower);
                if (brightness < _underLimitColorPower)
                {
                    _meshRenderer.enabled = false;
                    return;
                }

                color.a *= brightness;
            }

            _meshRenderer.enabled = true;
            _meshRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_Color", color);
            _mpb.SetColor("_BaseColor", color);
            _meshRenderer.SetPropertyBlock(_mpb);
        }
    }
}
