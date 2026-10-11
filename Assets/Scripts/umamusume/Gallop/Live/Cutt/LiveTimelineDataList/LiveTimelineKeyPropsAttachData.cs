using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live 道具挂载关键帧数据类
    /// 描述道具在特定时间轴帧上依附的角色/父级骨骼节点、空间位移偏置、旋转及缩放信息
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyPropsAttachData : LiveTimelineKeyWithInterpolate
    {
        /// <summary>
        /// 挂载目标主体类型
        /// </summary>
        public enum AttachType
        {
            /// <summary>
            /// 挂载到角色骨骼节点（如手部骨骼 Hand_Attach_R）
            /// </summary>
            Chara = 0,

            /// <summary>
            /// 挂载到另一个道具实体节点
            /// </summary>
            Prop = 1
        }

        /// <summary>
        /// 关键帧类型：PropsAttach
        /// </summary>
        public override LiveTimelineKeyDataType dataType => LiveTimelineKeyDataType.PropsAttach;

        /// <summary>
        /// 目标挂载关节骨骼名称
        /// </summary>
        public string _attachJointName;

        /// <summary>
        /// 目标挂载关节名称哈希
        /// </summary>
        public int _attachJointHash;

        /// <summary>
        /// 目标位置复制源骨骼名称
        /// </summary>
        public string _copyPositionJointName;

        /// <summary>
        /// 目标位置复制源骨骼名称哈希
        /// </summary>
        public int _copyPositionJointHash;

        /// <summary>
        /// 属性配置标志位
        /// </summary>
        public int _settingFlags;

        /// <summary>
        /// 被挂载的道具 ID
        /// </summary>
        public int _propsId;

        /// <summary>
        /// 本地坐标偏移位置
        /// </summary>
        public Vector3 _offsetPosition;

        /// <summary>
        /// 本地旋转偏移欧拉角
        /// </summary>
        public Vector3 OffsetRotate;

        /// <summary>
        /// 本地缩放偏移
        /// </summary>
        public Vector3 OffsetScale;

        /// <summary>
        /// 欧拉角计算所得的四元数旋转偏置（运行时计算）
        /// </summary>
        public Quaternion OffsetRotation { get; private set; }

        /// <summary>
        /// 是否联动锁定到挂载骨骼
        /// </summary>
        public bool IsLinkAttachBone;

        /// <summary>
        /// 挂载所属目标类型（角色或道具）
        /// </summary>
        public AttachType _attachType;

        /// <summary>
        /// 若挂载到道具时，所挂载的目标父级道具 ID
        /// </summary>
        public int _attachPropId;

        /// <summary>
        /// 若挂载到道具时，所依附的目标道具节点名称
        /// </summary>
        public string _attachTargetPropNodeName;

        /// <summary>
        /// 构造函数，初始化默认安全参数
        /// </summary>
        public LiveTimelineKeyPropsAttachData()
        {
            _attachJointName = string.Empty;
            _copyPositionJointName = string.Empty;
            _propsId = -1;
            _attachPropId = -1;

            _offsetPosition = Vector3.zero;
            OffsetRotate = Vector3.zero;
            OffsetScale = Vector3.one;
            OffsetRotation = Quaternion.identity;
            _attachType = AttachType.Chara;
        }

        /// <summary>
        /// 反序列化或加载完成回调，重新计算四元数及关节点哈希
        /// </summary>
        /// <param name="timelineControl">时间轴控制器实例</param>
        public override void OnLoad(LiveTimelineControl timelineControl)
        {
            base.OnLoad(timelineControl);
            UpdateParam();
        }

        /// <summary>
        /// 根据欧拉角更新四元数旋转与名称哈希
        /// </summary>
        public void UpdateParam()
        {
            OffsetRotation = Quaternion.Euler(OffsetRotate);

            if (_attachJointHash == 0 && !string.IsNullOrEmpty(_attachJointName))
            {
                _attachJointHash = FNVHash.Generate(_attachJointName);
            }

            if (_copyPositionJointHash == 0 && !string.IsNullOrEmpty(_copyPositionJointName))
            {
                _copyPositionJointHash = FNVHash.Generate(_copyPositionJointName);
            }
        }
    }

    /// <summary>
    /// Live 道具挂载关键帧数据列表模板封装
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyPropsAttachDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyPropsAttachData>
    {
        /// <summary>
        /// 初始化列表容器
        /// </summary>
        public LiveTimelineKeyPropsAttachDataList()
        {
        }
    }
}
