using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// LiveTimelineControl 分部类：负责角色动作、面部表情与口型同步更新
    /// </summary>
    public partial class LiveTimelineControl : MonoBehaviour
    {
        // 角色动作序列驱动实例数组
        private LiveTimelineMotionSequence[] _motionSequenceArray;

        // 动作序列关键帧数据列表数组
        public LiveTimelineKeyCharaMotionSeqDataList[] _keyArray;

        // 角色口型同步更新事件
        public event Action<LiveTimelineKeyIndex, float> OnUpdateLipSync;

        // 角色表情数据更新事件 (参数: 表情数据, 当前时间, 角色槽位索引)
        public event Action<FacialDataUpdateInfo, float, int> OnUpdateFacial;

        /// <summary>
        /// 初始化角色动作序列（包含组件健全性断言、边界校验与异常隔离保护）
        /// </summary>
        /// <param name="motionSequence">角色动作序列索引数组</param>
        public void InitCharaMotionSequence(int[] motionSequence)
        {
            try
            {
                // 1. 遍历 Director.instance.charaObjs 为角色挂载 Animation 组件，截断至当前 Live 实际允许站位数 targetCount
                if (Director.instance != null && Director.instance.charaObjs != null)
                {
                    if (Director.instance.charaAnims == null)
                    {
                        Director.instance.charaAnims = new List<Animation>();
                    }
                    else
                    {
                        // 预先清空旧动画组件引用，彻底消除多余残留
                        Director.instance.charaAnims.Clear();
                    }

                    // 截断循环上限为 targetCount，杜绝遍历到未初始化的 11~19 号空站位导致 9 处容器丢失报错
                    int targetCount = Director.instance.allowCount > 0
                        ? Mathf.Min(Director.instance.charaObjs.Count, Director.instance.allowCount)
                        : Director.instance.charaObjs.Count;

                    for (int i = 0; i < targetCount; i++)
                    {
                        try
                        {
                            var obj = Director.instance.charaObjs[i];
                            if (obj == null)
                            {
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 的 GameObject 为空！");
                                UmaErrorManager.ShowUIMessage($"[Live错误] 角色 {i + 1} 对象为空，无法绑定动作", UIMessageType.Error);
                                continue;
                            }

                            var container = obj.GetComponentInChildren<UmaContainer>();
                            if (container != null)
                            {
                                // 获取或新增 Animation 组件，并注册到 Director 角色动画列表
                                var animation = container.gameObject.GetComponent<Animation>();
                                if (animation == null)
                                {
                                    animation = container.gameObject.AddComponent<Animation>();
                                }
                                Director.instance.charaAnims.Add(animation);
                            }
                            else
                            {
                                // 未找到 UmaContainer 组件：记录错误日志并调用统一 UI 弹窗进行中文错误报警
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 未找到 UmaContainer 组件，无法挂载 Animation！");
                                UmaErrorManager.ShowUIMessage($"[Live错误] 角色 {i + 1} 容器丢失，无法绑定动作", UIMessageType.Error);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 单角色挂载异常隔离，避免连带崩溃
                            Debug.LogError($"[LiveTimeline] 挂载角色槽位 {i} 的 Animation 组件时发生未预料异常:\n{ex}");
                            UmaErrorManager.ShowUIMessage($"[Live错误] 角色 {i + 1} 动作组件挂载失败: {ex.Message}", UIMessageType.Error);
                        }
                    }
                }
                else
                {
                    Debug.LogError("[LiveTimeline] Director.instance 或 charaObjs 为空，无法初始化角色动作组件！");
                    UmaErrorManager.ShowUIMessage("[Live错误] 角色列表为空，无法初始化动作组件", UIMessageType.Error);
                }

                // 2. 获取并构建动作关键帧序列数组 _keyArray，触发批量动作动态解析绑定
                int musicId = Director.instance != null && Director.instance.live != null ? Director.instance.live.MusicId : 0;
                if (data != null && data.worksheetList != null)
                {
                    for (int i = 0; i < data.worksheetList.Count; i++)
                    {
                        var ws = data.worksheetList[i];
                        if (ws != null)
                        {
                            // 遍历工作表，批量预解析并注入缺失的 AnimationClip
                            ws.ResolveCharaMotions(musicId);
                        }
                    }
                }

                int listCount = 0;
                if (data != null && data.worksheetList != null && data.worksheetList.Count > 0 && data.worksheetList[0].charaMotSeqList != null)
                {
                    listCount = data.worksheetList[0].charaMotSeqList.Count;
                }
                else
                {
                    Debug.LogError("[LiveTimeline] 时间轴数据缺少角色动作序列列表 (charaMotSeqList)！");
                }

                _keyArray = new LiveTimelineKeyCharaMotionSeqDataList[listCount];
                for (int i = 0; i < listCount; i++)
                {
                    _keyArray[i] = data.worksheetList[0].charaMotSeqList[i].keys;
                }

                // 3. 校验 Director 实例及全局状态
                if (Director.instance == null)
                {
                    Debug.LogError("[LiveTimeline] Director.instance 为空，无法设置动作序列！");
                    return;
                }

                int charaPositionMax = Director.instance.allowCount;
                _motionSequenceArray = new LiveTimelineMotionSequence[charaPositionMax];

                // 4. 根据 liveMode 分流进行动作初始化
                if (Director.instance.liveMode == 1)
                {
                    // 动作模式 1：基于时间轴动作序列驱动
                    for (int i = 0; i < charaPositionMax; i++)
                    {
                        try
                        {
                            // 校验槽位角色是否存在
                            if (Director.instance.charaObjs == null || i >= Director.instance.charaObjs.Count || Director.instance.charaObjs[i] == null)
                            {
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 不存在或对象为空，跳过动作绑定！");
                                continue;
                            }

                            // 校验 motionSequence 数组有效性与长度
                            int seqDataIndex = 0;
                            if (motionSequence == null || motionSequence.Length == 0)
                            {
                                Debug.LogError($"[LiveTimeline] motionSequence 数组为空，角色槽位 {i} 降级使用默认索引 0！");
                                seqDataIndex = 0;
                            }
                            else if (i >= motionSequence.Length)
                            {
                                Debug.LogError($"[LiveTimeline] motionSequence 长度不足 (长度: {motionSequence.Length}，请求槽位: {i})，角色槽位 {i} 降级使用默认索引 0！");
                                seqDataIndex = 0;
                            }
                            else
                            {
                                seqDataIndex = motionSequence[i];
                            }

                            // 校验 _keyArray 边界，做安全边界限制，杜绝越界抛错
                            if (_keyArray == null || _keyArray.Length == 0)
                            {
                                Debug.LogError($"[LiveTimeline] _keyArray 为空或长度为 0，角色槽位 {i} 动作序列数据缺失！");
                                seqDataIndex = 0;
                            }
                            else if (seqDataIndex >= _keyArray.Length || seqDataIndex < 0)
                            {
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 的动作序列索引 seqDataIndex={seqDataIndex} 越界 (有效范围: 0 ~ {_keyArray.Length - 1})，已安全限制为边界值！");
                                seqDataIndex = Mathf.Clamp(seqDataIndex, 0, _keyArray.Length - 1);
                            }

                            _motionSequenceArray[i] = new LiveTimelineMotionSequence();
                            _motionSequenceArray[i].Initialize(Director.instance.charaObjs[i], i, seqDataIndex, this);
                        }
                        catch (Exception ex)
                        {
                            // 单角色动作异常隔离：输出完整堆栈并弹出友好提示，避免连带阻断后续角色
                            Debug.LogError($"[LiveTimeline] 角色槽位 {i} 动作序列初始化异常:\n{ex}");
                            UmaErrorManager.ShowUIMessage($"[Live错误] 角色 {i + 1} 动作初始化失败: {ex.Message}", UIMessageType.Error);
                        }
                    }
                }
                else if (Director.instance.liveMode == 0)
                {
                    // 动作模式 0：基于 AssetBundle 的全局动作加载驱动
                    List<AnimationClip> anims = new List<AnimationClip>();
                    try
                    {
                        if (UmaViewerMain.Instance != null && UmaViewerMain.Instance.AbMotions != null)
                        {
                            foreach (var motion in UmaViewerMain.Instance.AbMotions.Where(a => a.Name.StartsWith($"3d/motion/live/body/son{Director.instance.live.MusicId}") && Path.GetFileName(a.Name).Split('_').Length == 4))
                            {
                                AssetBundle motionAB = UmaAssetManager.LoadAssetBundle(motion);
                                if (motionAB != null)
                                {
                                    AnimationClip motionAnim = motionAB.LoadAsset<AnimationClip>(Path.GetFileName(motion.Name).Split('.')[0]);
                                    if (motionAnim != null)
                                    {
                                        anims.Add(motionAnim);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[LiveTimeline] 加载动作剪辑列表失败:\n{ex}");
                        UmaErrorManager.ShowUIMessage($"[Live错误] 基础动作剪辑加载失败: {ex.Message}", UIMessageType.Error);
                    }

                    for (int i = 0; i < charaPositionMax; i++)
                    {
                        try
                        {
                            if (Director.instance.charaObjs == null || i >= Director.instance.charaObjs.Count || Director.instance.charaObjs[i] == null)
                            {
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 不存在或对象为空，跳过动作绑定！");
                                continue;
                            }

                            int seqDataIndex = 0;
                            if (motionSequence == null || motionSequence.Length == 0)
                            {
                                Debug.LogError($"[LiveTimeline] motionSequence 数组为空，角色槽位 {i} 降级使用默认索引 0！");
                                seqDataIndex = 0;
                            }
                            else if (i >= motionSequence.Length)
                            {
                                Debug.LogError($"[LiveTimeline] motionSequence 长度不足 (长度: {motionSequence.Length}，请求槽位: {i})，角色槽位 {i} 降级使用默认索引 0！");
                                seqDataIndex = 0;
                            }
                            else
                            {
                                seqDataIndex = motionSequence[i];
                            }

                            if (_keyArray == null || _keyArray.Length == 0)
                            {
                                Debug.LogError($"[LiveTimeline] _keyArray 为空或长度为 0，角色槽位 {i} 动作序列数据缺失！");
                                seqDataIndex = 0;
                            }
                            else if (seqDataIndex >= _keyArray.Length || seqDataIndex < 0)
                            {
                                Debug.LogError($"[LiveTimeline] 角色槽位 {i} 的动作序列索引 seqDataIndex={seqDataIndex} 越界 (有效范围: 0 ~ {_keyArray.Length - 1})，已安全限制为边界值！");
                                seqDataIndex = Mathf.Clamp(seqDataIndex, 0, _keyArray.Length - 1);
                            }

                            _motionSequenceArray[i] = new LiveTimelineMotionSequence();
                            _motionSequenceArray[i].Initialize(Director.instance.charaObjs[i], i, seqDataIndex, this, anims);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[LiveTimeline] 角色槽位 {i} (模式0) 动作初始化发生异常:\n{ex}");
                            UmaErrorManager.ShowUIMessage($"[Live错误] 角色 {i + 1} 动作初始化失败: {ex.Message}", UIMessageType.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 全局未预料异常保护与友好弹窗提示
                Debug.LogError($"[LiveTimeline] InitCharaMotionSequence 全局执行异常:\n{ex}");
                UmaErrorManager.ShowUIMessage($"[Live错误] 角色动作序列初始化严重异常: {ex.Message}", UIMessageType.Error);
            }
        }

        public void AlterUpdate_CharaMotionSequence(float liveTime)
        {
            foreach (var motion in _motionSequenceArray)
            {
                motion.AlterUpdate(liveTime, data.worksheetList[0].timescaleKeys);
            }
        }

        public void AlterUpdate_FacialData(float liveTime)
        {
            var facialDataList = data.worksheetList[0].facial1Set;
            FacialDataUpdateInfo updateInfo = default(FacialDataUpdateInfo);
            if (facialDataList != null)
            {
                SetupFacialUpdateInfo_Mouth(ref updateInfo, facialDataList.mouthKeys, liveTime);
                SetupFacialUpdateInfo_Eye(ref updateInfo, facialDataList.eyeKeys, liveTime);
                SetupFacialUpdateInfo_Eyebrow(ref updateInfo, facialDataList.eyebrowKeys, liveTime);
                SetupFacialUpdateInfo_Ear(ref updateInfo, facialDataList.earKeys, liveTime);
                SetupFacialUpdateInfo_EyeTrack(ref updateInfo, facialDataList.eyeTrackKeys, liveTime);
                SetupFacialUpdateInfo_Effect(ref updateInfo, facialDataList.effectKeys, liveTime);
                this.OnUpdateFacial(updateInfo, liveTime, 0);
            }

            var otherFacialDataList = data.worksheetList[0].other4FacialArray;
            for (int i = 0; i < otherFacialDataList.Length && i < Director.instance.characterCount - 1; i++)
            {
                SetupFacialUpdateInfo_Mouth(ref updateInfo, otherFacialDataList[i].mouthKeys, liveTime);
                SetupFacialUpdateInfo_Eye(ref updateInfo, otherFacialDataList[i].eyeKeys, liveTime);
                SetupFacialUpdateInfo_Eyebrow(ref updateInfo, otherFacialDataList[i].eyebrowKeys, liveTime);
                SetupFacialUpdateInfo_Ear(ref updateInfo, otherFacialDataList[i].earKeys, liveTime);
                SetupFacialUpdateInfo_EyeTrack(ref updateInfo, otherFacialDataList[i].eyeTrackKeys, liveTime);
                SetupFacialUpdateInfo_Effect(ref updateInfo, otherFacialDataList[i].effectKeys, liveTime);
                this.OnUpdateFacial(updateInfo, liveTime, i + 1);
            }
        }

        private void SetupFacialUpdateInfo_Mouth(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialMouthDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.mouthPrev = liveTimelineKey as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthCur = liveTimelineKey2 as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthNext = liveTimelineKey3 as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Eye(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyeDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyePrev = liveTimelineKey as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeCur = liveTimelineKey2 as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeNext = liveTimelineKey3 as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Effect(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEffectDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.key;

                updateInfo.effect = liveTimelineKey as LiveTimelineKeyFacialEffectData;
                updateInfo.effectKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Eyebrow(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyebrowDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyebrowPrev = liveTimelineKey as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowCur = liveTimelineKey2 as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowNext = liveTimelineKey3 as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Ear(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEarDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.earPrev = liveTimelineKey as LiveTimelineKeyFacialEarData;
                updateInfo.earCur = liveTimelineKey2 as LiveTimelineKeyFacialEarData;
                updateInfo.earNext = liveTimelineKey3 as LiveTimelineKeyFacialEarData;
                updateInfo.earKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_EyeTrack(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyeTrackDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyeTrackPrev = liveTimelineKey as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackCur = liveTimelineKey2 as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackNext = liveTimelineKey3 as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackKeyIndex = curKey.index;
            }
        }

        public void AlterUpdate_LipSync(float liveTime)
        {
            var lipDataList = data.worksheetList[0].ripSyncKeys;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(lipDataList, liveTime);

            if (curKey != null && curKey.index != -1)
            {
                this.OnUpdateLipSync(curKey, liveTime);
            }
        }

        public void AlterUpdate_LipSync2(float liveTime)
        {
            var lipDataList = data.worksheetList[0].ripSync2Keys;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(lipDataList, liveTime);

            if (curKey != null && curKey.index != -1)
            {
                this.OnUpdateLipSync(curKey, liveTime);
            }
        }

    }
}
