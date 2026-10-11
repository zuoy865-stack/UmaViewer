using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 主镜头最终合成。所有 ShouldRender 的多机位 RT 都参与，而不是只 blit 第一路。
    /// </summary>
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class MultiCameraFinalComposite : MonoBehaviour
    {
        [Header("多机位合成器（可选，留空则自动从 Director.instance 获取）")]
        [SerializeField] private MultiCameraComposite _composite;

        public MultiCameraComposite TargetComposite
        {
            get => _composite;
            set => _composite = value;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (!TryComposite(src, dest))
            {
                Graphics.Blit(src, dest);
            }
        }

        /// <summary>
        /// 以 baseTex 为底板，按 Driver 的 DisplayWeight / ShouldRender 叠全部仍在出画的机位。
        /// </summary>
        public bool TryComposite(RenderTexture baseTex, RenderTexture dest)
        {
            Director director = Director.instance;
            if (director != null && director.CameraTransitionDriver != null)
            {
                LiveCameraTransitionDriver driver = director.CameraTransitionDriver;
                if (driver.AnyRenderable || driver.MainSwitchFade > LiveCameraTransitionDriver.WeightEpsilon)
                {
                    return director.CompositeMultiCameraLayers(baseTex, dest);
                }
            }

            if (_composite != null && _composite.IsCompositeActive && _composite.FadeValue > LiveCameraTransitionDriver.WeightEpsilon)
            {
                _composite.CompositeTextures(baseTex, _composite.SubTexture, dest);
                return true;
            }

            return false;
        }
    }
}