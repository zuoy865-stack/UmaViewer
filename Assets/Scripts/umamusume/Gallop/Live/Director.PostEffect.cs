using UnityEngine;
using Gallop.ImageEffect;
using Gallop.Live.Cutt;

namespace Gallop.Live
{
    /// <summary>
    /// Director 画面后处理效果分部类，负责获取主相机 GallopImageEffect 并响应时间轴光晕/扩散等后处理参数更新
    /// </summary>
    public partial class Director
    {
        [SerializeField]
        private GallopImageEffect _mainGallopImageEffect;

        /// <summary>
        /// 公开暴露给渲染管线获取当前活跃主相机的 GallopImageEffect 后处理组件，杜绝漫游查找
        /// </summary>
        public GallopImageEffect ActiveGallopImageEffect => GetActivePostEffect();

        /// <summary>
        /// 获取或动态挂载主相机的 GallopImageEffect 后处理组件
        /// </summary>
        private GallopImageEffect GetActivePostEffect()
        {
            Camera mainCamera = ResolveActiveLiveCamera();

            if (mainCamera == null)
                return null;

            if (_mainGallopImageEffect != null &&
                _mainGallopImageEffect.gameObject == mainCamera.gameObject)
            {
                return _mainGallopImageEffect;
            }

            _mainGallopImageEffect =
                mainCamera.GetComponent<GallopImageEffect>();

            if (_mainGallopImageEffect == null)
            {
                _mainGallopImageEffect =
                    mainCamera.gameObject
                        .AddComponent<GallopImageEffect>();
            }

            return _mainGallopImageEffect;
        }

        private Camera ResolveActiveLiveCamera()
        {
            if (_cameraObjects != null &&
                _activeCameraIndex >= 0 &&
                _activeCameraIndex < _cameraObjects.Length)
            {
                Camera timelineCamera = _cameraObjects[_activeCameraIndex];
                if (timelineCamera != null)
                    return timelineCamera;
            }

            return Camera.main;
        }

        /// <summary>
        /// 时间轴 Bloom 与 Diffusion 泛光扩散参数驱动回调
        /// </summary>
        private void OnUpdatePostEffect_BloomDiffusion(PostEffectUpdateInfo_BloomDiffusion updateInfo)
        {
            GallopImageEffect imageEffect = GetActivePostEffect();

            if (imageEffect == null) return;

            DofDiffusionBloomOverlayParam param =
                imageEffect.DofDiffusionBloomOverlayParam;

            param.IsEnableBloom =
                updateInfo.IsEnabledBloom;

            param.BloomDofWeight =
                updateInfo.bloomDofWeight;

            param.BloomThreshold =
                updateInfo.threshold;

            param.BloomIntensity =
                updateInfo.intensity;

            param.BloomBlurSize =
                updateInfo.BloomBlurSize;

            param.BloomBlendMode =
                updateInfo.BloomBlendMode;

            param.IsEnableDiffusion =
                updateInfo.IsEnabledDiffusion;

            param.DiffusionBlurSize =
                updateInfo.diffusionBlurSize;

            param.DiffusionBright =
                updateInfo.diffusionBright;

            param.DiffusionThreshold =
                updateInfo.diffusionThreshold;

            param.DiffusionSaturation =
                updateInfo.diffusionSaturation;

            param.DiffusionContrast =
                updateInfo.diffusionContrast;

            imageEffect.ApplyBloomParameter();
        }

        /// <summary>
        /// 时间轴 HDR 泛光事件驱动回调（驱动 HdrBloom 轨道）
        /// </summary>
        private void OnUpdateHdrBloom(ref HdrBloomUpdateInfo updateInfo)
        {
            GallopImageEffect imageEffect = GetActivePostEffect();
            if (imageEffect == null) return;

            imageEffect.UpdateHdrBloom(updateInfo.enable, updateInfo.intensity, updateInfo.blurSpread);
        }

        /// <summary>
        /// 取消时间轴后处理事件订阅
        /// </summary>
        private void UnbindTimelineEvents()
        {
            if (_liveTimelineControl == null)
                return;

            _liveTimelineControl.OnUpdatePostEffect_BloomDiffusion -=
                OnUpdatePostEffect_BloomDiffusion;

            _liveTimelineControl.OnUpdateHdrBloom -=
                OnUpdateHdrBloom;
        }
    }
}
