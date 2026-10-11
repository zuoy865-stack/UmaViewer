using System;
using System.Collections.Generic;
using UnityEngine;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// LiveTimelineControl 相机控制分部类：
    /// 负责时间轴镜头切换、机位定位与注视点计算、FOV/Roll控制、角色身体部位世界坐标解算以及多机位支持
    /// 算法严格对齐官方 Cutt 算法，消除坐标重复累加导致的镜头严重偏移
    /// </summary>
    public partial class LiveTimelineControl : MonoBehaviour
    {
        public Quaternion GetMultiCameraWorldRotation(int index)
        {
            if (_multiCameraCache == null || _multiCameraCache.Length <= index)
            {
                return Quaternion.identity;
            }
            return _multiCameraCache[index].cacheTransform.rotation;
        }

        public Vector3 GetMultiCameraWorldPosition(int index)
        {
            if (_multiCameraCache == null || _multiCameraCache.Length <= index)
            {
                return Vector3.zero;
            }
            return _multiCameraCache[index].cacheTransform.position;
        }

        public bool ExistsMultiCamera(int index)
        {
            if (_multiCameraCache != null && index < _multiCameraCache.Length)
            {
                return _multiCameraCache[index] != null;
            }
            return false;
        }

        public float GetCharacterHeight(LiveCharaPosition position)
        {
            int index = (int)position;
            if (index >= 0 && index < liveCharactorLocators.Length && liveCharactorLocators[index] != null)
            {
                return liveCharactorLocators[index].liveCharaHeightValue;
            }
            return 160f;
        }

        /// <summary>
        /// 官方标准实现：根据角色站位掩码和身体部位获取对应的目标世界坐标
        /// 绝不在此方法中额外叠加任何 charaPos 或 layerOffset，防止多重坐标叠加
        /// </summary>
        public Vector3 GetPositionWithCharacters(LiveCharaPositionFlag posFlags, LiveCameraCharaParts parts)
        {
            Vector3 retPos = Vector3.zero;
            if (posFlags == 0)
            {
                return liveStageCenterPos;
            }

            int num = 0;
            switch (parts)
            {
                case LiveCameraCharaParts.Face:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaHeadPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.Waist:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaWaistPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.LeftHandWrist:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaLeftHandWristPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.RightHandAttach:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaRightHandAttachPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.Chest:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaChestPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.Foot:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaFootPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.ConstFaceHeight:
                case LiveCameraCharaParts.InitFaceHeight:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaConstHeightHeadPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.ConstWaistHeight:
                case LiveCameraCharaParts.InitWaistHeight:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaConstHeightWaistPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.ConstChestHeight:
                case LiveCameraCharaParts.InitChestHeight:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaConstHeightChestPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.RightHandWrist:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaRightHandWristPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.LeftHandAttach:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaLeftHandAttachPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.Position:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.PositionWithoutOffset:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaPosition - liveCharactorLocators[i].liveCharaFormationHeightRateOffset;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.InitialHeightFace:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaInitialHeightHeadPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.InitialHeightChest:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaInitialHeightChestPosition;
                            num++;
                        }
                    }
                    break;

                case LiveCameraCharaParts.InitialHeightWaist:
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                        {
                            retPos += liveCharactorLocators[i].liveCharaInitialHeightWaistPosition;
                            num++;
                        }
                    }
                    break;
            }

            if (num > 1)
            {
                retPos /= (float)num;
            }
            else if (num == 0)
            {
                // 防御保护：当时间轴请求的角色尚未生成或定位器为空时（如高序号伴舞角色），严禁返回 Vector3.zero 导致镜头塌陷至原点仰视
                // 1. 尝试从 Director 舞台站位节点 charaObjs 获取世界坐标并叠加部位高度
                if (Director.instance != null && Director.instance.charaObjs != null)
                {
                    float heightOffset = 0.8f;
                    switch (parts)
                    {
                        case LiveCameraCharaParts.Face:
                        case LiveCameraCharaParts.ConstFaceHeight:
                        case LiveCameraCharaParts.InitFaceHeight:
                        case LiveCameraCharaParts.InitialHeightFace:
                            heightOffset = 1.4f;
                            break;
                        case LiveCameraCharaParts.Chest:
                        case LiveCameraCharaParts.ConstChestHeight:
                        case LiveCameraCharaParts.InitChestHeight:
                        case LiveCameraCharaParts.InitialHeightChest:
                            heightOffset = 1.1f;
                            break;
                        case LiveCameraCharaParts.Waist:
                        case LiveCameraCharaParts.ConstWaistHeight:
                        case LiveCameraCharaParts.InitWaistHeight:
                        case LiveCameraCharaParts.InitialHeightWaist:
                            heightOffset = 0.8f;
                            break;
                        case LiveCameraCharaParts.Foot:
                            heightOffset = 0.05f;
                            break;
                        default:
                            heightOffset = 0.8f;
                            break;
                    }

                    int standCount = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        if (posFlags.hasFlag(i) && i < Director.instance.charaObjs.Count && Director.instance.charaObjs[i] != null)
                        {
                            retPos += Director.instance.charaObjs[i].position + new Vector3(0f, heightOffset, 0f);
                            standCount++;
                        }
                    }

                    if (standCount > 0)
                    {
                        retPos /= (float)standCount;
                        return retPos;
                    }
                }

                // 2. 尝试从已实例化的其他角色定位器中寻找有效位置
                for (int i = 0; i < liveCharactorLocators.Length; i++)
                {
                    if (liveCharactorLocators[i] != null)
                    {
                        return liveCharactorLocators[i].liveCharaPosition;
                    }
                }

                // 3. 终极兜底：舞台中心点
                return liveStageCenterPos;
            }

            return retPos;
        }

        /// <summary>
        /// 兼容旧版调用的重载
        /// </summary>
        public Vector3 GetPositionWithCharacters(LiveCharaPositionFlag posFlags, LiveCameraCharaParts parts, Vector3 charaPos)
        {
            return GetPositionWithCharacters(posFlags, parts);
        }

        /// <summary>
        /// 兼容旧版调用的重载
        /// </summary>
        public Vector3 GetPositionWithCharacters(LiveCharaPositionFlag posFlags, LiveCameraCharaParts parts, Vector3 charaPos, Vector3 cameraOffset)
        {
            return GetPositionWithCharacters(posFlags, parts);
        }

        /// <summary>
        /// 获取目标角色的平均身高缩放比率
        /// </summary>
        public float GetHeightRateWithCharacters(LiveCharaPositionFlag posFlags)
        {
            float heightRate = 1f;
            if ((int)posFlags > 0)
            {
                int count = 0;
                heightRate = 0f;
                for (int i = 0; i < 20; i++)
                {
                    if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length && liveCharactorLocators[i] != null)
                    {
                        heightRate += liveCharactorLocators[i].liveCharaHeightRatio;
                        count++;
                    }
                }
                if (count > 1)
                {
                    heightRate /= (float)count;
                }
            }
            return heightRate;
        }

        /// <summary>
        /// 静态入口：获取相机层级高度修正偏移
        /// </summary>
        public static bool GetCameraLayerOffset(LiveTimelineControl timelineControl, LiveCharaPositionFlag posFlag, Vector3 layerOffsetMin, Vector3 layerOffsetDiff, out Vector3 offset)
        {
            if (timelineControl == null)
            {
                offset = Vector3.zero;
                return false;
            }
            return timelineControl.GetCameraLayerOffset(posFlag, layerOffsetMin, layerOffsetDiff, out offset);
        }

        /// <summary>
        /// 实例方法：根据角色平均身高相对基准身高的差值计算相机局部空间层级偏移
        /// </summary>
        public bool GetCameraLayerOffset(LiveCharaPositionFlag posFlag, Vector3 layerOffsetMin, Vector3 layerOffsetDiff, out Vector3 offset)
        {
            offset = Vector3.zero;
            float height = GetHeightValueWithCharacters(posFlag);
            if (height <= 0f)
            {
                return false;
            }

            float rate = (height - BaseCharaHeightMin) / BaseCharaHeightDiff;
            offset = layerOffsetMin + layerOffsetDiff * rate;
            return true;
        }

        /// <summary>
        /// 计算指定站位角色的平均身高绝对值（单位：cm）
        /// </summary>
        public float GetHeightValueWithCharacters(LiveCharaPositionFlag posFlags)
        {
            float heightValue = 1f;
            if ((int)posFlags > 0)
            {
                int count = 0;
                heightValue = 0f;
                for (int i = 0; i < 20; i++)
                {
                    if (posFlags.hasFlag(i) && i < liveCharactorLocators.Length)
                    {
                        ILiveTimelineCharactorLocator locator = liveCharactorLocators[i];
                        if (locator != null)
                        {
                            heightValue += locator.liveCharaHeightValue;
                            count++;
                        }
                    }
                }
                if (count > 1)
                {
                    heightValue /= count;
                }
            }
            return heightValue;
        }

        public static float CalculateInterpolationValue(LiveTimelineKey curKey, LiveTimelineKeyWithInterpolate nextKey, float frame)
        {
            float result = 0f;
            switch (nextKey.interpolateType)
            {
                case LiveCameraInterpolateType.Linear:
                    result = LinearInterpolateKeyframes(curKey, nextKey, frame);
                    break;
                case LiveCameraInterpolateType.Curve:
                    result = CurveInterpolateKeyframes(curKey, nextKey, frame);
                    break;
                case LiveCameraInterpolateType.Ease:
                    result = EaseInterpolateKeyframes(curKey, nextKey, frame);
                    break;
            }
            return result;
        }

        public void SetTimelineCamera(Camera cam, int index)
        {
            if (index < _cameraArray.Length)
            {
                if (_cameraArray[index] == null)
                {
                    _cameraArray[index] = new CacheCamera(cam);
                }
                else
                {
                    _cameraArray[index].Set(cam);
                }
                LiveTimelineCamera liveTimelineCamera = cam.gameObject.GetComponent<LiveTimelineCamera>();
                if (liveTimelineCamera == null)
                {
                    liveTimelineCamera = cam.gameObject.AddComponent<LiveTimelineCamera>();
                }
                _cameraScriptArray[index] = liveTimelineCamera;
                if (liveTimelineCamera != null)
                {
                    liveTimelineCamera.AlterAwake();
                }
            }
        }

        /// <summary>
        /// 调度相机切换事件，通知主相机更新当前活动机位
        /// </summary>
        private void AlterUpdate_CameraSwitcher(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraSwitcherKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraSwitcherKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            LiveTimelineKey curKey = null;
            FindTimelineKeyCurrent(out curKey, sheet.cameraSwitcherKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraSwitcherData liveTimelineKeyCameraSwitcherData = curKey as LiveTimelineKeyCameraSwitcherData;
            if (liveTimelineKeyCameraSwitcherData == null)
            {
                return;
            }

            int cameraIndex = liveTimelineKeyCameraSwitcherData.cameraIndex;
            if (this.OnUpdateCameraSwitcher != null)
            {
                this.OnUpdateCameraSwitcher(cameraIndex);
            }
            else
            {
                if (cameraIndex >= cameraArray.Length)
                {
                    return;
                }
                for (int i = 0; i < cameraArray.Length; i++)
                {
                    if (cameraArray[i] == null) continue;
                    if (i == cameraIndex)
                    {
                        if (!cameraArray[i].camera.enabled)
                        {
                            cameraArray[i].camera.enabled = true;
                            if (cameraScriptArray[i] != null)
                            {
                                cameraScriptArray[i].enabled = true;
                            }
                        }
                    }
                    else if (cameraArray[i].camera.enabled)
                    {
                        cameraArray[i].camera.enabled = false;
                        if (cameraScriptArray[i] != null)
                        {
                            cameraScriptArray[i].enabled = false;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 更新相机位置，并提取当前关键帧的层级局部偏移
        /// </summary>
        private void AlterUpdate_CameraPos(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            _cameraPosLayerOffset = Vector3.zero;

            if (sheet.cameraPosKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraPosKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }

            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }

            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraPosKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData = curKey as LiveTimelineKeyCameraPositionData;
            if (liveTimelineKeyCameraPositionData == null)
            {
                return;
            }

            camera.camera.nearClipPlane = liveTimelineKeyCameraPositionData.nearClip;
            camera.camera.farClipPlane = liveTimelineKeyCameraPositionData.farClip;

            if (CalculateCameraPos(out var pos, sheet, curKey, nextKey, currentFrame))
            {
                camera.cacheTransform.position = pos;

                if (liveTimelineKeyCameraPositionData.GetLayerOffset(this, out Vector3 layerOffset))
                {
                    _cameraPosLayerOffset = layerOffset;
                }
                _currentCameraPosKeyFrame = liveTimelineKeyCameraPositionData.frame;
            }
        }

        private static Vector3 GetCameraPosValue(LiveTimelineKeyCameraPositionData keyData, LiveTimelineControl timelineControl, FindTimelineConfig config)
        {
            return keyData.GetValue(timelineControl);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, float currentFrame)
        {
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = null;
            config.nextKey = null;
            config.keyType = FindTimelineConfig.KeyType.CurrentFrame;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = null;
            config.extraCameraIndex = 0;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            return CalculateCameraPos(out pos, sheet, currentFrame, camera, ref config, ref fnGetCameraPosValue);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, LiveTimelineKey curKey, LiveTimelineKey nextKey, float currentFrame)
        {
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = curKey;
            config.nextKey = nextKey;
            config.keyType = FindTimelineConfig.KeyType.KeyDirect;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = null;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            config.extraCameraIndex = 0;
            return CalculateCameraPos(out pos, sheet, currentFrame, camera, ref config, ref fnGetCameraPosValue);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, float currentFrame, CacheCamera targetCamera, ref FindTimelineConfig config, ref Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> getFunc)
        {
            pos = Vector3.zero;
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            if (config.posKeys == null)
            {
                return false;
            }
            if (config.keyType == FindTimelineConfig.KeyType.CurrentFrame)
            {
                FindTimelineKey(out curKey, out nextKey, config.posKeys, currentFrame);
            }
            else
            {
                curKey = config.curKey;
                nextKey = config.nextKey;
            }
            if (curKey == null)
            {
                return false;
            }
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData = curKey as LiveTimelineKeyCameraPositionData;
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData2 = nextKey as LiveTimelineKeyCameraPositionData;
            if (liveTimelineKeyCameraPositionData2 != null && liveTimelineKeyCameraPositionData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraPositionData, liveTimelineKeyCameraPositionData2, currentFrame);
                int bezierPointCount = liveTimelineKeyCameraPositionData2.GetBezierPointCount();
                if (bezierPointCount == 0)
                {
                    pos = LerpWithoutClamp(liveTimelineKeyCameraPositionData.GetValue(this), liveTimelineKeyCameraPositionData2.GetValue(this), t);
                }
                else
                {
                    BezierCalcWork.cameraPos.Set(liveTimelineKeyCameraPositionData.GetValue(this), liveTimelineKeyCameraPositionData2.GetValue(this), bezierPointCount);
                    BezierCalcWork.cameraPos.UpdatePoints(liveTimelineKeyCameraPositionData2, this);
                    BezierCalcWork.cameraPos.Calc(bezierPointCount, t, out pos);
                }
            }
            else
            {
                pos = liveTimelineKeyCameraPositionData.GetValue(this);
            }

            if (_isNowAlterUpdate && liveTimelineKeyCameraPositionData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayEnable) && (_oldFrame >= liveTimelineKeyCameraPositionData.frame || currentFrame < liveTimelineKeyCameraPositionData.frame || liveTimelineKeyCameraPositionData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayInherit)))
            {
                if (targetCamera == null)
                {
                    return false;
                }
                float t2 = liveTimelineKeyCameraPositionData.traceSpeed * _deltaTimeRatio;
                pos = Vector3.Slerp(targetCamera.cacheTransform.position, pos, t2);
            }
            return true;
        }

        /// <summary>
        /// 更新相机注视点（LookAt），并提取当前关键帧的层级局部偏移
        /// </summary>
        private void AlterUpdate_CameraLookAt(LiveTimelineWorkSheet sheet, float currentFrame, ref Vector3 outLookAt)
        {
            _cameraLookAtLayerOffset = Vector3.zero;

            if (!sheet.cameraLookAtKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) && sheet.cameraLookAtKeys.EnablePlayModeTimeline(_playMode))
            {
                CacheCamera camera = GetCamera(sheet.targetCameraIndex);
                if (camera != null && CalculateCameraLookAt(out var lookAtPos, sheet, currentFrame))
                {
                    camera.cacheTransform.LookAt(lookAtPos, Vector3.up);
                    outLookAt = lookAtPos;

                    LiveTimelineKey curKey = null;
                    FindTimelineKeyCurrent(out curKey, sheet.cameraLookAtKeys, currentFrame);
                    if (curKey is LiveTimelineKeyCameraLookAtData currentLookAtKey)
                    {
                        if (currentLookAtKey.GetLayerOffset(this, out Vector3 layerOffset))
                        {
                            _cameraLookAtLayerOffset = layerOffset;
                        }
                        _currentCameraLookAtKeyFrame = curKey.frame;
                    }
                }
            }
        }

        private static Vector3 GetCameraLookAtValue(LiveTimelineKeyCameraLookAtData keyData, LiveTimelineControl timelineControl, Vector3 camPos, FindTimelineConfig config)
        {
            return keyData.GetValue(timelineControl);
        }

        public bool CalculateCameraLookAt(out Vector3 lookAtPos, LiveTimelineWorkSheet sheet, float currentFrame)
        {
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = null;
            config.nextKey = null;
            config.keyType = FindTimelineConfig.KeyType.CurrentFrame;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = sheet.cameraLookAtKeys;
            config.extraCameraIndex = 0;
            return CalculateCameraLookAt(out lookAtPos, sheet, currentFrame, camera, ref config, ref fnGetCameraLookAtValue, ref fnGetCameraPosValue);
        }

        private bool CalculateCameraLookAt(
            out Vector3 lookAtPos,
            LiveTimelineWorkSheet sheet,
            float currentFrame,
            CacheCamera targetCamera,
            ref FindTimelineConfig config,
            ref Func<LiveTimelineKeyCameraLookAtData, LiveTimelineControl, Vector3, FindTimelineConfig, Vector3> getLookAtValueFunc,
            ref Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> getPosValueFunc)
        {
            lookAtPos = Vector3.zero;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null || config.lookAtKeys == null || config.posKeys == null)
            {
                return false;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, config.lookAtKeys, currentFrame);
            if (curKey == null)
            {
                return false;
            }
            LiveTimelineKeyCameraLookAtData liveTimelineKeyCameraLookAtData = curKey as LiveTimelineKeyCameraLookAtData;
            LiveTimelineKeyCameraLookAtData liveTimelineKeyCameraLookAtData2 = nextKey as LiveTimelineKeyCameraLookAtData;
            if (liveTimelineKeyCameraLookAtData == null)
            {
                return false;
            }

            Vector3 position = camera.cacheTransform.position;

            if (liveTimelineKeyCameraLookAtData2 != null && liveTimelineKeyCameraLookAtData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraLookAtData, liveTimelineKeyCameraLookAtData2, currentFrame);
                Vector3 start = liveTimelineKeyCameraLookAtData.GetValue(this);
                Vector3 end = liveTimelineKeyCameraLookAtData2.GetValue(this);
                int bezierPointCount = liveTimelineKeyCameraLookAtData2.GetBezierPointCount();

                if (bezierPointCount == 0)
                {
                    lookAtPos = LerpWithoutClamp(start, end, t);
                }
                else if (liveTimelineKeyCameraLookAtData2.necessaryToUseNewBezierCalcMethod)
                {
                    BezierCalcWork.cameraLookAt.Set(start, end, bezierPointCount);
                    BezierCalcWork.cameraLookAt.UpdatePoints(liveTimelineKeyCameraLookAtData2, this, Vector3.zero);
                    BezierCalcWork.cameraLookAt.Calc(bezierPointCount, t, out lookAtPos);
                }
                else
                {
                    Vector3 cp = liveTimelineKeyCameraLookAtData2.GetBezierPoint(0, this);
                    Vector3 cp2 = liveTimelineKeyCameraLookAtData2.GetBezierPoint(1, this);
                    Vector3 cp3 = liveTimelineKeyCameraLookAtData2.GetBezierPoint(2, this);
                    switch (bezierPointCount)
                    {
                        default:
                            lookAtPos = LerpWithoutClamp(start, end, t);
                            break;
                        case 1:
                            BezierUtil.Calc(ref start, ref end, ref cp, t, out lookAtPos);
                            break;
                        case 2:
                            BezierUtil.Calc(ref start, ref end, ref cp, ref cp2, t, out lookAtPos);
                            break;
                        case 3:
                            BezierUtil.Calc(ref start, ref end, ref cp, ref cp2, ref cp3, t, out lookAtPos);
                            break;
                    }
                }
            }
            else
            {
                lookAtPos = liveTimelineKeyCameraLookAtData.GetValue(this);
            }

            if (_isNowAlterUpdate && liveTimelineKeyCameraLookAtData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayEnable) && (_oldFrame >= liveTimelineKeyCameraLookAtData.frame || currentFrame < liveTimelineKeyCameraLookAtData.frame || liveTimelineKeyCameraLookAtData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayInherit)))
            {
                Vector3 b = lookAtPos - camera.cacheTransform.position;
                float magnitude = b.magnitude;
                if (magnitude >= float.Epsilon)
                {
                    b /= magnitude;
                    float t2 = liveTimelineKeyCameraLookAtData.traceSpeed * _deltaTimeRatio;
                    lookAtPos = position + Vector3.Slerp(camera.cacheTransform.forward, b, t2) * magnitude;
                }
            }
            return true;
        }

        private void AlterUpdate_CameraFov(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraFovKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraFovKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraFovKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraFovData liveTimelineKeyCameraFovData = curKey as LiveTimelineKeyCameraFovData;
            LiveTimelineKeyCameraFovData liveTimelineKeyCameraFovData2 = nextKey as LiveTimelineKeyCameraFovData;
            float num = 80f;
            if (liveTimelineKeyCameraFovData2 != null && liveTimelineKeyCameraFovData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraFovData, liveTimelineKeyCameraFovData2, currentFrame);
                num = LerpWithoutClamp(liveTimelineKeyCameraFovData.fov, liveTimelineKeyCameraFovData2.fov, t);
            }
            else if (liveTimelineKeyCameraFovData.fovType == LiveCameraFovType.Direct)
            {
                num = liveTimelineKeyCameraFovData.fov;
            }
            if (_limitFovForWidth)
            {
                float num2 = (float)camera.camera.pixelWidth / (float)camera.camera.pixelHeight;
                if (num2 > _baseCameraAspectRatio)
                {
                    float num3 = num2 / _baseCameraAspectRatio;
                    num /= num3;
                }
            }
            camera.camera.fieldOfView = num;
        }

        private void AlterUpdate_CameraRoll(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraRollKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraRollKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraRollKeys, currentFrame);
            if (curKey != null)
            {
                LiveTimelineKeyCameraRollData liveTimelineKeyCameraRollData = curKey as LiveTimelineKeyCameraRollData;
                LiveTimelineKeyCameraRollData liveTimelineKeyCameraRollData2 = nextKey as LiveTimelineKeyCameraRollData;
                float num = 80f;
                if (liveTimelineKeyCameraRollData2 != null && liveTimelineKeyCameraRollData2.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(liveTimelineKeyCameraRollData, liveTimelineKeyCameraRollData2, currentFrame);
                    num = LerpWithoutClamp(liveTimelineKeyCameraRollData.degree, liveTimelineKeyCameraRollData2.degree, t);
                }
                else
                {
                    num = liveTimelineKeyCameraRollData.degree;
                }
                camera.cacheTransform.Rotate(0f, 0f, num);
            }
        }

        public void SetMultiCamera(MultiCamera[] multiCamera)
        {
            _multiCamera = multiCamera;
            if (multiCamera != null)
            {
                _multiCameraCache = new CacheCamera[multiCamera.Length];
                for (int i = 0; i < multiCamera.Length; i++)
                {
                    _multiCameraCache[i] = new CacheCamera(multiCamera[i].GetCamera());
                }
            }
        }
    }
}
