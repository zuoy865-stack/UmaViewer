using Gallop.ImageEffect;
using Gallop.RenderPipeline;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop
{
    [DisallowMultipleComponent]
    public class GallopImageEffect : MonoBehaviour
    {
        [SerializeField]
        private Volume _volume;

        [SerializeField]
        private VolumeProfile _runtimeProfile;

        [SerializeField]
        private DofDiffusionBloomOverlayParam
            _dofDiffusionBloomOverlayParam =
                new DofDiffusionBloomOverlayParam();

        private Bloom _bloom;

        public DofDiffusionBloomOverlayParam
            DofDiffusionBloomOverlayParam
        {
            get
            {
                return _dofDiffusionBloomOverlayParam;
            }
        }

        /// <summary>
        /// 第二步隔离测试调试开关：控制是否临时全局禁用 URP Bloom / Diffusion 泛光，排查是否导致天空压白与荧光绿
        /// </summary>
        public static bool DisableBloomForDebug = false;

        private void Awake()
        {
            InitializeVolume();
        }

        private void LateUpdate()
        {
            ApplyBloomParameter();
        }

        public void InitializeVolume()
        {
            if (_volume == null)
                _volume = GetComponent<Volume>();

            if (_volume == null)
                _volume = gameObject.AddComponent<Volume>();

            // 将 Volume 设置为全局生效 (isGlobal = true)。
            // 在 URP 管线下，若 isGlobal 为 false，Volume 必须依赖 Collider 触发器且需要相机进入其碰撞范围才能生效。
            // 马娘演出中机位频繁切换或挂载在没有碰撞体的对象上时，局部 Volume 会导致后处理被管线直接忽略。
            // 全局化后，全屏任意机位均能全局执行后处理，并通过高优先级 (100f) 与完全权重 (1f) 保证后处理效果正确覆盖。
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.weight = DisableBloomForDebug ? 0f : 1f;

            if (_volume.sharedProfile != null)
                _runtimeProfile =
                    Instantiate(_volume.sharedProfile);
            else
                _runtimeProfile =
                    ScriptableObject.CreateInstance<VolumeProfile>();

            _runtimeProfile.name =
                $"{name}_RuntimePostEffectProfile";

            _volume.profile = _runtimeProfile;

            if (!_runtimeProfile.TryGet(out _bloom))
                _bloom = _runtimeProfile.Add<Bloom>(true);

            EnsureCameraPostProcess();
            PostImageEffectFeature.EnsureHooked();
        }

        /// <summary>
        /// 把时间轴 Bloom/Diffusion 写进 URP Volume Bloom。
        /// 强度、阈值、提取亮度都走 DofDiffusionBloomOverlayParam 的有界映射，避免天空洗白。
        /// </summary>
        public void ApplyBloomParameter()
        {
            if (_bloom == null)
                InitializeVolume();

            if (_bloom == null)
                return;

            if (DisableBloomForDebug)
            {
                _bloom.active = false;
                if (_volume != null) _volume.weight = 0f;
                return;
            }

            var param = _dofDiffusionBloomOverlayParam;
            if (param == null)
                return;

            DofDiffusionBloomOverlayParam.UrpBloomMapping mapping =
                param.ResolveUrpBloom();

            if (_volume != null)
                _volume.weight = mapping.Active ? 1f : 0f;

            _bloom.active = mapping.Active;
            _bloom.intensity.Override(mapping.Intensity);
            _bloom.threshold.Override(mapping.Threshold);
            _bloom.scatter.Override(mapping.Scatter);
            _bloom.clamp.Override(mapping.ExtractClamp);
            _bloom.highQualityFiltering.Override(false);
        }

        /// <summary>
        /// 接收时间轴 HDR 泛光事件更新并驱动 URP Volume
        /// </summary>
        public void UpdateHdrBloom(bool enable, float intensity, float blurSpread)
        {
            if (_dofDiffusionBloomOverlayParam != null)
            {
                _dofDiffusionBloomOverlayParam.IsEnableHdrBloom = enable;
                _dofDiffusionBloomOverlayParam.HdrBloomIntensity = intensity;
                _dofDiffusionBloomOverlayParam.HdrBloomBlurSpread = blurSpread;
                ApplyBloomParameter();
            }
        }

        /// <summary>
        /// Live 预制体相机默认关着 URP 后处理。没有这一步，Volume Bloom 不会进画面。
        /// </summary>
        public void EnsureCameraPostProcess()
        {
            EnsureCameraPostProcess(GetComponent<Camera>());
        }

        public void EnsureCameraPostProcess(Camera camera)
        {
            if (camera == null)
                return;

            camera.allowHDR = true;

            UniversalAdditionalCameraData cameraData =
                camera.GetUniversalAdditionalCameraData();
            if (cameraData != null)
                cameraData.renderPostProcessing = true;
        }
    }
}
