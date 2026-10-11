using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 道具时间轴关键帧数据类
    /// 记录指定时间节点上道具的显隐状态、着色材质参数、动画剪辑播放及平行光角度配置
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyPropsData : LiveTimelineKey
    {
        private const int AttrColorLinkAttachedCharacter = 131072;
        private const int AttrDirectionalLightLinkAttachedCharacter = 262144;
        private const int AttrUseDirectionalLight = 524288;
        private const int AttrDirectionalLightCameraFollow = 1048576;
        private const int AttrLightDirectionFix = 4194304;

        /// <summary>
        /// 关键帧数据类型：Props
        /// </summary>
        public override LiveTimelineKeyDataType dataType => LiveTimelineKeyDataType.Props;

        /// <summary>
        /// 动画剪辑名称缓存（只读公开属性）
        /// </summary>
        public string AnimationClipName { get; private set; }

        /// <summary>
        /// 是否与挂载马娘的着色主色同步
        /// </summary>
        public bool LinkColorAttachedCharacter
        {
            get => ((int)attribute & AttrColorLinkAttachedCharacter) != 0;
            private set
            {
                int attr = (int)attribute;
                if (value) attr |= AttrColorLinkAttachedCharacter;
                else attr &= ~AttrColorLinkAttachedCharacter;
                attribute = (LiveTimelineKeyAttribute)attr;
            }
        }

        /// <summary>
        /// 是否与挂载马娘的平行光方向同步
        /// </summary>
        public bool LinkDirectionalLightAttachedCharacter
        {
            get => ((int)attribute & AttrDirectionalLightLinkAttachedCharacter) != 0;
            private set
            {
                int attr = (int)attribute;
                if (value) attr |= AttrDirectionalLightLinkAttachedCharacter;
                else attr &= ~AttrDirectionalLightLinkAttachedCharacter;
                attribute = (LiveTimelineKeyAttribute)attr;
            }
        }

        /// <summary>
        /// 是否使用道具独立的自定义平行光
        /// </summary>
        public bool UseOriginalDirectionalLight
        {
            get => ((int)attribute & AttrUseDirectionalLight) != 0;
            private set
            {
                int attr = (int)attribute;
                if (value) attr |= AttrUseDirectionalLight;
                else attr &= ~AttrUseDirectionalLight;
                attribute = (LiveTimelineKeyAttribute)attr;
            }
        }

        /// <summary>
        /// 是否固定平行光照射方向
        /// </summary>
        public bool UseLightDirectionFix
        {
            get => ((int)attribute & AttrLightDirectionFix) != 0;
            private set
            {
                int attr = (int)attribute;
                if (value) attr |= AttrLightDirectionFix;
                else attr &= ~AttrLightDirectionFix;
                attribute = (LiveTimelineKeyAttribute)attr;
            }
        }

        /// <summary>
        /// 平行光是否跟随相机朝向
        /// </summary>
        public bool DirectionalLightCameraFollow
        {
            get => ((int)attribute & AttrDirectionalLightCameraFollow) != 0;
            private set
            {
                int attr = (int)attribute;
                if (value) attr |= AttrDirectionalLightCameraFollow;
                else attr &= ~AttrDirectionalLightCameraFollow;
                attribute = (LiveTimelineKeyAttribute)attr;
            }
        }

        /// <summary>
        /// 设定掩码标记位
        /// </summary>
        public int settingFlags;

        /// <summary>
        /// 绑定的道具编号 (Props ID)
        /// </summary>
        public int propsID;

        /// <summary>
        /// 道具渲染器启用开关
        /// </summary>
        public bool rendererEnable;

        /// <summary>
        /// 道具显隐是否跟随挂载角色的显隐
        /// </summary>
        public bool IsVisibleAttachedCharaLinked;

        /// <summary>
        /// 镜面反射渲染时是否自动切换所属渲染层
        /// </summary>
        public bool AutoSwitchLayerOnMirrorRendering;

        /// <summary>
        /// 道具主色调
        /// </summary>
        public Color color;

        /// <summary>
        /// 道具根部色调
        /// </summary>
        public Color rootColor;

        /// <summary>
        /// 道具尖端/尾部色调
        /// </summary>
        public Color tipColor;

        /// <summary>
        /// 颜色强度倍率
        /// </summary>
        public float colorPower;

        /// <summary>
        /// 是否应用道具动画
        /// </summary>
        public bool IsApplyAnimation;

        /// <summary>
        /// 是否预热动画状态
        /// </summary>
        public bool IsApplyReserveWarming;

        /// <summary>
        /// 驱动道具的动画剪辑资源
        /// </summary>
        public AnimationClip AnimationClip;

        /// <summary>
        /// 动画起始播放相对时间（秒）
        /// </summary>
        public float StartAnimationTime;

        /// <summary>
        /// 动画起始帧偏移
        /// </summary>
        public int AnimationHeadFrame;

        /// <summary>
        /// 卡通渲染阴影暗部色彩
        /// </summary>
        public Color ToonDarkColor;

        /// <summary>
        /// 卡通渲染高光亮部色彩
        /// </summary>
        public Color ToonBrightColor;

        /// <summary>
        /// 是否投射阴影
        /// </summary>
        public bool IsCastShadow;

        /// <summary>
        /// 是否强制投射阴影
        /// </summary>
        public bool IsCastShadowForced;

        /// <summary>
        /// 序列化的平行光欧拉角度
        /// </summary>
        [SerializeField]
        private Vector3 _directionalLightAngle;

        /// <summary>
        /// 是否更新描边轮廓属性
        /// </summary>
        public bool IsUpdateOutline;

        /// <summary>
        /// 描边轮廓宽度
        /// </summary>
        public float OutlineWidth;

        /// <summary>
        /// 描边轮廓颜色
        /// </summary>
        public Color OutlineColor;

        /// <summary>
        /// 是否开启自发光 (Emissive)
        /// </summary>
        public bool IsEmissive;

        /// <summary>
        /// 自发光颜色
        /// </summary>
        public Color EmissiveColor;

        /// <summary>
        /// 自发光 UV 滚动时间缩放倍率
        /// </summary>
        public float EmissiveScrollTimeScale;

        /// <summary>
        /// 自发光能量缩放倍率
        /// </summary>
        public float EmissiveScrollEnergyScale;

        /// <summary>
        /// 运行时计算所得的平行光四元数旋转
        /// </summary>
        [NonSerialized]
        public Quaternion DirectionalLightRotation;

        /// <summary>
        /// 构造函数，初始化合理的默认配置参数
        /// </summary>
        public LiveTimelineKeyPropsData()
        {
            propsID = -1;
            rendererEnable = true;

            color = Color.white;
            rootColor = Color.white;
            tipColor = Color.white;
            colorPower = 1f;

            ToonDarkColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            ToonBrightColor = Color.white;

            _directionalLightAngle = new Vector3(-75f, 0f, 0f);
            DirectionalLightRotation = Quaternion.Euler(_directionalLightAngle);

            OutlineWidth = 0.325f;
            OutlineColor = Color.black;

            EmissiveColor = Color.white;
            EmissiveScrollTimeScale = 1f;
            EmissiveScrollEnergyScale = 1f;

            AnimationClipName = string.Empty;
        }

        /// <summary>
        /// 关键帧数据加载完成回调，刷新光照旋转四元数及动画名称
        /// </summary>
        /// <param name="timelineControl">时间轴主控控制器</param>
        public override void OnLoad(LiveTimelineControl timelineControl)
        {
            base.OnLoad(timelineControl);
            UpdateDirectionalLightRotation();
            UpdateAnimationClipName();
        }

        /// <summary>
        /// 根据欧拉角更新平行光旋转四元数
        /// </summary>
        public void UpdateDirectionalLightRotation()
        {
            DirectionalLightRotation = Quaternion.Euler(_directionalLightAngle);
        }

        /// <summary>
        /// 同步更新绑定的 AnimationClip 资源名称
        /// </summary>
        public void UpdateAnimationClipName()
        {
            AnimationClipName = AnimationClip != null ? AnimationClip.name : string.Empty;
        }
    }

    /// <summary>
    /// Live 道具关键帧列表模板封装类
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyPropsDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyPropsData>
    {
        /// <summary>
        /// 初始化道具关键帧数据列表容器
        /// </summary>
        public LiveTimelineKeyPropsDataList()
        {
        }
    }
}
