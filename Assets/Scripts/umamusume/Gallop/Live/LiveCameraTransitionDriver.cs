using System;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Live 镜头过渡权重中枢。
    /// 主镜头 CameraSwitcher 与多机位 Switcher 都把 fadeTime 交给这里换算，
    /// layer 轨道只提供分屏遮罩和自己的 fadeValue，最终谁出画、权重多少由这里合并。
    /// 不负责算位姿，也不直接去碰 Stage / 后处理。
    /// </summary>
    public sealed class LiveCameraTransitionDriver
    {
        public const float WeightEpsilon = 0.001f;
        public const float TargetFps = 60f;

        /// <summary>
        /// 单路机位在本帧的过渡状态。
        /// </summary>
        public struct ChannelState
        {
            public float SwitcherWeight;
            public float LayerFade;
            public bool LayerDrove;
            public bool IsScreenDivide;
            public bool IsSingleMask;
            public bool EnableRequested;
            public bool IsFading;
            public bool JustFinishedFade;
            public float DisplayWeight;
            public bool ShouldRender;
        }

        private ChannelState[] _channels = Array.Empty<ChannelState>();
        private int _count;
        private int _mainCameraIndex = -1;
        private int _previousMainCameraIndex = -1;
        private int _mainSwitchStartFrame;
        private int _mainSwitchFadeFrames;
        private bool _mainSwitchActive;
        private bool _mainSwitcherChangedThisFrame;
        private bool _hasCaptureRequest;
        private float _concurrentFadeTime;

        public int ChannelCount => _count;

        public int MainCameraIndex => _mainCameraIndex;

        public int PreviousMainCameraIndex => _previousMainCameraIndex;

        /// <summary>
        /// 主镜头切机位时，上一帧全屏画面还要叠多少（1 = 完全是旧画面）。
        /// </summary>
        public float MainSwitchFade { get; private set; }

        /// <summary>
        /// 切主镜头的当帧需要把当前全屏结果抓进备份 RT。
        /// </summary>
        public bool HasCaptureRequest => _hasCaptureRequest;

        public bool AnyRenderable { get; private set; }

        public int RenderableCount { get; private set; }

        public void EnsureChannelCount(int count)
        {
            if (count < 0)
            {
                count = 0;
            }

            if (_channels == null || _channels.Length != count)
            {
                _channels = new ChannelState[count];
                for (int i = 0; i < count; i++)
                {
                    _channels[i].LayerFade = 1f;
                }
            }

            _count = count;
        }

        public void Reset()
        {
            _count = 0;
            _channels = Array.Empty<ChannelState>();
            _mainCameraIndex = -1;
            _previousMainCameraIndex = -1;
            _mainSwitchStartFrame = 0;
            _mainSwitchFadeFrames = 0;
            _mainSwitchActive = false;
            _mainSwitcherChangedThisFrame = false;
            _hasCaptureRequest = false;
            _concurrentFadeTime = 0f;
            MainSwitchFade = 0f;
            AnyRenderable = false;
            RenderableCount = 0;
        }

        /// <summary>
        /// 每帧时间轴驱动开始前调用，清掉本帧图层标记，但保留上一帧权重作为淡出起点。
        /// </summary>
        public void BeginFrame(int currentFrame)
        {
            _mainSwitcherChangedThisFrame = false;
            _concurrentFadeTime = 0f;

            for (int i = 0; i < _count; i++)
            {
                _channels[i].LayerDrove = false;
                _channels[i].IsFading = false;
                _channels[i].JustFinishedFade = false;
            }
        }

        /// <summary>
        /// 主镜头 CameraSwitcher：记录切到哪一路。真正的淡变时长跟本帧多机位 fadeTime 对齐，
        /// 没有 fadeTime 时不发明时长，保持立刻切。
        /// </summary>
        public void NotifyMainCameraSwitcher(int cameraIndex, int currentFrame)
        {
            if (cameraIndex == _mainCameraIndex)
            {
                return;
            }

            _previousMainCameraIndex = _mainCameraIndex;
            _mainCameraIndex = cameraIndex;
            _mainSwitcherChangedThisFrame = true;
            _hasCaptureRequest = _previousMainCameraIndex >= 0;
            _mainSwitchActive = _hasCaptureRequest;
            _mainSwitchStartFrame = currentFrame;
            _mainSwitchFadeFrames = 0;
            MainSwitchFade = _hasCaptureRequest ? 1f : 0f;
        }

        public void ConsumeCaptureRequest()
        {
            _hasCaptureRequest = false;
        }

        /// <summary>
        /// 按关键帧 fadeTime 换算这一路的切换权重。
        /// enable + 非 Single：0→1 淡入；否则 1→0 淡出（含切回主镜头的 Single）。
        /// </summary>
        public bool ApplySwitcher(
            int index,
            int keyFrame,
            float fadeTime,
            bool enableMultiCamera,
            bool isSingleMask,
            bool isScreenDivide,
            int currentFrame,
            float oldFrame,
            bool useFadeTime)
        {
            if (!IsValidIndex(index))
            {
                return false;
            }

            ref ChannelState channel = ref _channels[index];
            channel.EnableRequested = enableMultiCamera;
            channel.IsSingleMask = isSingleMask;
            channel.IsScreenDivide = isScreenDivide && !isSingleMask;

            bool fadeIn = enableMultiCamera && !isSingleMask;
            float targetWeight = fadeIn ? 1f : 0f;

            if (!useFadeTime || fadeTime <= 0f)
            {
                channel.SwitcherWeight = targetWeight;
                channel.IsFading = false;
                channel.JustFinishedFade = false;
                return false;
            }

            int fadeFrame = Mathf.Max(0, (int)Math.Round(fadeTime * TargetFps));
            int fadeEndFrame = keyFrame + fadeFrame;

            if (currentFrame >= fadeEndFrame)
            {
                channel.SwitcherWeight = targetWeight;
                channel.IsFading = false;

                if (oldFrame < fadeEndFrame)
                {
                    channel.JustFinishedFade = true;
                    channel.IsFading = true;
                    RememberConcurrentFadeTime(fadeTime);
                    return true;
                }

                return false;
            }

            float fadeFrom = fadeIn ? 0f : 1f;
            float fadeTo = fadeIn ? 1f : 0f;
            float rate = fadeFrame > 0 ? (currentFrame - keyFrame) / (float)fadeFrame : 1f;
            channel.SwitcherWeight = Mathf.Lerp(fadeFrom, fadeTo, Mathf.Clamp01(rate));
            channel.IsFading = true;
            channel.JustFinishedFade = false;
            RememberConcurrentFadeTime(fadeTime);
            return false;
        }

        public void ApplyLayer(int index, float fadeValue)
        {
            if (!IsValidIndex(index))
            {
                return;
            }

            _channels[index].LayerDrove = true;
            _channels[index].LayerFade = Mathf.Clamp01(fadeValue);
        }

        public void ApplyScreenDivide(int index, bool isScreenDivide, bool isSingleMask)
        {
            if (!IsValidIndex(index))
            {
                return;
            }

            _channels[index].IsSingleMask = isSingleMask;
            _channels[index].IsScreenDivide = isScreenDivide && !isSingleMask;
        }

        public void FinalizeFrame(int currentFrame)
        {
            UpdateMainSwitchFade(currentFrame);

            AnyRenderable = false;
            RenderableCount = 0;

            for (int i = 0; i < _count; i++)
            {
                ref ChannelState channel = ref _channels[i];
                float layer = channel.LayerDrove ? channel.LayerFade : 1f;
                channel.DisplayWeight = Mathf.Clamp01(channel.SwitcherWeight * layer);
                channel.ShouldRender = channel.DisplayWeight > WeightEpsilon || channel.IsFading;
                if (channel.ShouldRender)
                {
                    AnyRenderable = true;
                    RenderableCount++;
                }
            }
        }

        public ChannelState GetChannel(int index)
        {
            if (!IsValidIndex(index))
            {
                return default;
            }

            return _channels[index];
        }

        public bool ShouldRender(int index)
        {
            return IsValidIndex(index) && _channels[index].ShouldRender;
        }

        public float GetDisplayWeight(int index)
        {
            return IsValidIndex(index) ? _channels[index].DisplayWeight : 0f;
        }

        public bool IsScreenDivide(int index)
        {
            return IsValidIndex(index) && _channels[index].IsScreenDivide;
        }

        private void UpdateMainSwitchFade(int currentFrame)
        {
            if (!_mainSwitchActive)
            {
                MainSwitchFade = 0f;
                return;
            }

            if (_mainSwitchFadeFrames <= 0)
            {
                if (_mainSwitcherChangedThisFrame && _concurrentFadeTime > 0f)
                {
                    _mainSwitchFadeFrames = Mathf.Max(1, (int)Math.Round(_concurrentFadeTime * TargetFps));
                }
                else if (!_mainSwitcherChangedThisFrame)
                {
                    // 本帧没有带时长的切换，主镜头保持立刻切，不发明淡变。
                    _mainSwitchActive = false;
                    MainSwitchFade = 0f;
                    return;
                }
                else
                {
                    MainSwitchFade = 0f;
                    return;
                }
            }

            if (_mainSwitchFadeFrames <= 0)
            {
                _mainSwitchActive = false;
                MainSwitchFade = 0f;
                return;
            }

            float rate = (currentFrame - _mainSwitchStartFrame) / (float)_mainSwitchFadeFrames;
            MainSwitchFade = 1f - Mathf.Clamp01(rate);
            if (rate >= 1f)
            {
                _mainSwitchActive = false;
                MainSwitchFade = 0f;
            }
        }

        private void RememberConcurrentFadeTime(float fadeTime)
        {
            if (fadeTime > _concurrentFadeTime)
            {
                _concurrentFadeTime = fadeTime;
            }
        }

        private bool IsValidIndex(int index)
        {
            return _channels != null && index >= 0 && index < _count;
        }
    }
}
