using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    

    [System.Serializable]
    public class LiveTimelineKeyCharaMotionData : LiveTimelineKey
    {
        public override LiveTimelineKeyDataType dataType
        {
            get
            {
                return LiveTimelineKeyDataType.CharaMotionSequecne;
            }
        }
        public string motionName;
        public string motionName2;
        public string motionName3;
        public AnimationClip clip;
        public AnimationClip clip2;
        public AnimationClip clip3;

        // 官方替换动作通道。字段名/类型必须与资产里的保持一致，否则反序列化拿不到数据。
        // 官方用它按角色/服装条件挑一段替换动作剪辑，用于手持物、换装等姿态切换。
        public SwapMotionData[] SwapMotionDataArray;

        public int overrideMotionSysTextId;
        public bool useOverrideMotionFacial;
        public int motionHeadFrame;
        public int playFrameLength;
        public float playSpeed;
        public bool loop;
        public bool isMotionHeadFrameAll;
        public int[] motionHeadFrameSeparetes;

        // 官方字段：为真时该关键帧不参与全局时间缩放（timescale）换算，直接按线性时间推进。
        [UnityEngine.SerializeField] private bool _isTimescaleDisabled;

        public bool IsTimescaleDisabled => _isTimescaleDisabled;

        // 官方运行时字段：动作加载后写入，播放时按名称取 AnimationState。不参与序列化。
        [System.NonSerialized] public string StateName;
        [System.NonSerialized] public string StateName2;
        [System.NonSerialized] public string StateName3;

        // 官方默认值为 0；大于 0 时由系统文本动作接管（当前工程尚无对应播放器，保留判据备用）。
        public bool UseOverrideSysTextAnimation => overrideMotionSysTextId > 0;

        // 官方 LiveTimelineKeyCharaMotionData.SwapMotionData。
        // 序列化字段名/类型/顺序对齐资产，否则取不到数据。
        [System.Serializable]
        public class SwapMotionData
        {
            public TargetMotionType TargetMotion;
            public int TargetOrder;
            public bool ActiveCharaId;
            public int CharaId;
            public bool ActiveDressId;
            public int DressId;
            public DressConditionType DressCondition;
            public string SwapMotionName;
            public AndConditionData[] AndConditionDataArray;

            [System.NonSerialized] public AnimationClip SwapAnimationClip;
            [System.NonSerialized] public string SwapAnimationClipName;
            [System.NonSerialized] public bool IsEnabledSwapAnimationClip;

            public enum TargetMotionType
            {
                AnimationClip1 = 1,
                AnimationClip2 = 2,
                AnimationClip3 = 3
            }

            public enum DressConditionType
            {
                None = 0,
                Moesode = 1
            }

            [System.Serializable]
            public class AndConditionData
            {
                public int TargetOrder;
                public bool ActiveCharaId;
                public int CharaId;
                public bool ActiveDressId;
                public int DressId;
                public DressConditionType DressCondition;
            }
        }
    }

    [System.Serializable]
    public class LiveTimelineKeyCharaMotionSeqDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyCharaMotionData>
    {

    }

    [System.Serializable]
    public class LiveTimelineCharaMotSeqData : ILiveTimelineGroupData
    {
        public LiveTimelineKeyCharaMotionSeqDataList keys;
        [SerializeField]
        private bool _existsOverrideMotionSet;
    }
}
