using Gallop;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop.RenderPipeline
{
    /// <summary>
    /// Live Bloom/Diffusion 的短 Pass。不重写官方那条超长 DOF 链，
    /// 只在 URP 后处理前把 GallopImageEffect 的有界参数写进 Volume Bloom。
    /// </summary>
    public class DofDiffusionBloomOverlayPass : ScriptableRenderPass
    {
        private GallopImageEffect _effect;

        public DofDiffusionBloomOverlayPass(RenderPassEvent passEvent)
        {
            renderPassEvent = passEvent;
        }

        public void Setup(GallopImageEffect effect)
        {
            _effect = effect;
        }

        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData)
        {
            if (_effect == null)
                return;

            _effect.EnsureCameraPostProcess();
            _effect.ApplyBloomParameter();
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            _effect = null;
        }
    }
}
