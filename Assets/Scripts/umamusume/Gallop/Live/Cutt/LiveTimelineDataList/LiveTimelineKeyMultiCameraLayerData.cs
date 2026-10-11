using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 多机位图层关键帧数据类
    /// 承载多机位画面分屏线条、渐变、变换参数、旋转及剪裁位置等核心分屏渲染属性
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyMultiCameraLayerData : LiveTimelineKeyWithInterpolate
    {
        /// <summary>
        /// 关键帧数据类型：MultiCameraLayer
        /// </summary>
        public override LiveTimelineKeyDataType dataType => LiveTimelineKeyDataType.MultiCameraLayer;

        /// <summary>
        /// 多机位通道编号
        /// </summary>
        public int MultiCameraNo;

        /// <summary>
        /// 分屏分割线类型（Fade渐变 或 Color纯色）
        /// </summary>
        public MultiCameraComposite.DivideLineType LineType;

        /// <summary>
        /// 分割线厚度
        /// </summary>
        public float LineThickness;

        /// <summary>
        /// 分割线颜色
        /// </summary>
        public Color LineColor;

        /// <summary>
        /// 图层淡入淡出透明度权重
        /// </summary>
        public float FadeValue;

        /// <summary>
        /// 分屏贴图及 UV 变换参数 (X: MaskOffsetX, Y: MaskOffsetY, Z: MaskRoll, W: 扇形/扩展参数)
        /// </summary>
        public Vector4 TransformParameter;

        /// <summary>
        /// 分屏遮罩旋转角度
        /// </summary>
        public float MaskRoll;

        /// <summary>
        /// 图层最大坐标偏移范围
        /// </summary>
        public Vector3 offsetMaxPosition;

        /// <summary>
        /// 图层最小坐标偏移范围
        /// </summary>
        public Vector3 offsetMinPosition;

        /// <summary>
        /// 构造函数，初始化多机位图层关键帧的默认参数
        /// </summary>
        public LiveTimelineKeyMultiCameraLayerData()
        {
            MultiCameraNo = 0;
            LineType = MultiCameraComposite.DivideLineType.Color;
            LineThickness = 0.015f;
            LineColor = Color.white;
            FadeValue = 1f;
            TransformParameter = Vector4.zero;
            MaskRoll = 0f;
            offsetMaxPosition = Vector3.zero;
            offsetMinPosition = Vector3.zero;
        }

        /// <summary>
        /// 关键帧加载完成时回调
        /// </summary>
        /// <param name="timelineControl">时间轴控制器</param>
        public override void OnLoad(LiveTimelineControl timelineControl)
        {
            base.OnLoad(timelineControl);
        }
    }

    /// <summary>
    /// 多机位图层关键帧数据列表模板封装
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyMultiCameraLayerDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyMultiCameraLayerData>
    {
        /// <summary>
        /// 初始化多机位图层关键帧列表
        /// </summary>
        public LiveTimelineKeyMultiCameraLayerDataList()
        {
        }
    }
}
