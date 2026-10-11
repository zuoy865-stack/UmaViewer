using Gallop;
using Gallop.Live;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop.RenderPipeline
{
    /// <summary>
    /// Live 后处理入口。真正的 Bloom 由 GallopImageEffect 的 URP Volume 执行；
    /// 本 Feature 负责在渲染前把时间轴参数灌进去。
    /// 【同目异构优化】严格过滤镜面反射与辅助离屏相机，杜绝 FindObjectOfType 慢速扫描与对离屏相机的误处理。
    /// </summary>
    public class PostImageEffectFeature : ScriptableRendererFeature
    {
        private DofDiffusionBloomOverlayPass _pass;
        private static bool _hooked;
        private static GallopImageEffect _cachedStandaloneEffect;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoHook()
        {
            EnsureHooked();
        }

        public static void EnsureHooked()
        {
            if (_hooked)
                return;

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _hooked = true;
        }

        /// <summary>
        /// 判定相机是否为主画面呈现相机，滤除镜面反射（MirrorReflection）及阴影离屏相机
        /// </summary>
        private static bool IsTargetCamera(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game)
                return false;

            string name = camera.name;
            if (name.IndexOf("Mirror", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Reflection", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Shadow", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        private static void OnBeginCameraRendering(
            ScriptableRenderContext context,
            Camera camera)
        {
            if (!IsTargetCamera(camera))
                return;

            GallopImageEffect effect = ResolveEffect(camera);
            if (effect == null)
                return;

            // 仅对真正挂载该组件的相机或 Live 激活主相机开启后处理，禁止作用于镜面与离屏相机
            effect.EnsureCameraPostProcess(camera);
            effect.ApplyBloomParameter();
        }

        private static GallopImageEffect ResolveEffect(Camera camera)
        {
            if (!IsTargetCamera(camera))
                return null;

            // 1. 若相机自身挂载了 GallopImageEffect，直接返回
            if (camera.TryGetComponent(out GallopImageEffect effect))
                return effect;

            // 2. 若当前在 Live 演出中，直接通过 Director 缓存的当前主相机与后处理组件比对获取，杜绝 FindObjectOfType 全局遍历
            if (Director.instance != null)
            {
                if (camera == Director.instance.CurrentMainCamera)
                {
                    return Director.instance.ActiveGallopImageEffect;
                }
                return null;
            }

            // 3. 非 Live 状态（如单纯角色预览），若为 Camera.main，则尝试缓存获取一次
            if (camera == Camera.main)
            {
                if (_cachedStandaloneEffect == null || _cachedStandaloneEffect.gameObject == null)
                {
                    _cachedStandaloneEffect = Camera.main.GetComponent<GallopImageEffect>();
                }
                return _cachedStandaloneEffect;
            }

            return null;
        }

        public override void Create()
        {
            _pass = new DofDiffusionBloomOverlayPass(
                RenderPassEvent.BeforeRenderingPostProcessing);
            EnsureHooked();
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (!IsTargetCamera(camera))
                return;

            GallopImageEffect effect = ResolveEffect(camera);
            if (effect == null)
                return;

            if (_pass == null)
            {
                _pass = new DofDiffusionBloomOverlayPass(
                    RenderPassEvent.BeforeRenderingPostProcessing);
            }

            _pass.Setup(effect);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;
        }
    }
}
