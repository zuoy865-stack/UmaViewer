using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 单路多机位相机。每路持有自己的离屏 RT，渲染开关由过渡权重决定，不在这里做全局只留一台。
    /// </summary>
    public class MultiCamera : MonoBehaviour
    {
        private RenderTexture _framebufferCompositeTexture; // 0x18
        private RenderTexture _compositeTexture; // 0x20
        [SerializeField]
        private Material[] _compositeMaterial; // 0x28

        public Vector2 MaskOffset;
        public float MaskRoll;

        private const float NearClipOffset = 0.01f;

        private Camera _camera;

        private int _maskIndex = -1;

        private Transform _myTransform;

        private Transform _maskTransform;

        private Renderer[] _maskRenderer;

        private RenderTexture _offscreenTexture;

        public Vector3 maskPosition { get; set; }

        public Quaternion maskRotation { get; set; }

        public Vector3 maskScale { get; set; }

        public int maskIndex => _maskIndex;

        /// <summary>
        /// 这一路自己的离屏颜色缓冲。
        /// </summary>
        public RenderTexture OffscreenTexture => _offscreenTexture;

        public Camera GetCamera()
        {
            return _camera;
        }

        public Transform GetMaskTransform()
        {
            return _maskTransform;
        }

        public void DetachMask()
        {
            _maskTransform = null;
            _maskRenderer = null;
            _maskIndex = -1;
        }

        public void Initialize()
        {
            if (!(_camera != null))
            {
                _myTransform = base.transform;
                _camera = base.gameObject.AddComponent<Camera>();
                _camera.enabled = false;
                _camera.depth = -3;
            }
        }

        /// <summary>
        /// 绑定本路独立离屏 RT。权重为 0 时可以停渲染，过渡中必须保持这张图是新的。
        /// </summary>
        public void AttachOffscreenTexture(RenderTexture rt)
        {
            _offscreenTexture = rt;
            if (_camera != null)
            {
                _camera.targetTexture = rt;
            }
        }

        /// <summary>
        /// 只开关这一路的 Camera.enabled，不去 SetActive 整个节点，避免合成参数一起被关掉。
        /// </summary>
        public void SetRenderingEnabled(bool enabled)
        {
            if (_camera != null && _camera.enabled != enabled)
            {
                _camera.enabled = enabled;
            }
        }

        public void Setup(int cameraDepth, RenderTexture colorBuffer, RenderTexture depthBuffer)
        {
            if (!(_camera == null))
            {
                _camera.depth = cameraDepth;
                _camera.clearFlags = CameraClearFlags.Depth;
                _camera.allowHDR = false;
                _camera.SetTargetBuffers(colorBuffer.colorBuffer, depthBuffer.depthBuffer);
            }
        }

        private void Release()
        {
            DetachMask();
            _myTransform = null;
            _camera = null;
            _offscreenTexture = null;
        }

        private void OnDestroy()
        {
            Release();
        }

        private void UpdateTransform()
        {
            Vector3 forward = _myTransform.forward;
            Vector3 vector = _myTransform.localRotation * maskPosition + forward * (_camera.nearClipPlane + NearClipOffset);
            _maskTransform.localPosition = _myTransform.localPosition + vector;
            _maskTransform.localRotation = Quaternion.LookRotation(forward) * maskRotation;
            _maskTransform.localScale = maskScale;
        }

        private void OnPreCull()
        {
            if (_maskIndex != -1)
            {
                UpdateTransform();
                Renderer[] maskRenderer = _maskRenderer;
                for (int i = 0; i < maskRenderer.Length; i++)
                {
                    maskRenderer[i].enabled = true;
                }
            }
        }

        private void OnPostRender()
        {
            if (_maskIndex != -1)
            {
                Renderer[] maskRenderer = _maskRenderer;
                for (int i = 0; i < maskRenderer.Length; i++)
                {
                    maskRenderer[i].enabled = false;
                }
            }
        }
    }
}
