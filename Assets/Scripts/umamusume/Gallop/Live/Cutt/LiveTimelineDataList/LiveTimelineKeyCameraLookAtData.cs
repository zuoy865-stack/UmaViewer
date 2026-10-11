using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live时间轴摄像机注视点（LookAt）关键帧数据
    /// 包含注视目标类型（Direct绝对坐标或Character角色部位）、Bezier样条插值与道具依附参数
    /// </summary>
    [Serializable]
    public class LiveTimelineKeyCameraLookAtData : LiveTimelineKeyWithInterpolate
    {
        public override LiveTimelineKeyDataType dataType
        {
            get { return LiveTimelineKeyDataType.CameraLookAt; }
        }

        /// <summary>
        /// 注视类型：0=Direct直接坐标，1=Character跟踪角色
        /// </summary>
        public LiveCameraLookAtType lookAtType;

        /// <summary>
        /// 基础坐标或局部偏移值
        /// </summary>
        public Vector3 position;

        /// <summary>
        /// 目标角色站位标志（按位掩码）
        /// </summary>
        public LiveCharaPositionFlag lookAtCharaPos;

        /// <summary>
        /// 目标角色身体部位或预设高度类型
        /// </summary>
        public LiveCameraCharaParts lookAtCharaParts;

        /// <summary>
        /// 角色全局站位基础偏移（仅在ConstHeight模式下生效叠加）
        /// </summary>
        public Vector3 charaPos;

        /// <summary>
        /// 贝塞尔控制点数组
        /// </summary>
        public Vector3[] bezierPoints;

        /// <summary>
        /// 镜头追踪速度
        /// </summary>
        public float traceSpeed;

        public Vector3 rotation = Vector3.forward;
        public float eyeLength = 10f;
        public Vector3 offset = Vector3.zero;

        /// <summary>
        /// 关键帧起始帧记录的角色头部位置缓存
        /// </summary>
        public Vector3 CharaPositionAtStartFrame;

        /// <summary>
        /// 是否启用新贝塞尔计算方法
        /// </summary>
        public bool newBezierCalcMethod;

        /// <summary>
        /// 是否挂载至舞台道具
        /// </summary>
        public bool IsAttachedToProps;

        /// <summary>
        /// 道具索引
        /// </summary>
        public int PropsIndex;

        /// <summary>
        /// 道具挂载节点索引
        /// </summary>
        public int PropsAttachNodeIndex;

        public bool necessaryToUseNewBezierCalcMethod
        {
            get
            {
                if (!newBezierCalcMethod)
                {
                    return GetBezierPointCount() > 3;
                }
                return true;
            }
        }

        public bool HasBezier()
        {
            return bezierPoints != null && bezierPoints.Length != 0;
        }

        public int GetBezierPointCount()
        {
            if (!HasBezier())
            {
                return 0;
            }
            return bezierPoints.Length;
        }

        public Vector3 GetBezierPoint(int index, LiveTimelineControl timelineControl, Vector3 camPos)
        {
            Vector3 value = GetValue(timelineControl);
            if (HasBezier() && index >= 0 && index < bezierPoints.Length)
            {
                return value + bezierPoints[index];
            }
            return value;
        }

        public void GetBezierPoints(LiveTimelineControl timelineControl, Vector3 camPos, Vector3[] outPoints, int startIndex)
        {
            if (!HasBezier() || outPoints == null || startIndex < 0 || startIndex >= outPoints.Length)
            {
                return;
            }

            int num = Mathf.Min(outPoints.Length - startIndex, bezierPoints.Length);
            Vector3 value = GetValue(timelineControl);
            for (int i = 0; i < num; i++)
            {
                outPoints[startIndex + i] = value + bezierPoints[i];
            }
        }

        /// <summary>
        /// 获取针对当前角色身高的缩放比例
        /// </summary>
        public float GetHeightRate(LiveTimelineControl timelineControl)
        {
            if (lookAtType == LiveCameraLookAtType.Character)
            {
                return timelineControl.GetHeightRateWithCharacters(lookAtCharaPos);
            }
            return 1f;
        }

        /// <summary>
        /// 获取层级偏移（因角色身高差异产生的微调）
        /// </summary>
        public bool GetLayerOffset(LiveTimelineControl timelineControl, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (lookAtType != LiveCameraLookAtType.Character)
            {
                return false;
            }

            return LiveTimelineControl.GetCameraLayerOffset(
                timelineControl,
                lookAtCharaPos,
                timelineControl.CameraLayerOffsetMin,
                timelineControl.CameraLayerOffsetDiff,
                out offset);
        }

        public void GetBezierPoints(LiveTimelineControl timelineControl, Vector3[] outPoints, int startIndex)
        {
            if (bezierPoints == null || bezierPoints.Length == 0)
            {
                return;
            }

            if (outPoints == null)
            {
                throw new NullReferenceException();
            }

            int count = Mathf.Min(outPoints.Length, bezierPoints.Length);
            Vector3 value = GetValue(timelineControl);
            for (int i = 0; i < count; i++)
            {
                outPoints[startIndex + i] = value + bezierPoints[i];
            }
        }

        /// <summary>
        /// 官方Cutt算法：解算目标角色世界坐标
        /// 区分固定高度与动态骨骼，严禁在动态骨骼上重复累加charaPos
        /// </summary>
        public static Vector3 GetCharacterWorldPos(
            LiveTimelineControl timelineControl,
            LiveCharaPositionFlag posFlag,
            LiveCameraCharaParts charaParts,
            Vector3 charaPos,
            Vector3 offset,
            bool isAttachedToProps,
            int propsIndex,
            int propsAttachNodeIndex)
        {
            Transform transform;
            if (TryGetTransformAttachedToProps(isAttachedToProps, posFlag, propsIndex, propsAttachNodeIndex, out transform))
            {
                if (transform == null)
                {
                    return offset;
                }
                return transform.position + transform.rotation * offset;
            }

            switch ((int)charaParts)
            {
                case 11: // ConstFaceHeight: 纯垂直高度 + 站位charaPos + 局部offset
                    return timelineControl.GetPositionWithCharacters(posFlag, (LiveCameraCharaParts)17)
                        + charaPos
                        + offset;

                case 12: // ConstChestHeight: 纯垂直高度 + 站位charaPos + 局部offset
                    return timelineControl.GetPositionWithCharacters(posFlag, (LiveCameraCharaParts)18)
                        + charaPos
                        + offset;

                case 13: // ConstWaistHeight: 纯垂直高度 + 站位charaPos + 局部offset
                    return timelineControl.GetPositionWithCharacters(posFlag, (LiveCameraCharaParts)19)
                        + charaPos
                        + offset;

                case 14: // ConstFootHeight: 站位charaPos + 局部offset
                    return charaPos + offset;

                default: // 真实骨骼世界坐标（自带世界位置），直接加局部offset，绝不叠加charaPos
                    return timelineControl.GetPositionWithCharacters(posFlag, charaParts)
                        + offset;
            }
        }

        public static bool TryGetTransformAttachedToProps(
            bool isAttachedToProps,
            LiveCharaPositionFlag posFlag,
            int propsIndex,
            int propsAttachNodeIndex,
            out Transform transform)
        {
            transform = null;
            if (!isAttachedToProps)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 计算当前注视点关键帧的世界坐标
        /// </summary>
        public Vector3 GetValue(LiveTimelineControl timelineControl)
        {
            Vector3 value = Vector3.zero;

            switch (lookAtType)
            {
                case LiveCameraLookAtType.Direct:
                    value = position;
                    break;

                case LiveCameraLookAtType.Character:
                    if (lookAtCharaParts == LiveCameraCharaParts.Max || lookAtCharaParts == LiveCameraCharaParts.StartFrameFace)
                    {
                        if (timelineControl == null)
                        {
                            throw new NullReferenceException();
                        }

                        if (frame != timelineControl.CurrentCameraLookAtKeyFrame)
                        {
                            CharaPositionAtStartFrame = timelineControl.GetPositionWithCharacters(
                                lookAtCharaPos,
                                LiveCameraCharaParts.Face);
                        }

                        value = CharaPositionAtStartFrame + position;
                    }
                    else
                    {
                        value = GetCharacterWorldPos(
                            timelineControl,
                            lookAtCharaPos,
                            lookAtCharaParts,
                            charaPos,
                            position,
                            IsAttachedToProps,
                            PropsIndex,
                            PropsAttachNodeIndex);
                    }

                    value = CuttVector3_Helper.Round(value);
                    break;
            }

            return value;
        }

        public Vector3 GetValue(LiveTimelineControl timelineControl, Vector3 camPos)
        {
            return GetValue(timelineControl);
        }

        public Vector3 GetBezierPoint(int index, LiveTimelineControl timelineControl)
        {
            return GetBezierPoint(index, timelineControl, Vector3.zero);
        }
    }
}