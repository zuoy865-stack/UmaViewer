using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 时间轴角色动作序列驱动器，负责按帧采样动作关键帧并应用到角色模型 Animation 组件。
    /// 集成多通道动作剪辑联动解析、合法休止帧误报消除、专属舞蹈长动作动态嗅探提取以及姿态维持(Hold)机制，彻底根治 T-pose 隐患。
    /// </summary>
    public class LiveTimelineMotionSequence
    {
        private ILiveTimelineMSQTarget _target; // 0x10
        private Animation[] _animationArray; // 0x18
        private int _targetIndex; // 0x20
        private LiveTimelineKeyCharaMotionSeqDataList[] _keyArray; // 0x28
        private LiveTimelineKeyCharaMotionSeqDataList _currentKey; // 0x30
        private int _indexOfPlayingKey; // 0x38
        private LiveTimelineControl _timelineControl; // 0x40
        private float _heightRate; // 0x48
        private LiveTimelineDefine.SheetIndex _currentSheetIndex; // 0x4C
        //private LivePlayableAnimator[] _playableAnimatorArray; // 0x50
        private int _prevSysTextId; // 0x58
        private const float OVERRIDE_ANIMATOR_FACIAL_WAIT = 1;
        private int _prevFrame; // 0x5C
        private float _prevFrameAnimationTime; // 0x60

        // 基础组件与变换缓存
        private Transform _tempTarget;
        private Animation _tempAnim;

        private bool _needChange;
        private LiveTimelineKeyCharaMotionData _prevKey;

        private AnimationClip _targetAnim = null;

        private bool _motionSetup = false;

        private int _curIndex = -1;
        private int _prevIndex = -1;

        public int charaIndex = -1;

        /// <summary>
        /// 最近一次成功采样的动作剪辑，用于在休止帧时保持姿态 (Freeze/Hold) 防止角色回退至 T-pose
        /// </summary>
        private AnimationClip _lastValidClip = null;

        /// <summary>
        /// 最近一次采样的动作时间戳（秒）
        /// </summary>
        private float _lastValidSampleTime = 0f;

        /// <summary>
        /// 当前角色动态嗅探提取到的专属舞蹈长动作剪辑（整首歌曲的主舞步），在全轨道动作缺失或休止时兜底播放
        /// </summary>
        private AnimationClip _exclusiveDanceClip = null;

        /// <summary>
        /// 是否允许在整条动作序列没有任何有效主通道剪辑时，自动绑定「专属舞蹈长动作」兜底。
        /// 默认关闭：官方语义下主通道为空是合法的（本帧无主通道动作），不需要兜底；
        /// 自动绑定是在猜——用整首长剪辑顶替逐帧动作，既破坏姿态，也因每帧整段骨架 Sample() 造成掉帧。
        /// 仅在明确需要该兜底时手动打开。
        /// </summary>
        public static bool AllowExclusiveDanceFallback = false;

        /// <summary>
        /// 初始化角色的动作序列配置与动画组件，并执行严格的组件、关键帧联动解析与专属动作嗅探
        /// </summary>
        /// <param name="target">角色根变换节点</param>
        /// <param name="targetIndex">角色索引编号（0-indexed）</param>
        /// <param name="seqDataIndex">时间轴动作数据序列索引</param>
        /// <param name="timelineControl">Live 时间轴总控制器</param>
        /// <param name="animclips">当处于 LiveMode 0（纯动作模式）时传入的整首动作剪辑列表</param>
        public void Initialize(Transform target, int targetIndex, int seqDataIndex, LiveTimelineControl timelineControl, List<AnimationClip> animclips = null)
        {
            _tempTarget = target;
            _targetIndex = targetIndex;
            charaIndex = targetIndex;
            _timelineControl = timelineControl;

            var director = Director.instance;
            int musicId = (director != null && director.live != null) ? director.live.MusicId : 0;

            // 1. 优先获取角色的 Animation 组件
            if (director != null && director.charaAnims != null && targetIndex < director.charaAnims.Count)
            {
                _tempAnim = director.charaAnims[targetIndex];
            }

            // 2. 校验 Animation 动画组件是否存在，若缺失则记录严重错误并弹窗警告，防止静默失败
            if (_tempAnim == null)
            {
                Debug.LogError($"[LiveMotion] 角色 {targetIndex + 1} 动画组件 Animation 丢失！");
                UmaErrorManager.ShowUIMessage($"[Live动作异常] 角色 {targetIndex + 1} 缺少动作组件 Animation，将无法播放动作", UIMessageType.Error);
            }

            // 3. 分支判断：LiveMode 0 独立动作播放模式
            bool isLiveMode0 = (director != null && director.liveMode == 0) || animclips != null;
            if (isLiveMode0)
            {
                if (animclips != null && targetIndex < animclips.Count)
                {
                    _targetAnim = animclips[targetIndex];
                }

                // 兜底补齐：若传入的动作剪辑列表为空或不足以覆盖当前角色站位，尝试从 Live 专属动作包中动态嗅探提取
                if (_targetAnim == null && musicId > 0)
                {
                    _targetAnim = ResolveCharaExclusiveDanceClip(musicId, targetIndex);
                }

                if (_targetAnim == null)
                {
                    Debug.LogError($"[LiveMotion] LiveMode 0 角色 {targetIndex + 1} 动作剪辑缺失！(歌曲: {musicId})");
                    UmaErrorManager.ShowUIMessage($"[Live动作异常] 角色 {targetIndex + 1} 动作数据缺失，角色将保持初始姿态(T-pose)，请检查对应 Live 动作资源是否下载完整", UIMessageType.Error);
                }
                else if (_tempAnim != null)
                {
                    // 增加同名检查，防止重复注册剪辑
                    if (_tempAnim.GetClip(_targetAnim.name) == null)
                    {
                        _tempAnim.AddClip(_targetAnim, _targetAnim.name);
                    }
                    _lastValidClip = _targetAnim;
                    _lastValidSampleTime = 0f;
                }

                // 增强容错：安全配置动画循环模式，彻底防止空指针异常导致后续所有角色中断初始化
                if (_tempAnim != null)
                {
                    _tempAnim.wrapMode = WrapMode.Clamp;
                    _tempAnim.enabled = false;
                }
                return;
            }

            // 4. 分支判断：Timeline 序列帧动作模式
            _keyArray = timelineControl != null ? timelineControl._keyArray : null;

            if (_keyArray != null && seqDataIndex >= 0 && seqDataIndex < _keyArray.Length)
            {
                _currentKey = _keyArray[seqDataIndex];
            }

            // 校验 _currentKey 是否为 null
            if (_currentKey == null)
            {
                Debug.LogError($"[LiveMotion] 角色 {targetIndex + 1} 动作关键帧序列为空！(序列索引: {seqDataIndex}, 歌曲: {musicId})");
                UmaErrorManager.ShowUIMessage($"[Live动作异常] 角色 {targetIndex + 1} 动作数据缺失，角色将保持初始姿态(T-pose)，请检查对应 Live 动作资源是否下载完整", UIMessageType.Error);
            }
            else if (_currentKey.thisList != null)
            {
                // 遍历统计有效主通道剪辑数量，并对「三通道全空」的合法休止帧做误报抑制
                int validClipCount = 0;
                foreach (var key in _currentKey.thisList)
                {
                    if (key == null)
                        continue;

                    // 官方语义：三条通道各自独立，互不兜底。
                    // 主通道只由 key.motionName 解析；主通道为空是合法的「本帧无主通道动作」。
                    // 绝不能把通道 2/3（面部·眼部补正、手持物姿态）提升为主通道 —— 那样眼部调整的剪辑
                    // 会被当成身体主动作去驱动骨架，挂在手部骨骼上的手持物也会跟着错位。
                    if (key.clip == null && !string.IsNullOrEmpty(key.motionName))
                    {
                        key.clip = LiveTimelineMotionClipResolver.ResolveClip(key.motionName, musicId);
                    }

                    // 通道 2/3 走确定性路径解析，不复用主通道的模糊嗅探链路
                    if (key.clip2 == null && !string.IsNullOrEmpty(key.motionName2))
                    {
                        key.clip2 = LiveTimelineMotionClipResolver.ResolveClipExact(key.motionName2, musicId);
                    }
                    if (key.clip3 == null && !string.IsNullOrEmpty(key.motionName3))
                    {
                        key.clip3 = LiveTimelineMotionClipResolver.ResolveClipExact(key.motionName3, musicId);
                    }

                    // 注册有效剪辑到 Animation 组件并计数
                    if (key.clip != null)
                    {
                        validClipCount++;
                        RegisterClipToAnimation(key.clip);
                    }
                    if (key.clip2 != null)
                    {
                        RegisterClipToAnimation(key.clip2);
                    }
                    if (key.clip3 != null)
                    {
                        RegisterClipToAnimation(key.clip3);
                    }

                    // b) 误报抑制：若三个通道的 clip 与 motionName 均为空，确认为合法的待机休止关键帧，绝不输出动作缺失警告
                    if (key.clip == null)
                    {
                        bool isLegitRest = (key.clip2 == null && key.clip3 == null) &&
                                           string.IsNullOrEmpty(key.motionName) &&
                                           string.IsNullOrEmpty(key.motionName2) &&
                                           string.IsNullOrEmpty(key.motionName3);

                        if (!isLegitRest)
                        {
                            string missingMotionName = !string.IsNullOrEmpty(key.motionName) ? key.motionName
                                                     : (!string.IsNullOrEmpty(key.motionName2) ? key.motionName2
                                                     : (!string.IsNullOrEmpty(key.motionName3) ? key.motionName3 : "(未知动作名)"));
                            Debug.LogWarning($"[LiveMotion] 角色 {targetIndex + 1} 关键帧动作剪辑缺失: {missingMotionName} (歌曲: {musicId})");
                        }
                    }
                }

                // c) 整条序列若没有任何有效主通道 Clip，只记录诊断信息，不再自动绑定「专属舞蹈长动作」。
                // 自动绑定是在「猜」：用整首长剪辑顶替逐帧动作，既会破坏姿态，又因为每帧整段骨架 Sample() 造成掉帧。
                // 需要该兜底时请显式打开下面的开关。
                if (validClipCount == 0 && musicId > 0 && AllowExclusiveDanceFallback)
                {
                    _exclusiveDanceClip = ResolveCharaExclusiveDanceClip(musicId, targetIndex);
                    if (_exclusiveDanceClip != null)
                    {
                        validClipCount++;
                        RegisterClipToAnimation(_exclusiveDanceClip);
                        _targetAnim = _exclusiveDanceClip;
                        _lastValidClip = _exclusiveDanceClip;
                        _lastValidSampleTime = 0f;
                        Debug.Log($"[LiveMotion] 角色 {targetIndex + 1} 成功兜底绑定 Live 专属舞蹈动作: {_exclusiveDanceClip.name}");
                    }
                }
                else if (validClipCount == 0 && musicId > 0)
                {
                    Debug.LogWarning($"[LiveMotion] 角色 {targetIndex + 1} 整条动作序列没有有效主通道剪辑 (歌曲: {musicId})，已按官方语义保持上一姿态，未启用长动作兜底");
                }

                // 若经过专属动作动态嗅探后仍没有任何有效 Clip，才弹出错误警告
                if (validClipCount == 0)
                {
                    UmaErrorManager.ShowUIMessage($"[Live动作异常] 角色 {targetIndex + 1} 动作数据缺失，角色将保持初始姿态(T-pose)，请检查对应 Live 动作资源是否下载完整", UIMessageType.Error);
                }
            }

            // 增强容错：在结尾设置 wrapMode 与 enabled 前必须校验 _tempAnim != null
            if (_tempAnim != null)
            {
                _tempAnim.wrapMode = WrapMode.Clamp;
                _tempAnim.enabled = false;
            }
        }

        /// <summary>
        /// 安全向角色 Animation 组件注册指定动作剪辑，内部包含同名去重检查
        /// </summary>
        private void RegisterClipToAnimation(AnimationClip clip)
        {
            if (_tempAnim == null || clip == null)
                return;

            if (_tempAnim.GetClip(clip.name) == null)
            {
                _tempAnim.AddClip(clip, clip.name);
            }
        }

        /// <summary>
        /// 从 Live 专属动作资源中按角色站位序号嗅探提取该角色的专属舞蹈长动作。
        /// 覆盖 anm_liv_son{musicId}_{targetIndex+1:D2}、anm_liv_son{musicId}_{targetIndex+1}st/th 等候选。
        /// </summary>
        /// <param name="musicId">Live 歌曲编号</param>
        /// <param name="targetIndex">角色索引编号（0-indexed）</param>
        /// <returns>解析成功的专属长动作剪辑，若未找到返回 null</returns>
        private AnimationClip ResolveCharaExclusiveDanceClip(int musicId, int targetIndex)
        {
            if (musicId <= 0)
                return null;

            int pos = targetIndex + 1;
            string ordinal = GetOrdinalSuffix(pos);

            // 1. 构造多维度的候选动作名列表
            List<string> candidateNames = new List<string>
            {
                $"anm_liv_son{musicId}_{pos:D2}",
                $"anm_liv_son{musicId}_{pos}",
                $"anm_liv_son{musicId}_{ordinal}",
                $"anm_liv_son{musicId}_pos{pos:D2}",
                $"anm_live_son{musicId}_{pos:D2}",
                $"anm_live_son{musicId}_{pos}",
                $"anm_live_son{musicId}_{ordinal}",
                $"son{musicId}_{pos:D2}",
                $"son{musicId}_{pos}"
            };

            // 2. 优先通过 LiveTimelineMotionClipResolver 动态解析候选动作
            for (int i = 0; i < candidateNames.Count; i++)
            {
                AnimationClip clip = LiveTimelineMotionClipResolver.ResolveClip(candidateNames[i], musicId);
                if (clip != null)
                {
                    return clip;
                }
            }

            // 3. 若名字直查未命中，直接在 AbMotions 中按前缀动态扫描匹配专属动作包
            try
            {
                if (UmaViewerMain.Instance != null && UmaViewerMain.Instance.AbMotions != null)
                {
                    string sonBodyPrefix = $"3d/motion/live/body/son{musicId}";
                    var matchedMotions = new List<UmaDatabaseEntry>();

                    foreach (var entry in UmaViewerMain.Instance.AbMotions)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Name) || !entry.IsAssetBundle)
                            continue;

                        if (entry.Name.StartsWith(sonBodyPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedMotions.Add(entry);
                        }
                    }

                    if (matchedMotions.Count > 0)
                    {
                        // 优先按文件名末尾匹配站位标识
                        string pD2 = $"_{pos:D2}";
                        string pNum = $"_{pos}";
                        string pOrd = $"_{ordinal}";
                        string pPos = $"_pos{pos:D2}";

                        UmaDatabaseEntry bestEntry = null;
                        for (int i = 0; i < matchedMotions.Count; i++)
                        {
                            string fileName = Path.GetFileNameWithoutExtension(matchedMotions[i].Name);
                            if (fileName.EndsWith(pD2, StringComparison.OrdinalIgnoreCase) ||
                                fileName.EndsWith(pNum, StringComparison.OrdinalIgnoreCase) ||
                                fileName.EndsWith(pOrd, StringComparison.OrdinalIgnoreCase) ||
                                fileName.EndsWith(pPos, StringComparison.OrdinalIgnoreCase))
                            {
                                bestEntry = matchedMotions[i];
                                break;
                            }
                        }

                        // 若未通过后缀命中，且站位在匹配列表范围内，按字典序对齐选取
                        if (bestEntry == null && targetIndex < matchedMotions.Count)
                        {
                            matchedMotions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                            bestEntry = matchedMotions[targetIndex];
                        }

                        if (bestEntry != null)
                        {
                            AssetBundle ab = UmaAssetManager.LoadAssetBundle(bestEntry, neverUnload: true);
                            if (ab != null)
                            {
                                string targetName = Path.GetFileNameWithoutExtension(bestEntry.Name);
                                AnimationClip clip = ab.LoadAsset<AnimationClip>(targetName);
                                if (clip == null)
                                {
                                    var allClips = ab.LoadAllAssets<AnimationClip>();
                                    if (allClips != null && allClips.Length > 0)
                                    {
                                        clip = allClips[0];
                                    }
                                }
                                if (clip != null)
                                {
                                    return clip;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiveMotion] 角色 {pos} 动态嗅探专属动作时发生异常: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 获取数字对应的英文序数词后缀（1st, 2nd, 3rd, 4th等）
        /// </summary>
        private static string GetOrdinalSuffix(int num)
        {
            if (num <= 0) return num.ToString();
            int rem100 = num % 100;
            if (rem100 >= 11 && rem100 <= 13) return $"{num}th";
            switch (num % 10)
            {
                case 1: return $"{num}st";
                case 2: return $"{num}nd";
                case 3: return $"{num}rd";
                default: return $"{num}th";
            }
        }

        /// <summary>
        /// 在遇到合法休止关键帧或动作暂时缺失时，维持上一个有效采样的姿态（Freeze/Hold）或采样基础动作，彻底根治 T-pose 隐患
        /// </summary>
        private void HoldLastPoseOrBasePose()
        {
            if (_tempAnim == null)
                return;

            // 1. 优先使用最近一次成功采样的剪辑与时间戳进行姿态保持 (Freeze/Hold)
            if (_lastValidClip != null)
            {
                var state = _tempAnim[_lastValidClip.name];
                if (state != null)
                {
                    state.enabled = true;
                    state.weight = 1f;
                    state.time = _lastValidSampleTime;
                    _tempAnim.Sample();
                    state.enabled = false;
                    return;
                }
            }

            // 2. 其次尝试采样专属长动作剪辑
            if (_exclusiveDanceClip != null)
            {
                var state = _tempAnim[_exclusiveDanceClip.name];
                if (state != null)
                {
                    state.enabled = true;
                    state.weight = 1f;
                    state.time = 0f;
                    _tempAnim.Sample();
                    state.enabled = false;
                    return;
                }
            }

            // 3. 再次尝试采样备用的 _targetAnim
            if (_targetAnim != null)
            {
                var state = _tempAnim[_targetAnim.name];
                if (state != null)
                {
                    state.enabled = true;
                    state.weight = 1f;
                    state.time = 0f;
                    _tempAnim.Sample();
                    state.enabled = false;
                    return;
                }
            }

            // 4. 兜底尝试遍历动画组件中已注册的任意可用剪辑进行采样，避免角色骨骼丢失形体回退到 T-pose
            foreach (AnimationState st in _tempAnim)
            {
                if (st != null && st.clip != null)
                {
                    st.enabled = true;
                    st.weight = 1f;
                    st.time = 0f;
                    _tempAnim.Sample();
                    st.enabled = false;
                    break;
                }
            }
        }

        /// <summary>
        /// 每帧根据当前 Live 时间更新动作采样并应用到角色的动画组件上
        /// </summary>
        /// <param name="currentTime">当前音乐/时间轴时间（秒）</param>
        /// <param name="timescaleKeys">全局时间缩放关键帧列表</param>
        public void AlterUpdate(float currentTime, LiveTimelineKeyTimescaleDataList timescaleKeys)
        {
            var director = Director.instance;

            // LiveMode 0 独立动作播放模式更新
            if (director != null && director.liveMode == 0)
            {
                AnimationClip playClip = _targetAnim != null ? _targetAnim : _exclusiveDanceClip;
                if (playClip != null && _tempAnim != null)
                {
                    var state = _tempAnim[playClip.name];
                    if (state != null)
                    {
                        if ((director.sliderControl != null && director.sliderControl.is_Touched) || director.IsRecordVMD)
                        {
                            state.time = currentTime;
                            _tempAnim.Play(playClip.name);
                            _motionSetup = false;
                        }
                        else if (!_motionSetup)
                        {
                            state.time = currentTime;
                            _tempAnim.Play(playClip.name);
                            _motionSetup = true;
                        }
                        _lastValidClip = playClip;
                        _lastValidSampleTime = currentTime;
                    }
                }
                return;
            }

            // Timeline 模式关键帧动作更新
            if (_currentKey != null && _tempAnim != null)
            {
                LiveTimelineKeyIndex curKey = LiveTimelineControl.AlterUpdate_Key(_currentKey, currentTime);

                _curIndex = curKey.index;
                LiveTimelineKeyCharaMotionData arg = curKey.key as LiveTimelineKeyCharaMotionData;
                if (arg == null)
                {
                    // 无法取得关键帧时平滑维持上一姿态，避免骨骼重置为 T-pose
                    HoldLastPoseOrBasePose();
                    _prevFrameAnimationTime = currentTime;
                    return;
                }

                AnimationClip anim = arg.clip;

                // 不再把「专属舞蹈长动作」塞进主通道。
                // 那是整首曲子的长剪辑，用它顶替单个关键帧会让角色整体姿态跳到不相干的位置，
                // 而且每帧对整段骨架 Sample() 也是实打实的帧时间开销（掉帧来源之一）。
                // 主通道为空时按官方语义维持上一姿态，走下面的 HoldLastPoseOrBasePose。

                // d) 在 AlterUpdate 中，当遇到合法休止关键帧（anim == null）时，不要直接 return 导致角色变成 T-pose，
                // 而是记录并保持上一个有效采样的姿态（_lastValidClip 姿态 Freeze/Hold），或切换至已注册的基础动作采样
                if (anim == null)
                {
                    bool isLegitRest = (arg.clip2 == null && arg.clip3 == null) &&
                                       string.IsNullOrEmpty(arg.motionName) &&
                                       string.IsNullOrEmpty(arg.motionName2) &&
                                       string.IsNullOrEmpty(arg.motionName3);

                    if (!isLegitRest && _curIndex != _prevIndex)
                    {
                        Debug.LogWarning($"[LiveMotion] 角色 {_targetIndex + 1} 关键帧索引 {_curIndex} 缺失动作剪辑 (动作名: {arg.motionName})，启用姿态维持 Hold 机制");
                        _prevIndex = _curIndex;
                    }

                    HoldLastPoseOrBasePose();
                    _prevFrameAnimationTime = currentTime;
                    return;
                }

                // 计算当前动作采样时间戳
                double start;
                if (arg.isMotionHeadFrameAll)
                {
                    start = (double)arg.motionHeadFrame / 60;
                }
                else
                {
                    start = (charaIndex >= 0 && arg.motionHeadFrameSeparetes != null && charaIndex < arg.motionHeadFrameSeparetes.Length)
                        ? (double)arg.motionHeadFrameSeparetes[charaIndex] / 60
                        : 0;
                }

                // 官方：playSpeed 为 0 时按 1 处理。
                // 资产未序列化 playSpeed 时其默认值就是 0，若直接相乘会让 interval 恒为 0，
                // 动画时间永远停在起始帧（表现为动作"不动"/只摆一个姿势）。
                float playSpeed = Mathf.Approximately(arg.playSpeed, 0f) ? 1f : arg.playSpeed;

                double interval = 0;
                double last_current_time = currentTime;
                // 官方：IsTimescaleDisabled 时跳过时间缩放关键帧，直接按线性时间推进。
                if (!arg.IsTimescaleDisabled && timescaleKeys != null && timescaleKeys.thisList != null && timescaleKeys.thisList.Count > 0)
                {
                    var has_key = false;
                    // 应用时间缩放关键帧
                    for (int i = timescaleKeys.thisList.Count - 1; i >= 0; i--)
                    {
                        var scaleKey = timescaleKeys.thisList[i];
                        if (scaleKey.FrameSecond <= currentTime)
                        {
                            has_key = true;
                            if (scaleKey.FrameSecond <= arg.FrameSecond)
                            {
                                interval += (last_current_time - arg.FrameSecond) * scaleKey.Timescale * playSpeed;
                                break;
                            }
                            else
                            {
                                interval += (last_current_time - scaleKey.FrameSecond) * scaleKey.Timescale * playSpeed;
                                last_current_time = scaleKey.FrameSecond;
                            }
                        }
                    }
                    if (!has_key)
                    {
                        interval = (currentTime - arg.FrameSecond) * playSpeed; // 无时间缩放关键帧，使用默认速度
                    }
                }
                else
                {
                    interval = (currentTime - arg.FrameSecond) * playSpeed;
                }
                float currentAnimationTime = (float)(start + interval);

                var state = _tempAnim[anim.name];
                if (state != null)
                {
                    state.enabled = true;
                    state.weight = 1;
                    state.time = arg.loop ? Mathf.Repeat(currentAnimationTime, state.length) : currentAnimationTime;
                    _tempAnim.Sample();
                    state.enabled = false;

                    // 成功采样后记录为最近有效动作与采样时间点，供休止帧 Freeze/Hold 使用
                    _lastValidClip = anim;
                    _lastValidSampleTime = state.time;
                }
                else
                {
                    // 若尚未注册到动画组件中，尝试即时注册并采样
                    RegisterClipToAnimation(anim);
                    var newState = _tempAnim[anim.name];
                    if (newState != null)
                    {
                        newState.enabled = true;
                        newState.weight = 1;
                        newState.time = arg.loop ? Mathf.Repeat(currentAnimationTime, newState.length) : currentAnimationTime;
                        _tempAnim.Sample();
                        newState.enabled = false;

                        _lastValidClip = anim;
                        _lastValidSampleTime = newState.time;
                    }
                    else if (_curIndex != _prevIndex)
                    {
                        Debug.LogWarning($"[LiveMotion] 角色 {_targetIndex + 1} 动画组件中未注册动作剪辑: {anim.name}，启用姿态维持 Hold 机制");
                        HoldLastPoseOrBasePose();
                    }
                }
                _prevIndex = _curIndex;
            }
            _prevFrameAnimationTime = currentTime;
        }
    }
}
