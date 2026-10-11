using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// LiveTimelineControl 分部类：Live 道具（Props）调度器与挂载姿态控制器
    /// 负责按时间轴帧驱动演出道具的显隐状态、色彩强度、动画剪辑播放进度以及依附于角色/父级道具的骨骼挂载变换矩阵
    /// </summary>
    public partial class LiveTimelineControl : MonoBehaviour
    {
        /// <summary>
        /// 道具属性与动画播放更新委托
        /// </summary>
        /// <param name="propId">道具编号 (Props ID)</param>
        /// <param name="rendererEnable">道具渲染器启用状态（显隐）</param>
        /// <param name="color">道具主色调（经线性插值）</param>
        /// <param name="colorPower">色彩强度倍率（经线性插值）</param>
        /// <param name="clip">绑定的动画剪辑资源 (AnimationClip)</param>
        /// <param name="animTime">动画播放时间偏移量（秒）</param>
        /// <param name="clipName">动画剪辑名称</param>
        public delegate void PropsUpdateDelegate(
            int propId,
            bool rendererEnable,
            Color color,
            float colorPower,
            AnimationClip clip,
            float animTime,
            string clipName,
            int settingFlags
        );

        /// <summary>
        /// 道具属性及动画刷新事件，当时间轴步进到有效道具关键帧时触发
        /// </summary>
        public event PropsUpdateDelegate OnUpdateProps;

        /// <summary>
        /// 道具挂载姿态更新委托
        /// </summary>
        /// <param name="propId">被挂载的道具编号</param>
        /// <param name="jointName">依附的目标骨骼关节点名称</param>
        /// <param name="offsetPos">局部位置偏移（经时间轴插值）</param>
        /// <param name="offsetRot">局部旋转四元数偏移（经球面球面插值）</param>
        /// <param name="offsetScale">局部缩放偏移（经时间轴插值）</param>
        /// <param name="attachType">挂载目标主体类型（角色骨骼或父级道具）</param>
        /// <param name="targetPropId">当挂载到父级道具时的目标道具编号</param>
        public delegate void PropsAttachUpdateDelegate(
            int propId,
            string jointName,
            Vector3 offsetPos,
            Quaternion offsetRot,
            Vector3 offsetScale,
            LiveTimelineKeyPropsAttachData.AttachType attachType,
            int targetPropId,
            string attachTargetPropNodeName,
            int settingFlags
        );

        /// <summary>
        /// 道具空间挂载姿态刷新事件，当时间轴步进到有效挂载关键帧时触发
        /// </summary>
        public event PropsAttachUpdateDelegate OnUpdatePropsAttach;

        /// <summary>
        /// 驱动当前工作表内的所有道具属性与动画播放进度
        /// 遍历 sheet.propsList，通过二分查找当前帧所在的相邻关键帧，计算颜色、强度线性插值与动画时间偏移并派发事件
        /// </summary>
        /// <param name="sheet">当前时间轴工作表实例</param>
        /// <param name="currentFrame">当前时间轴浮点帧</param>
        /// <param name="currentLiveTime">当前 Live 播放绝对时间（秒）</param>
        public void AlterUpdate_PropsControl(LiveTimelineWorkSheet sheet, float currentFrame, float currentLiveTime)
        {
            if (sheet == null || sheet.propsList == null || sheet.propsList.Count == 0)
            {
                return;
            }

            int count = sheet.propsList.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelinePropsData propData = sheet.propsList[i];
                if (propData == null || !propData.IsEnableVariation())
                {
                    continue;
                }

                LiveTimelineKeyPropsDataList keys = propData.keys;
                if (keys == null || keys.Count == 0)
                {
                    continue;
                }

                // 检查轨道属性与当前播放模式过滤条件
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }

                // 使用时间轴二分查找定位当前帧及下一帧关键帧
                FindTimelineKey(out LiveTimelineKey curKeyBase, out LiveTimelineKey nextKeyBase, keys, currentFrame);
                if (curKeyBase == null)
                {
                    continue;
                }

                LiveTimelineKeyPropsData curKey = curKeyBase as LiveTimelineKeyPropsData;
                if (curKey == null)
                {
                    continue;
                }

                LiveTimelineKeyPropsData nextKey = nextKeyBase as LiveTimelineKeyPropsData;

                // 计算显隐状态：布尔值取当前关键帧的设定
                bool rendererEnable = curKey.rendererEnable;

                // 计算颜色与色彩强度的线性插值
                Color color;
                float colorPower;
                if (nextKey != null && nextKey.frame > curKey.frame)
                {
                    float frameDiff = nextKey.frame - curKey.frame;
                    float t = Mathf.Clamp01((currentFrame - curKey.frame) / frameDiff);
                    color = Color.Lerp(curKey.color, nextKey.color, t);
                    colorPower = Mathf.Lerp(curKey.colorPower, nextKey.colorPower, t);
                }
                else
                {
                    color = curKey.color;
                    colorPower = curKey.colorPower;
                }

                // 计算动画剪辑资源与当前播放进度时间
                AnimationClip clip = curKey.AnimationClip;
                string clipName = !string.IsNullOrEmpty(curKey.AnimationClipName)
                    ? curKey.AnimationClipName
                    : (clip != null ? clip.name : string.Empty);

                float animTime = 0f;
                if (curKey.IsApplyAnimation)
                {
                    // 相对当前关键帧起始点经过的时间（秒）
                    float elapsedSec = (currentFrame - curKey.frame) * kFrameToSec;
                    // 叠加关键帧配置的起始播放时间以及起始帧偏置
                    animTime = curKey.StartAnimationTime + elapsedSec + (curKey.AnimationHeadFrame * kFrameToSec);
                    if (animTime < 0f)
                    {
                        animTime = 0f;
                    }
                }

                // 派发道具状态更新事件
                OnUpdateProps?.Invoke(
                    curKey.propsID,
                    rendererEnable,
                    color,
                    colorPower,
                    clip,
                    animTime,
                    clipName,
                    curKey.settingFlags
                );
            }
        }

        /// <summary>
        /// 驱动当前工作表内的所有道具属性与动画播放进度（整数帧兼容重载）
        /// </summary>
        public void AlterUpdate_PropsControl(LiveTimelineWorkSheet sheet, int currentFrame, float currentLiveTime)
        {
            AlterUpdate_PropsControl(sheet, (float)currentFrame, currentLiveTime);
        }

        /// <summary>
        /// 驱动当前工作表内的所有道具挂载姿态变换
        /// 遍历 sheet.propsAttachList，计算当前帧相对关节点的局部位置、旋转及缩放矩阵插值并派发事件
        /// </summary>
        /// <param name="sheet">当前时间轴工作表实例</param>
        /// <param name="currentFrame">当前时间轴浮点帧</param>
        public void AlterUpdate_PropsAttachControl(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet == null || sheet.propsAttachList == null || sheet.propsAttachList.Count == 0)
            {
                return;
            }

            int count = sheet.propsAttachList.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelinePropsAttachData attachData = sheet.propsAttachList[i];
                if (attachData == null || !attachData.IsEnableVariation())
                {
                    continue;
                }

                LiveTimelineKeyPropsAttachDataList keys = attachData.keys;
                if (keys == null || keys.Count == 0)
                {
                    continue;
                }

                // 检查轨道属性与当前播放模式过滤条件
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }

                // 查找当前帧的关键帧
                FindTimelineKey(out LiveTimelineKey curKeyBase, out LiveTimelineKey nextKeyBase, keys, currentFrame);
                if (curKeyBase == null)
                {
                    continue;
                }

                LiveTimelineKeyPropsAttachData curKey = curKeyBase as LiveTimelineKeyPropsAttachData;
                if (curKey == null)
                {
                    continue;
                }

                LiveTimelineKeyPropsAttachData nextKey = nextKeyBase as LiveTimelineKeyPropsAttachData;

                int propId = curKey._propsId;
                string jointName = curKey._attachJointName;
                LiveTimelineKeyPropsAttachData.AttachType attachType = curKey._attachType;
                int targetPropId = curKey._attachPropId;

                Vector3 offsetPos = curKey._offsetPosition;
                Quaternion offsetRot = curKey.OffsetRotation;
                Vector3 offsetScale = curKey.OffsetScale;

                // 若下一关键帧为有效插值帧，则按照曲线/缓动方式执行空间变换数学插值
                if (nextKey != null && nextKey.IsInterpolateKey())
                {
                    float t = CalculateInterpolationValue(curKey, nextKey, currentFrame);
                    offsetPos = Vector3.LerpUnclamped(curKey._offsetPosition, nextKey._offsetPosition, t);
                    offsetRot = Quaternion.SlerpUnclamped(curKey.OffsetRotation, nextKey.OffsetRotation, t);
                    offsetScale = Vector3.LerpUnclamped(curKey.OffsetScale, nextKey.OffsetScale, t);
                }

                // 派发道具挂载空间姿态更新事件
                OnUpdatePropsAttach?.Invoke(
                    propId,
                    jointName,
                    offsetPos,
                    offsetRot,
                    offsetScale,
                    attachType,
                    targetPropId,
                    curKey._attachTargetPropNodeName,
                    curKey._settingFlags
                );
            }
        }

        /// <summary>
        /// 驱动当前工作表内的所有道具挂载姿态变换（整数帧兼容重载）
        /// </summary>
        public void AlterUpdate_PropsAttachControl(LiveTimelineWorkSheet sheet, int currentFrame)
        {
            AlterUpdate_PropsAttachControl(sheet, (float)currentFrame);
        }
    }
}
