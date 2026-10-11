using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    [System.Serializable]
    public class LiveTimelineKeyMultiCameraPositionData : LiveTimelineKeyCameraPositionData
    {
        public enum MaskType
        {
            Single = 0,
            All = 1,
            Up = 2,
            Down = 3,
            Left = 4,
            Right = 5,
            LeftUp = 6,
            RightUp = 7,
            LeftDown = 8,
            RightDown = 9,
            Fan = 10 // 扇形遮罩
        }

        public bool enableMultiCamera;
        public float fadeTime;
        public float lineThickness;
        public MultiCameraComposite.DivideLineType LineType;
        public Color LineColor;
        public float LineAntialiasing;
        public LiveTimelineKeyMultiCameraPositionData.MaskType maskType;
        public bool updateMainCamera;
        public float roll;
        public float fov;
        public float maskRoll;
        public Vector2 maskOffset;
        public float MaskCentralAngle; // 扇形遮罩中心角
    }

    [System.Serializable]
    public class LiveTimelineKeyMultiCameraPositionDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyMultiCameraPositionData>
    {

    }

    [System.Serializable]
    public class LiveTimelineMultiCameraPositionData : ILiveTimelineGroupDataWithName
    {
        private const string default_name = "MultiCameraPos";

        /// <summary>
        /// 多机位位置关键帧数据列表，初始化默认空容器防止未反序列化时空引用
        /// </summary>
        public LiveTimelineKeyMultiCameraPositionDataList keys = new LiveTimelineKeyMultiCameraPositionDataList();

        /// <summary>
        /// 多机位序号（0~N），与 AssetBundle 反序列化字段对齐
        /// </summary>
        public int MultiCameraNo;

        /// <summary>
        /// 重写基类统一获取关键帧列表接口
        /// </summary>
        public override ILiveTimelineKeyDataList GetKeyList()
        {
            return keys;
        }

        /// <summary>
        /// 默认构造函数，调用基类构造并注册轨道名称
        /// </summary>
        public LiveTimelineMultiCameraPositionData() : base(default_name)
        {
        }
    }
}