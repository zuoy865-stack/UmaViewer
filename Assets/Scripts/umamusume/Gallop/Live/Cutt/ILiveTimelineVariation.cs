namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 时间轴轨道变体配置接口
    /// 用于标记和控制轨道数据组是否根据歌曲的变体配置（Variation ID）进行条件化过滤与启用
    /// </summary>
    public interface ILiveTimelineVariation
    {
        /// <summary>
        /// 是否应用变体过滤条件
        /// 为 true 时仅当匹配对应变体编号才播放该轨道；为 false 时无条件生效
        /// </summary>
        bool ApplyVariation { get; set; }

        /// <summary>
        /// 绑定的演出变体唯一编号 (Variation ID)
        /// </summary>
        int VariationId { get; set; }
    }
}
