using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 道具骨骼挂载点（Props Attach）数据组容器
    /// 管理指定道具在角色骨骼或父级节点上的空间附着与位姿变换时间轴关键帧
    /// </summary>
    [Serializable]
    public class LiveTimelinePropsAttachData : ILiveTimelineGroupDataWithName, ILiveTimelineVariation
    {
        /// <summary>
        /// 默认轨道组名称
        /// </summary>
        private const string DefaultName = "PropsAttach";

        /// <summary>
        /// 道具挂载关键帧数据列表
        /// </summary>
        [SerializeField]
        public LiveTimelineKeyPropsAttachDataList keys = new LiveTimelineKeyPropsAttachDataList();

        /// <summary>
        /// 是否启用变体条件过滤
        /// </summary>
        [SerializeField]
        private bool _applyVariation;

        /// <summary>
        /// 对应的变体识别 ID
        /// </summary>
        [SerializeField]
        private int _variationId;

        /// <summary>
        /// 获取或设置是否应用变体过滤
        /// </summary>
        public bool ApplyVariation
        {
            get => _applyVariation;
            set => _applyVariation = value;
        }

        /// <summary>
        /// 获取或设置当前轨道绑定的变体 ID
        /// </summary>
        public int VariationId
        {
            get => _variationId;
            set => _variationId = value;
        }

        /// <summary>
        /// 获取当前数据组包含的关键帧列表
        /// </summary>
        /// <returns>关键帧列表对象</returns>
        public override ILiveTimelineKeyDataList GetKeyList()
        {
            return keys;
        }

        /// <summary>
        /// 初始化道具挂载数据组容器
        /// </summary>
        public LiveTimelinePropsAttachData() : base(DefaultName)
        {
        }

        /// <summary>
        /// 评估当前道具挂载轨道在当前变体下是否应激活
        /// </summary>
        /// <returns>若变体有效则返回 true</returns>
        public bool IsEnableVariation()
        {
            if (!_applyVariation)
            {
                return true;
            }

            return Director.IsEnableVariationId(_variationId);
        }
    }
}
