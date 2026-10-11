using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 多机位图层（Multi-Camera Layer）时间轴数据组容器
    /// 承载多机位分屏组合中特定机位图层的时间轴关键帧序列与机位通道映射编号
    /// </summary>
    [Serializable]
    public class LiveTimelineMultiCameraLayerData : ILiveTimelineGroupDataWithName
    {
        /// <summary>
        /// 默认轨道组名称
        /// </summary>
        private const string DefaultName = "MultiCameraLayer";

        /// <summary>
        /// 多机位图层关键帧列表
        /// </summary>
        [SerializeField]
        public LiveTimelineKeyMultiCameraLayerDataList keys = new LiveTimelineKeyMultiCameraLayerDataList();

        /// <summary>
        /// 对应的多机位通道编号 (MultiCamera No)
        /// </summary>
        public int MultiCameraNo;

        /// <summary>
        /// 获取当前多机位图层的关键帧列表
        /// </summary>
        /// <returns>关键帧列表接口对象</returns>
        public override ILiveTimelineKeyDataList GetKeyList()
        {
            return keys;
        }

        /// <summary>
        /// 初始化多机位图层组数据容器
        /// </summary>
        public LiveTimelineMultiCameraLayerData() : base(DefaultName)
        {
        }
    }
}
