using Gallop.Live.Cutt;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gallop.Live
{
    /// <summary>
    /// Director 多机位运行时：每路独立离屏 RT，过渡权重交给 LiveCameraTransitionDriver，
    /// 合成时让所有权重大于 0 的路一起进 URP 全屏 Overlay，而不是只 blit 第一路。
    /// </summary>
    public partial class Director
    {
        [Header("多机位分屏运行时管理")]
        [SerializeField] private MultiCameraComposite[] _multiCameraComposites;
        [SerializeField] private GameObject _multiCameraRoot;
        private readonly List<MultiCamera> _multiCameraList = new List<MultiCamera>();
        private RenderTexture[] _multiCameraRTs;
        private LiveTimelineControl _boundMultiCameraTimeline;
        private readonly LiveCameraTransitionDriver _cameraTransitionDriver = new LiveCameraTransitionDriver();

        [Header("多机位 URP 全屏呈现层")]
        [SerializeField] private GameObject _multiCameraOverlayRoot;
        [SerializeField] private Canvas _multiCameraOverlayCanvas;
        [SerializeField] private UnityEngine.UI.RawImage _multiCameraOverlayImage;
        private RenderTexture _finalDisplayRT;
        private RenderTexture _sceneBaseRT;
        private RenderTexture _chainTempRT;
        private RenderTexture _chainTempRT2;
        private RenderTexture _capturedPreviousFrame;
        private bool[] _multiCamRenderedMask;
        private int _expectedRenderableCount;
        private bool _pipelineCallbacksRegistered;

        public MultiCameraComposite[] MultiCameraComposites => _multiCameraComposites;

        public MultiCameraComposite MultiCamComposite => GetMultiCameraComposite(0);

        public LiveCameraTransitionDriver CameraTransitionDriver => _cameraTransitionDriver;

        /// <summary>
        /// 提供给舞台大屏监视器（StageMonitorDriver）的多机位实时离屏合成纹理
        /// </summary>
        public RenderTexture MultiCameraToMonitorTexture => _finalDisplayRT;

        /// <summary>
        /// 当前帧多机位是否处于出画渲染或过渡状态
        /// </summary>
        public bool IsMultiCameraActive => ShouldCompositeThisFrame();

        public MultiCameraComposite GetMultiCameraComposite(int index)
        {
            if (_multiCameraComposites != null && index >= 0 && index < _multiCameraComposites.Length)
            {
                return _multiCameraComposites[index];
            }
            return null;
        }

        public RenderTexture GetMultiCameraRenderTexture(int index)
        {
            if (_multiCameraRTs != null && index >= 0 && index < _multiCameraRTs.Length)
            {
                return _multiCameraRTs[index];
            }
            return null;
        }

        /// <summary>
        /// 按时间轴机位数创建独立离屏 RT。初始可以先不画，但绝不是之后永远只开一路。
        /// </summary>
        public void InitializeMultiCamera(LiveTimelineControl control)
        {
            CleanupMultiCamera();

            if (control == null || control.data == null)
            {
                return;
            }

            int cameraCount = 0;
            if (control.data.multiCameraSettings != null)
            {
                cameraCount = control.data.multiCameraSettings.cameraNum;
            }

            if (cameraCount <= 0)
            {
                if (control.data.worksheetList != null &&
                    control.data.worksheetList.Count > 0 &&
                    control.data.worksheetList[0] != null &&
                    control.data.worksheetList[0].multiCameraPosKeys != null)
                {
                    int posKeysCount = control.data.worksheetList[0].multiCameraPosKeys.Count;
                    if (posKeysCount > 0)
                    {
                        cameraCount = posKeysCount;
                        Debug.Log($"[Director.MultiCamera] multiCameraSettings 为空或 cameraNum 无效，从 worksheetList[0].multiCameraPosKeys 推断多机位数：{cameraCount}");
                    }
                }
            }

            if (cameraCount <= 0)
            {
                Debug.Log("[Director.MultiCamera] 当前 Live 未启用多机位配置 (cameraCount <= 0)");
                return;
            }

            Debug.Log($"[Director.MultiCamera] 开始初始化多机位分屏系统，机位总数：{cameraCount}");

            _boundMultiCameraTimeline = control;
            _cameraTransitionDriver.EnsureChannelCount(cameraCount);

            _multiCameraRoot = new GameObject("MultiCameras");
            _multiCameraRoot.transform.SetParent(control.transform, false);

            int rtWidth = Screen.width > 0 ? Screen.width : 1920;
            int rtHeight = Screen.height > 0 ? Screen.height : 1080;

            _multiCameraRTs = new RenderTexture[cameraCount];
            _multiCameraComposites = new MultiCameraComposite[cameraCount];
            _multiCamRenderedMask = new bool[cameraCount];
            MultiCamera[] cameras = new MultiCamera[cameraCount];
            _multiCameraList.Clear();

            if (control.MultiRecordFrames == null)
            {
                control.MultiRecordFrames = new List<List<LiveCameraFrame>>();
            }
            control.MultiRecordFrames.Clear();

            for (int i = 0; i < cameraCount; i++)
            {
                GameObject camObj = new GameObject($"MultiCamera_{i}");
                camObj.transform.SetParent(_multiCameraRoot.transform, false);

                MultiCamera multiCam = camObj.AddComponent<MultiCamera>();
                multiCam.Initialize();
                cameras[i] = multiCam;
                _multiCameraList.Add(multiCam);

                MultiCameraComposite composite = camObj.AddComponent<MultiCameraComposite>();
                composite.MultiCameraNo = i;
                _multiCameraComposites[i] = composite;

                control.MultiRecordFrames.Add(new List<LiveCameraFrame>());

                RenderTexture rt = CreateOffscreenRT(rtWidth, rtHeight, $"MultiCam_RT_{i}");
                _multiCameraRTs[i] = rt;
                multiCam.AttachOffscreenTexture(rt);

                Camera cam = multiCam.GetCamera();
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Color.clear;
                    cam.depth = i + 1f;
                    // 初始化时默认保持关闭，避免无分屏或过渡需求时常驻并发全场景渲染；由时间轴 Driver.ShouldRender 按需唤醒
                    cam.enabled = false;
                }

                composite.CameraMain = null;
                composite.CameraSub = cam;
                composite.SetCameraTextures(null, rt);
                composite.ResetParameters();
            }

            control.OnUpdateMultiCameraLayer += OnTimelineMultiCameraLayerUpdated;
            control.SetMultiCamera(cameras);

            EnsureFinalCompositeAttached();
            EnsureOverlayCreated();
            RegisterPipelineCallbacks();

            Debug.Log("[Director.MultiCamera] 多机位独立离屏 RT 与全屏呈现层已就绪，出画由时间轴权重决定。");
        }

        public void SetMultiCameraEnabled(int index, bool enabled)
        {
            if (_multiCameraList == null || index < 0 || index >= _multiCameraList.Count)
            {
                return;
            }

            MultiCamera multiCam = _multiCameraList[index];
            if (multiCam != null)
            {
                multiCam.SetRenderingEnabled(enabled);
            }
        }

        public void SetMultiCamerasEnabled(bool enabled)
        {
            if (_multiCameraList == null || _multiCameraList.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _multiCameraList.Count; i++)
            {
                SetMultiCameraEnabled(i, enabled);
            }
        }

        /// <summary>
        /// 把 Driver 的本帧权重写回各路合成器，并按 ShouldRender 开关相机。过渡中两路都会画。
        /// </summary>
        public void ApplyMultiCameraTransitionState()
        {
            if (_cameraTransitionDriver == null || _multiCameraComposites == null)
            {
                return;
            }

            int count = Mathf.Min(_multiCameraComposites.Length, _cameraTransitionDriver.ChannelCount);
            for (int i = 0; i < count; i++)
            {
                LiveCameraTransitionDriver.ChannelState channel = _cameraTransitionDriver.GetChannel(i);
                MultiCameraComposite composite = _multiCameraComposites[i];
                if (composite != null)
                {
                    composite.IsScreenDivide = channel.IsScreenDivide;
                    composite.CommitDisplayWeight(channel.DisplayWeight);
                    if (composite.gameObject != null && !composite.gameObject.activeSelf)
                    {
                        composite.gameObject.SetActive(true);
                    }
                }

                SetMultiCameraEnabled(i, channel.ShouldRender);
            }
        }

        private void OnTimelineMultiCameraLayerUpdated(
            int cameraNo,
            MultiCameraComposite.DivideLineType lineType,
            float lineThickness,
            Color lineColor,
            float fadeValue,
            Vector4 transformParameter,
            float maskRoll,
            Vector3 offsetMinPos,
            Vector3 offsetMaxPos
        )
        {
            MultiCameraComposite composite = GetMultiCameraComposite(cameraNo);
            if (composite != null)
            {
                composite.UpdateLayerParameters(
                    cameraNo,
                    lineType,
                    lineThickness,
                    lineColor,
                    fadeValue,
                    transformParameter,
                    maskRoll,
                    offsetMinPos,
                    offsetMaxPos
                );
            }

            _cameraTransitionDriver.ApplyLayer(cameraNo, fadeValue);
        }

        public void CleanupMultiCamera()
        {
            SetMultiCamerasEnabled(false);
            UnregisterPipelineCallbacks();
            _cameraTransitionDriver.Reset();

            if (_boundMultiCameraTimeline != null)
            {
                _boundMultiCameraTimeline.OnUpdateMultiCameraLayer -= OnTimelineMultiCameraLayerUpdated;
                _boundMultiCameraTimeline = null;
            }

            Camera liveMain = GetLiveMainCamera();
            if (liveMain != null && liveMain.targetTexture == _sceneBaseRT)
            {
                liveMain.targetTexture = null;
            }

            ReleaseRTArray(ref _multiCameraRTs);
            ReleaseRT(ref _finalDisplayRT);
            ReleaseRT(ref _sceneBaseRT);
            ReleaseRT(ref _chainTempRT);
            ReleaseRT(ref _chainTempRT2);
            ReleaseRT(ref _capturedPreviousFrame);

            if (_multiCameraOverlayRoot != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_multiCameraOverlayRoot);
                }
                else
                {
                    DestroyImmediate(_multiCameraOverlayRoot);
                }
                _multiCameraOverlayRoot = null;
                _multiCameraOverlayCanvas = null;
                _multiCameraOverlayImage = null;
            }

            if (_multiCameraRoot != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_multiCameraRoot);
                }
                else
                {
                    DestroyImmediate(_multiCameraRoot);
                }
                _multiCameraRoot = null;
            }

            _multiCameraComposites = null;
            _multiCamRenderedMask = null;
            _multiCameraList.Clear();
        }

        private void EnsureOverlayCreated()
        {
            if (_multiCameraOverlayRoot != null)
            {
                return;
            }

            _multiCameraOverlayRoot = new GameObject("MultiCameraScreenOverlay");
            _multiCameraOverlayRoot.transform.SetParent(transform, false);

            _multiCameraOverlayCanvas = _multiCameraOverlayRoot.AddComponent<Canvas>();
            _multiCameraOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _multiCameraOverlayCanvas.sortingOrder = -1;

            GameObject imgObj = new GameObject("MultiCameraDisplayImage");
            imgObj.transform.SetParent(_multiCameraOverlayRoot.transform, false);
            _multiCameraOverlayImage = imgObj.AddComponent<UnityEngine.UI.RawImage>();
            _multiCameraOverlayImage.raycastTarget = false;

            RectTransform rect = _multiCameraOverlayImage.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _multiCameraOverlayRoot.SetActive(false);
        }

        private void RegisterPipelineCallbacks()
        {
            if (_pipelineCallbacksRegistered)
            {
                return;
            }

            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            _pipelineCallbacksRegistered = true;
        }

        private void UnregisterPipelineCallbacks()
        {
            if (!_pipelineCallbacksRegistered)
            {
                return;
            }

            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            _pipelineCallbacksRegistered = false;
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            ResetRenderedMask();
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_multiCameraList == null || _multiCameraList.Count == 0)
            {
                return;
            }

            // 【同目异构优化】若当前帧无需进行多机位分屏或切镜转场合成，直接阻断退出，
            // 杜绝普通演出期间每一帧无条件执行 1080p 全屏抓帧（GrabCameraToRT）导致的巨大显存带宽消耗
            if (!ShouldCompositeThisFrame())
            {
                return;
            }

            Camera liveMain = GetLiveMainCamera();
            int index = IndexOfMultiCamera(camera);
            if (index >= 0 && _multiCamRenderedMask != null && index < _multiCamRenderedMask.Length)
            {
                _multiCamRenderedMask[index] = true;
            }

            if (AllExpectedCamerasRendered() || (_expectedRenderableCount == 0 && camera == liveMain))
            {
                PresentCompositeOverlay();
            }
        }

        /// <summary>
        /// LateUpdate 兜底：即使管线回调漏了某路，全屏 Overlay 仍能吃到上一帧全部激活 RT。
        /// </summary>
        public void UpdateMultiCameraDisplay()
        {
            if (_multiCameraComposites == null || _multiCameraComposites.Length == 0)
            {
                return;
            }

            // 【同目异构优化】未处于分屏时无需分配或轮询显示贴图，直接关闭 Overlay 根节点并返回
            if (!ShouldCompositeThisFrame())
            {
                if (_multiCameraOverlayRoot != null && _multiCameraOverlayRoot.activeSelf)
                {
                    _multiCameraOverlayRoot.SetActive(false);
                }
                return;
            }

            EnsureDisplayTargets();
            CapturePreviousFrameIfRequested();

            PresentCompositeOverlay();
        }

        /// <summary>
        /// 以底板为起点，按通道顺序把所有仍在出画的多机位叠进去。
        /// </summary>
        public bool CompositeMultiCameraLayers(RenderTexture baseTex, RenderTexture dest)
        {
            if (dest == null || _multiCameraComposites == null || _multiCameraComposites.Length == 0)
            {
                return false;
            }

            LiveCameraTransitionDriver driver = _cameraTransitionDriver;
            bool driverActive = driver != null && (driver.AnyRenderable || driver.MainSwitchFade > LiveCameraTransitionDriver.WeightEpsilon);
            if (!driverActive)
            {
                bool hasActiveComp = false;
                for (int i = 0; i < _multiCameraComposites.Length; i++)
                {
                    if (_multiCameraComposites[i] != null && _multiCameraComposites[i].IsCompositeActive)
                    {
                        hasActiveComp = true;
                        break;
                    }
                }
                if (!hasActiveComp)
                {
                    return false;
                }
            }

            // 先数真正要叠的路：过渡中 ShouldRender 的路或处于激活态的合成器即使权重很小也要参与
            int layerCount = 0;
            for (int i = 0; i < _multiCameraComposites.Length; i++)
            {
                MultiCameraComposite comp = _multiCameraComposites[i];
                bool isRenderable = driver != null && driverActive ? driver.ShouldRender(i) : (comp != null && comp.IsCompositeActive);
                if (comp != null && isRenderable)
                {
                    layerCount++;
                }
            }

            RenderTexture current = baseTex;
            int processed = 0;
            bool needCaptureBlend = driver != null && driver.MainSwitchFade > LiveCameraTransitionDriver.WeightEpsilon;

            for (int i = 0; i < _multiCameraComposites.Length; i++)
            {
                MultiCameraComposite composite = _multiCameraComposites[i];
                bool isRenderable = driver != null && driverActive ? driver.ShouldRender(i) : (composite != null && composite.IsCompositeActive);
                if (composite == null || !isRenderable)
                {
                    continue;
                }

                processed++;
                bool isLastLayer = processed >= layerCount && !needCaptureBlend;
                RenderTexture nextTarget;
                if (isLastLayer)
                {
                    nextTarget = dest;
                }
                else if ((processed % 2) == 1)
                {
                    nextTarget = EnsureSizedRT(ref _chainTempRT, dest.width, dest.height, 0, "MultiCam_ChainTemp_RT");
                }
                else
                {
                    nextTarget = EnsureSizedRT(ref _chainTempRT2, dest.width, dest.height, 0, "MultiCam_ChainTemp2_RT");
                }

                if (nextTarget == current)
                {
                    nextTarget = (nextTarget == _chainTempRT)
                        ? EnsureSizedRT(ref _chainTempRT2, dest.width, dest.height, 0, "MultiCam_ChainTemp2_RT")
                        : EnsureSizedRT(ref _chainTempRT, dest.width, dest.height, 0, "MultiCam_ChainTemp_RT");
                }

                RenderTexture sub = composite.SubTexture != null ? composite.SubTexture : GetMultiCameraRenderTexture(i);
                if (current == null)
                {
                    Graphics.Blit(sub, nextTarget);
                }
                else
                {
                    // 在多机位分屏拼接时，两路机位之间使用完整不透明度进行空间切割，
                    // 整体向主舞台的淡入淡出由最外层 Overlay 画布的 Alpha 统一驱动
                    float oldFade = composite.FadeValue;
                    bool oldActive = composite.IsCompositeActive;
                    composite.FadeValue = 1f;
                    composite.IsCompositeActive = true;

                    composite.CompositeTextures(current, sub, nextTarget);

                    composite.FadeValue = oldFade;
                    composite.IsCompositeActive = oldActive;
                }

                current = nextTarget;
            }

            if (processed == 0)
            {
                if (baseTex != null)
                {
                    Graphics.Blit(baseTex, dest);
                }
                return BlendCapturedPrevious(dest, dest);
            }

            if (current != dest && current != null)
            {
                Graphics.Blit(current, dest);
            }

            return BlendCapturedPrevious(dest, dest) || processed > 0;
        }
        public void EnsureFinalCompositeAttached()
        {
            Camera mainCam = GetLiveMainCamera();
            AttachFinalComposite(mainCam);

            if (_cameraObjects == null)
            {
                return;
            }

            for (int i = 0; i < _cameraObjects.Length; i++)
            {
                AttachFinalComposite(_cameraObjects[i]);
            }
        }

        private void AttachFinalComposite(Camera cam)
        {
            if (cam == null)
            {
                return;
            }

            MultiCameraFinalComposite finalComp = cam.GetComponent<MultiCameraFinalComposite>();
            if (finalComp == null)
            {
                finalComp = cam.gameObject.AddComponent<MultiCameraFinalComposite>();
            }
            if (finalComp.TargetComposite == null)
            {
                finalComp.TargetComposite = GetMultiCameraComposite(0);
            }
        }

        private void PresentCompositeOverlay()
        {
            EnsureOverlayCreated();
            EnsureDisplayTargets();

            RenderTexture baseTex = _sceneBaseRT;
            if (baseTex == null && _multiCameraRTs != null)
            {
                // 还没抓到主镜头时，用第一路仍在出画的 RT 当底板，避免整屏空黑
                for (int i = 0; i < _multiCameraRTs.Length; i++)
                {
                    bool isRender = _cameraTransitionDriver != null ? _cameraTransitionDriver.ShouldRender(i) : (_multiCameraComposites != null && i < _multiCameraComposites.Length && _multiCameraComposites[i] != null && _multiCameraComposites[i].IsCompositeActive);
                    if (isRender && _multiCameraRTs[i] != null)
                    {
                        baseTex = _multiCameraRTs[i];
                        break;
                    }
                }
            }

            bool composed = CompositeMultiCameraLayers(baseTex, _finalDisplayRT);
            if (!composed)
            {
                return;
            }

            // 计算当前多机位呈现层的综合淡入淡出权重，交由 UI Overlay 画布以硬件 Alpha 混合覆盖在主相机之上
            float displayFade = 1f;
            if (_cameraTransitionDriver != null)
            {
                float maxWeight = 0f;
                for (int i = 0; i < _cameraTransitionDriver.ChannelCount; i++)
                {
                    maxWeight = Mathf.Max(maxWeight, _cameraTransitionDriver.GetDisplayWeight(i));
                }
                if (maxWeight > LiveCameraTransitionDriver.WeightEpsilon)
                {
                    displayFade = maxWeight;
                }
                else if (_cameraTransitionDriver.MainSwitchFade > LiveCameraTransitionDriver.WeightEpsilon)
                {
                    displayFade = _cameraTransitionDriver.MainSwitchFade;
                }
            }
            else if (_multiCameraComposites != null)
            {
                float maxWeight = 0f;
                for (int i = 0; i < _multiCameraComposites.Length; i++)
                {
                    if (_multiCameraComposites[i] != null && _multiCameraComposites[i].IsCompositeActive)
                    {
                        maxWeight = Mathf.Max(maxWeight, _multiCameraComposites[i].FadeValue);
                    }
                }
                displayFade = maxWeight;
            }

            if (_multiCameraOverlayImage != null)
            {
                _multiCameraOverlayImage.texture = _finalDisplayRT;
                _multiCameraOverlayImage.color = new Color(1f, 1f, 1f, Mathf.Clamp01(displayFade));
            }
            if (_multiCameraOverlayRoot != null && !_multiCameraOverlayRoot.activeSelf)
            {
                _multiCameraOverlayRoot.SetActive(true);
            }
        }

        private bool BlendCapturedPrevious(RenderTexture current, RenderTexture dest)
        {
            float fade = _cameraTransitionDriver != null ? _cameraTransitionDriver.MainSwitchFade : 0f;
            if (_capturedPreviousFrame == null || fade <= LiveCameraTransitionDriver.WeightEpsilon)
            {
                return current != null;
            }

            if (fade >= 1f - LiveCameraTransitionDriver.WeightEpsilon)
            {
                Graphics.Blit(_capturedPreviousFrame, dest);
                return true;
            }

            MultiCameraComposite blender = GetMultiCameraComposite(0);
            if (blender == null)
            {
                Graphics.Blit(current, dest);
                return true;
            }

            bool oldDivide = blender.IsScreenDivide;
            float oldFade = blender.FadeValue;
            bool oldActive = blender.IsCompositeActive;
            blender.IsScreenDivide = false;
            blender.CommitDisplayWeight(fade);
            blender.CompositeTextures(current, _capturedPreviousFrame, dest);
            blender.IsScreenDivide = oldDivide;
            blender.CommitDisplayWeight(oldFade);
            blender.IsCompositeActive = oldActive;
            return true;
        }

        private bool ShouldCompositeThisFrame()
        {
            if (_cameraTransitionDriver != null)
            {
                if (_cameraTransitionDriver.AnyRenderable ||
                    _cameraTransitionDriver.MainSwitchFade > LiveCameraTransitionDriver.WeightEpsilon)
                {
                    return true;
                }
            }

            if (_multiCameraComposites != null)
            {
                for (int i = 0; i < _multiCameraComposites.Length; i++)
                {
                    if (_multiCameraComposites[i] != null && _multiCameraComposites[i].IsCompositeActive)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CapturePreviousFrameIfRequested()
        {
            if (_cameraTransitionDriver == null || !_cameraTransitionDriver.HasCaptureRequest)
            {
                return;
            }

            if (_finalDisplayRT != null)
            {
                RenderTexture capture = EnsureSizedRT(ref _capturedPreviousFrame, _finalDisplayRT.width, _finalDisplayRT.height, 0, "MultiCam_PrevFrame_RT");
                Graphics.Blit(_finalDisplayRT, capture);
            }

            _cameraTransitionDriver.ConsumeCaptureRequest();
        }

        /// <summary>
        /// 保证主相机在所有帧中直接输出至屏幕 Display 1（targetTexture == null），
        /// 杜绝 URP 管线因缺乏直出屏幕相机而弹出「Display 1: No cameras rendering」警告。
        /// 多机位分屏与切镜画面统一通过全屏 Overlay 以硬件 Alpha 混合覆盖呈现。
        /// </summary>
        private void UpdateMainCameraTargetTexture()
        {
            Camera liveMain = GetLiveMainCamera();
            if (liveMain == null)
            {
                return;
            }

            if (liveMain.targetTexture == _sceneBaseRT)
            {
                liveMain.targetTexture = null;
            }
        }

        private void GrabCameraToRT(Camera camera, ref RenderTexture target)
        {
            if (camera == null)
            {
                return;
            }

            Texture source = camera.targetTexture != null ? (Texture)camera.targetTexture : camera.activeTexture;
            if (source == null)
            {
                return;
            }

            int width = source.width > 0 ? source.width : (Screen.width > 0 ? Screen.width : 1920);
            int height = source.height > 0 ? source.height : (Screen.height > 0 ? Screen.height : 1080);
            RenderTexture rt = EnsureSizedRT(ref target, width, height, 0, "MultiCam_SceneBase_RT");
            Graphics.Blit(source, rt);
        }

        private Camera GetLiveMainCamera()
        {
            if (_cameraObjects != null && _activeCameraIndex >= 0 && _activeCameraIndex < _cameraObjects.Length)
            {
                return _cameraObjects[_activeCameraIndex];
            }
            return Camera.main;
        }

        private int IndexOfMultiCamera(Camera camera)
        {
            if (camera == null || _multiCameraList == null)
            {
                return -1;
            }

            for (int i = 0; i < _multiCameraList.Count; i++)
            {
                if (_multiCameraList[i] != null && _multiCameraList[i].GetCamera() == camera)
                {
                    return i;
                }
            }
            return -1;
        }

        private void ResetRenderedMask()
        {
            _expectedRenderableCount = _cameraTransitionDriver != null ? _cameraTransitionDriver.RenderableCount : 0;
            if (_multiCamRenderedMask == null)
            {
                return;
            }

            for (int i = 0; i < _multiCamRenderedMask.Length; i++)
            {
                _multiCamRenderedMask[i] = false;
            }
        }

        private bool AllExpectedCamerasRendered()
        {
            if (_multiCamRenderedMask == null || _cameraTransitionDriver == null)
            {
                return false;
            }

            int seen = 0;
            int count = Mathf.Min(_multiCamRenderedMask.Length, _cameraTransitionDriver.ChannelCount);
            for (int i = 0; i < count; i++)
            {
                if (_cameraTransitionDriver.ShouldRender(i))
                {
                    if (!_multiCamRenderedMask[i])
                    {
                        return false;
                    }
                    seen++;
                }
            }

            return seen > 0;
        }

        private void EnsureDisplayTargets()
        {
            int screenW = Screen.width > 0 ? Screen.width : 1920;
            int screenH = Screen.height > 0 ? Screen.height : 1080;
            EnsureSizedRT(ref _finalDisplayRT, screenW, screenH, 0, "MultiCam_FinalDisplay_RT");

            if (_multiCameraRTs == null)
            {
                return;
            }

            for (int i = 0; i < _multiCameraRTs.Length; i++)
            {
                if (_multiCameraRTs[i] != null && (_multiCameraRTs[i].width != screenW || _multiCameraRTs[i].height != screenH))
                {
                    ReleaseRT(ref _multiCameraRTs[i]);
                    _multiCameraRTs[i] = CreateOffscreenRT(screenW, screenH, $"MultiCam_RT_{i}");
                    if (i < _multiCameraList.Count && _multiCameraList[i] != null)
                    {
                        _multiCameraList[i].AttachOffscreenTexture(_multiCameraRTs[i]);
                    }
                    MultiCameraComposite composite = GetMultiCameraComposite(i);
                    if (composite != null)
                    {
                        composite.SetCameraTextures(null, _multiCameraRTs[i]);
                    }
                }
            }
        }

        private static RenderTexture CreateOffscreenRT(int width, int height, string name)
        {
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        private static RenderTexture EnsureSizedRT(ref RenderTexture rt, int width, int height, int depth, string name)
        {
            if (width <= 0 || height <= 0)
            {
                return rt;
            }

            if (rt != null && rt.width == width && rt.height == height)
            {
                return rt;
            }

            ReleaseRT(ref rt);
            rt = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            rt.Release();
            if (Application.isPlaying)
            {
                Object.Destroy(rt);
            }
            else
            {
                Object.DestroyImmediate(rt);
            }
            rt = null;
        }

        private static void ReleaseRTArray(ref RenderTexture[] rts)
        {
            if (rts == null)
            {
                return;
            }

            for (int i = 0; i < rts.Length; i++)
            {
                ReleaseRT(ref rts[i]);
            }
            rts = null;
        }
    }
}
