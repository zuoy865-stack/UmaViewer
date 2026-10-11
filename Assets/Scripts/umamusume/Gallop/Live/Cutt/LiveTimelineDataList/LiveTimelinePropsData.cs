using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 时间轴道具（Live Props）数据组容器
    /// 承载一整条道具轨道的所有关键帧及变体激活条件，负责在时间轴驱动期间提供关键帧列表与变体判定
    /// </summary>
    [Serializable]
    public class LiveTimelinePropsData : ILiveTimelineGroupDataWithName, ILiveTimelineVariation
    {
        /// <summary>
        /// 道具轨道默认标识名称常量
        /// </summary>
        public const string PropsTimelineNameFlags = "PropsFlags";

        /// <summary>
        /// 默认轨道组名
        /// </summary>
        private const string DefaultName = "PropsFlags";

        /// <summary>
        /// 道具关键帧列表
        /// </summary>
        [SerializeField]
        public LiveTimelineKeyPropsDataList keys = new LiveTimelineKeyPropsDataList();

        /// <summary>
        /// 是否启用歌曲变体条件过滤
        /// </summary>
        [SerializeField]
        private bool _applyVariation;

        /// <summary>
        /// 匹配的变体编号
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
        /// 获取或设置绑定的变体编号
        /// </summary>
        public int VariationId
        {
            get => _variationId;
            set => _variationId = value;
        }

        /// <summary>
        /// 获取该道具组的关键帧列表接口
        /// </summary>
        /// <returns>关键帧列表对象</returns>
        public override ILiveTimelineKeyDataList GetKeyList()
        {
            return keys;
        }

        /// <summary>
        /// 初始化道具轨道数据组
        /// </summary>
        public LiveTimelinePropsData() : base(DefaultName)
        {
        }

        /// <summary>
        /// 评估当前道具轨道在当前 Live 变体环境下是否应激活播放
        /// </summary>
        /// <returns>若未开启变体或当前变体匹配则返回 true，否则返回 false</returns>
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
