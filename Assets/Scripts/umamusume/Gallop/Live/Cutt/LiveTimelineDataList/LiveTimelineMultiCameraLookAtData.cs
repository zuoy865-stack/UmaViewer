using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    [System.Serializable]
    public class LiveTimelineKeyMultiCameraLookAtData : LiveTimelineKeyCameraLookAtData
    {

    }

    [System.Serializable]
    public class LiveTimelineKeyMultiCameraLookAtDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyMultiCameraLookAtData>
    {

    }

    [System.Serializable]
    public class LiveTimelineMultiCameraLookAtData : ILiveTimelineGroupDataWithName
    {
        private const string default_name = "MultiCameraLookAt";

        /// <summary>
        /// 多机位注视点关键帧数据列表，初始化默认空容器防止未反序列化时空引用
        /// </summary>
        public LiveTimelineKeyMultiCameraLookAtDataList keys = new LiveTimelineKeyMultiCameraLookAtDataList();

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
        public LiveTimelineMultiCameraLookAtData() : base(default_name)
        {
        }
    }
}
