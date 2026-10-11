using System;
using System.Collections.Generic;
using UnityEngine;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// LiveTimelineControl 分部类：负责关键帧插值算法、角色站位偏移变换、舞台物体/骨骼变换、通用动画轨道以及 Bloom 特效
    /// </summary>
    public partial class LiveTimelineControl : MonoBehaviour
    {
        private void AlterUpdate_AnimationControl(LiveTimelineWorkSheet sheet, int currentFrame)
        {
        if (OnUpdateAnimation == null)
            return;

        if (sheet == null || sheet.animationList == null)
            return;

        int count = sheet.animationList.Count;
        for (int i = 0; i < count; i++)
        {
            LiveTimelineAnimationData animData = sheet.animationList[i];
            if (animData == null)
                continue;

            LiveTimelineKeyAnimationDataList keys = animData.keys;
            if (keys == null)
                continue;

            if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable))
                continue;

            if (!keys.EnablePlayModeTimeline(_playMode))
                continue;

            LiveTimelineKey currentBaseKey;
            LiveTimelineKey nextBaseKey;
            FindTimelineKey(out currentBaseKey, out nextBaseKey, keys, currentFrame);

            LiveTimelineKeyAnimationData currentKey = currentBaseKey as LiveTimelineKeyAnimationData;
            if (currentKey == null)
                continue;

            LiveTimelineKeyAnimationData nextKey = nextBaseKey as LiveTimelineKeyAnimationData;

            if (nextKey != null && nextKey.IsInterpolateKey())
            {
                CalculateInterpolationValue(currentKey, nextKey, currentFrame);
            }

            AnimationUpdateInfo updateInfo = default;
            updateInfo.progressTime = (currentFrame - currentKey.frame) / 60.0f;
            updateInfo.data = animData;
            updateInfo.animationId = currentKey.animationID;
            updateInfo.wrapMode = currentKey.wrapMode;
            updateInfo.speed = currentKey.speed;
            updateInfo.offsetTime = currentKey.offsetTime;

            OnUpdateAnimation(ref updateInfo);
        }
}


        public static void FindTimelineKey(out LiveTimelineKey curKey, out LiveTimelineKey nextKey, ILiveTimelineKeyDataList keys, float curFrame)
        {
            FindKeyResult findKeyResult = keys.FindKeyCached(curFrame, availableFindKeyCache);
            curKey = findKeyResult.key;

            if (curKey != null)
            {
                nextKey = keys.At(findKeyResult.index + 1);
            }
            else
            {
                nextKey = null;
            }
        }

        private void AlterLateUpdate_FormationOffset(float liveTime)
        {

            var formationList = data.worksheetList[0].formationOffsetSet.Init();

            for (int i = 0; i < Director.instance.characterCount; i++)
            {
                LiveTimelineKeyIndex curKey = AlterUpdate_Key(formationList[i], liveTime);

                if (curKey != null && curKey.index != -1)
                {
                    LateUpdateFormationOffset_Transform(i, curKey, liveTime);
                }
            }
        }

        private static float LinearInterpolateKeyframes(LiveTimelineKey from, LiveTimelineKey to, float curFrame)
        {
            int num = to.frame - from.frame;
            return Mathf.Clamp01((curFrame - (float)from.frame) / (float)num);
        }

        private static float CurveInterpolateKeyframes(LiveTimelineKey from, LiveTimelineKey to, float curFrame)
        {
            LiveTimelineKeyWithInterpolate liveTimelineKeyWithInterpolate = from as LiveTimelineKeyWithInterpolate;
            LiveTimelineKeyWithInterpolate liveTimelineKeyWithInterpolate2 = to as LiveTimelineKeyWithInterpolate;
            if (liveTimelineKeyWithInterpolate == null)
            {
                return 0f;
            }
            if (liveTimelineKeyWithInterpolate2 == null)
            {
                return 0f;
            }
            int num = to.frame - from.frame;
            float time = Mathf.Clamp01((curFrame - (float)from.frame) / (float)num);
            return liveTimelineKeyWithInterpolate2.curve.Evaluate(time);
        }

        private static float EaseInterpolateKeyframes(LiveTimelineKey from, LiveTimelineKey to, float curFrame)
        {
            LiveTimelineKeyWithInterpolate liveTimelineKeyWithInterpolate = from as LiveTimelineKeyWithInterpolate;
            LiveTimelineKeyWithInterpolate liveTimelineKeyWithInterpolate2 = to as LiveTimelineKeyWithInterpolate;
            if (liveTimelineKeyWithInterpolate == null)
            {
                return 0f;
            }
            if (liveTimelineKeyWithInterpolate2 == null)
            {
                return 0f;
            }
            int num = to.frame - from.frame;
            return LiveTimelineEasing.GetValue(liveTimelineKeyWithInterpolate2.easingType, curFrame - (float)from.frame, 0f, 1f, (float)num);
        }



        
        public void LateUpdateFormationOffset_Transform(int targetIndex, LiveTimelineKeyIndex curKeyIndex, float time)
        {
            bool ControlMode = UmaViewerUI.Instance != null && UmaViewerUI.Instance.isControlMode;

            LiveTimelineKeyFormationOffsetData curKey = curKeyIndex.key as LiveTimelineKeyFormationOffsetData;
            LiveTimelineKeyFormationOffsetData nextKey = curKeyIndex.nextKey as LiveTimelineKeyFormationOffsetData;

            var chara = Director.instance.CharaContainerScript[targetIndex];
            if (!chara) return;


            if (ControlMode)
            {
                if (chara.LiveVisible != curKey.visible)
                {
                    chara.Materials.ForEach(m =>
                    {
                        foreach (var key in m.Renderers.Keys)
                        {
                            if (key.gameObject.activeSelf != curKey.visible)
                                key.gameObject.SetActive(curKey.visible);
                        }
                    });
                    chara.LiveVisible = curKey.visible;
                }


                if (curKey.visible || IsRecordVMD)
                {
                    if (!string.IsNullOrEmpty(curKey.ParentObjectName))
                    {
                        var parent_transform = curKey.GetParentObjectTransform(this);
                        if (parent_transform)
                        {
                            if (chara.transform.parent != parent_transform)
                            {
                                chara.transform.SetParent(parent_transform);
                            }
                        }
                    }
                    else if (chara.transform.parent)
                    {
                        chara.transform.SetParent(null);
                    }

                    if (nextKey != null && nextKey.interpolateType != LiveCameraInterpolateType.None)
                    {
                        float ratio = CalculateInterpolationValue(curKey, nextKey, time * 60);
                        chara.transform.localPosition = Vector3.Lerp(curKey.Position, nextKey.Position, ratio);
                        var x = chara.transform.eulerAngles.x;
                        var z = chara.transform.eulerAngles.z;
                        chara.transform.eulerAngles = new Vector3(x, Mathf.Lerp(curKey.RotationY, nextKey.RotationY, ratio), z);

                        var local_x = chara.Position.localEulerAngles.x;
                        var local_z = chara.Position.localEulerAngles.z;
                        chara.Position.localEulerAngles = new Vector3(local_x, Mathf.Lerp(curKey.LocalRotationY, nextKey.LocalRotationY, ratio), local_z);
                    }
                    else
                    {
                        chara.transform.localPosition = curKey.Position;
                        var x = chara.transform.eulerAngles.x;
                        var z = chara.transform.eulerAngles.z;
                        chara.transform.eulerAngles = new Vector3(x, curKey.RotationY, z);

                        var local_x = chara.Position.localEulerAngles.x;
                        var local_z = chara.Position.localEulerAngles.z;
                        chara.Position.localEulerAngles = new Vector3(local_x, curKey.LocalRotationY, local_z);
                    }
                }
            }
            else
            {
                if (curKey.visible || IsRecordVMD)
                {
                    if (!string.IsNullOrEmpty(curKey.ParentObjectName))
                    {
                        var parent_transform = curKey.GetParentObjectTransform(this);
                        if (parent_transform && chara.transform.parent != parent_transform)
                        {
                            chara.transform.SetParent(parent_transform);
                        }
                    }
                    else if (chara.transform.parent)
                    {
                        chara.transform.SetParent(null);
                    }
                }
            }
        }



        public static void FindTimelineKeyCurrent(out LiveTimelineKey curKey, ILiveTimelineKeyDataList keys, float curFrame)
        {
            LiveTimelineKey nextKey;
            FindTimelineKey(out curKey, out nextKey, keys, curFrame);

        }

        public static void FindTimelineKeyCurrent(out LiveTimelineKeyIndex curKey, ILiveTimelineKeyDataList keys, float curTime)
        {
            curKey = keys.FindCurrentKey(curTime);
        }

        public static void UpdateTimelineKeyCurrent(out LiveTimelineKeyIndex curKey, ILiveTimelineKeyDataList keys, float curTime)
        {
            curKey = keys.UpdateCurrentKey(curTime);
        }

        public static LiveTimelineKeyIndex AlterUpdate_Key(ILiveTimelineKeyDataList keys, float curTime)
        {
            LiveTimelineKeyIndex curKey = keys.TimeKeyIndex;

            if (curKey.index == -1 || Director.instance.sliderControl.is_Touched)
            {
                FindTimelineKeyCurrent(out curKey, keys, curTime);
            }
            else
            {
                UpdateTimelineKeyCurrent(out curKey, keys, curTime);
            }

            return curKey;
        }


        private void AlterUpdate_TransformControl(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            TransformUpdateInfoDelegate handler = OnUpdateTransform;
            if (handler == null || sheet == null || sheet.transformList == null)
                return;

            int count = sheet.transformList.Count;
            for (int i = 0; i < count; i++)
            {
                var transformEntry = sheet.transformList[i];
                if (transformEntry == null || transformEntry.keys == null)
                    continue;

                var keys = transformEntry.keys;
                if (keys.Count <= 0 ||
                    keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) ||
                    !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);

                LiveTimelineKeyTransformData transformData = curKey as LiveTimelineKeyTransformData;
                LiveTimelineKeyTransformData transformData2 = nextKey as LiveTimelineKeyTransformData;
                if (transformData == null)
                    continue;

                TransformUpdateInfo updateInfo = default;
                updateInfo.data = transformEntry;

                if (transformData2 != null && transformData2.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(transformData, transformData2, currentFrame);
                    updateInfo.updateData.position = Vector3.Lerp(transformData.position, transformData2.position, t);
                    updateInfo.updateData.rotation = Quaternion.Lerp(
                        Quaternion.Euler(transformData.rotate),
                        Quaternion.Euler(transformData2.rotate),
                        t);
                    updateInfo.updateData.scale = Vector3.Lerp(transformData.scale, transformData2.scale, t);
                }
                else
                {
                    updateInfo.updateData.position = transformData.position;
                    updateInfo.updateData.rotation = Quaternion.Euler(transformData.rotate);
                    updateInfo.updateData.scale = transformData.scale;
                }

                handler(ref updateInfo);
            }
        }

        private void AlterUpdate_ObjectControl(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            ObjectUpdateInfoDelegate handler = OnUpdateObject;
            if (handler == null || sheet == null || sheet.objectList == null)
                return;

            int count = sheet.objectList.Count;
            for (int i = 0; i < count; i++)
            {
                var objectEntry = sheet.objectList[i];
                if (objectEntry == null || objectEntry.keys == null || string.IsNullOrEmpty(objectEntry.name))
                    continue;

                var keys = objectEntry.keys;
                if (keys.Count <= 0 ||
                    keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) ||
                    !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }

                if (StageObjectMap == null || !StageObjectMap.ContainsKey(objectEntry.name))
                    continue;

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);

                LiveTimelineKeyObjectData objectData = curKey as LiveTimelineKeyObjectData;
                LiveTimelineKeyObjectData objectData2 = nextKey as LiveTimelineKeyObjectData;
                if (objectData == null)
                    continue;

                ObjectUpdateInfo updateInfo = default;

                if (objectData2 != null && objectData2.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(objectData, objectData2, currentFrame);
                    updateInfo.updateData.position = Vector3.Lerp(objectData.position, objectData2.position, t);
                    updateInfo.updateData.rotation = Quaternion.Lerp(
                        Quaternion.Euler(objectData.rotate),
                        Quaternion.Euler(objectData2.rotate),
                        t);
                    updateInfo.updateData.scale = Vector3.Lerp(objectData.scale, objectData2.scale, t);
                }
                else
                {
                    updateInfo.updateData.position = objectData.position;
                    updateInfo.updateData.rotation = Quaternion.Euler(objectData.rotate);
                    updateInfo.updateData.scale = objectData.scale;
                }

                updateInfo.data = objectEntry;
                updateInfo.renderEnable = objectData.renderEnable;
                updateInfo.AttachTarget = objectData.AttachTarget;
                updateInfo.CharacterPosition = objectData.CharacterPosition;
                updateInfo.MultiCameraIndex = objectData.MultiCameraIndex;
                updateInfo.OffsetType = objectData.OffsetValueType;
                updateInfo.LayerType = objectData.LayerTypeValue;
                updateInfo.IsLayerTypeRecursively = objectData.IsLayerTypeRecursively;

                handler(ref updateInfo);
            }
        }
        private void AlterUpdate_HdrBloom(LiveTimelineWorkSheet sheet, int currentFrame)
        {
            if (OnUpdateHdrBloom == null)
                return;

            if (sheet == null || sheet.hdrBloomList == null)
                return;

            int count = sheet.hdrBloomList.Count;

            for (int i = 0; i < count; i++)
            {
                LiveTimelineHdrBloomData timelineData =
                    sheet.hdrBloomList[i];

                if (timelineData == null ||
                    timelineData.keys == null)
                {
                    continue;
                }

                LiveTimelineKeyHdrBloomDataList keys =
                    timelineData.keys;

                if (keys.HasAttribute(
                    LiveTimelineKeyDataListAttr.Disable))
                {
                    continue;
                }

                FindTimelineKey(
                    out LiveTimelineKey currentBaseKey,
                    out LiveTimelineKey nextBaseKey,
                    keys,
                    currentFrame);

                LiveTimelineKeyHdrBloomData currentKey =
                    currentBaseKey as LiveTimelineKeyHdrBloomData;

                LiveTimelineKeyHdrBloomData nextKey =
                    nextBaseKey as LiveTimelineKeyHdrBloomData;

                if (currentKey == null)
                    continue;

                HdrBloomUpdateInfo updateInfo = default;

                updateInfo.data = timelineData;

                if (nextKey != null &&
                    nextKey.IsInterpolateKey())
                {
                    float t = CalculateInterpolationValue(
                        currentKey,
                        nextKey,
                        currentFrame);

                    updateInfo.intensity = Mathf.Lerp(
                        currentKey.intensity,
                        nextKey.intensity,
                        t);

                    updateInfo.blurSpread = Mathf.Lerp( currentKey.blurSpread, nextKey.blurSpread, t);
                }
                else
                {
                    updateInfo.intensity = currentKey.intensity;

                    updateInfo.blurSpread = currentKey.blurSpread;
                }

                updateInfo.enable = currentKey.enable;

                OnUpdateHdrBloom(ref updateInfo);
            }
        }

        private void SetupBloomDiffusionUpdateInfo(ref PostEffectUpdateInfo_BloomDiffusion updateInfo,LiveTimelineKeyPostEffectBloomDiffusionData currentKey, LiveTimelineKeyPostEffectBloomDiffusionData nextKey, int currentFrame)
        {
            if (currentKey == null)
                return;

            updateInfo.IsEnabledBloom = currentKey.IsEnabledBloom;
            updateInfo.IsEnabledDiffusion = currentKey.IsEnabledDiffusion;
            updateInfo.diffusionBlurSize = currentKey.diffusionBlurSize;

            updateInfo.diffusionBright =
                currentKey.diffusionBright;

            updateInfo.diffusionThreshold =
                currentKey.diffusionThreshold;

            updateInfo.diffusionSaturation =
                currentKey.diffusionSaturation;

            updateInfo.diffusionContrast =
                currentKey.diffusionContrast;

            if (nextKey != null &&
                nextKey.IsInterpolateKey())
            {
                float t = CalculateInterpolationValue(
                    currentKey,
                    nextKey,
                    currentFrame);

                updateInfo.bloomDofWeight = Mathf.Lerp(
                    currentKey.bloomDofWeight,
                    nextKey.bloomDofWeight,
                    t);

                updateInfo.threshold = Mathf.Lerp(
                    currentKey.threshold,
                    nextKey.threshold,
                    t);

                updateInfo.intensity = Mathf.Lerp(
                    currentKey.intensity,
                    nextKey.intensity,
                    t);

                updateInfo.BloomBlurSize = Mathf.Lerp(
                    currentKey.BloomBlurSize,
                    nextKey.BloomBlurSize,
                    t);

                updateInfo.BloomBlendMode =
                    currentKey.BloomBlendMode;

                if (currentKey.IsEnabledDiffusion &&
                    nextKey.IsEnabledDiffusion)
                {
                    updateInfo.diffusionBlurSize = Mathf.Lerp(
                        currentKey.diffusionBlurSize,
                        nextKey.diffusionBlurSize,
                        t);

                    updateInfo.diffusionBright = Mathf.Lerp(
                        currentKey.diffusionBright,
                        nextKey.diffusionBright,
                        t);

                    updateInfo.diffusionThreshold = Mathf.Lerp(
                        currentKey.diffusionThreshold,
                        nextKey.diffusionThreshold,
                        t);

                    updateInfo.diffusionSaturation = Mathf.Lerp(
                        currentKey.diffusionSaturation,
                        nextKey.diffusionSaturation,
                        t);

                    updateInfo.diffusionContrast = Mathf.Lerp(
                        currentKey.diffusionContrast,
                        nextKey.diffusionContrast,
                        t);
                }
            }
            else
            {
                updateInfo.bloomDofWeight =
                    currentKey.bloomDofWeight;

                updateInfo.threshold =
                    currentKey.threshold;

                updateInfo.intensity =
                    currentKey.intensity;

                updateInfo.BloomBlurSize =
                    currentKey.BloomBlurSize;

                updateInfo.BloomBlendMode =
                    currentKey.BloomBlendMode;
            }
        }

        private void AlterUpdate_PostEffect_BloomDiffusion( LiveTimelineWorkSheet sheet, int currentFrame)
        {
            if (sheet == null)
                return;

            LiveTimelineKeyPostEffectBloomDiffusionDataList keys =
                sheet.postEffectBloomDiffusionKeys;

            if (keys == null)
                return;

            if (keys.HasAttribute(
                LiveTimelineKeyDataListAttr.Disable))
            {
                return;
            }

            if (!keys.EnablePlayModeTimeline(_playMode))
                return;

            if (OnUpdatePostEffect_BloomDiffusion == null)
                return;

            FindTimelineKey(
                out LiveTimelineKey currentBaseKey,
                out LiveTimelineKey nextBaseKey,
                keys,
                currentFrame);

            LiveTimelineKeyPostEffectBloomDiffusionData currentKey =
                currentBaseKey
                    as LiveTimelineKeyPostEffectBloomDiffusionData;

            LiveTimelineKeyPostEffectBloomDiffusionData nextKey =
                nextBaseKey
                    as LiveTimelineKeyPostEffectBloomDiffusionData;

            if (currentKey == null)
                return;

            PostEffectUpdateInfo_BloomDiffusion updateInfo =
                default;

            SetupBloomDiffusionUpdateInfo( ref updateInfo, currentKey, nextKey, currentFrame);

            OnUpdatePostEffect_BloomDiffusion(updateInfo);
        }

        private static float LerpWithoutClamp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static Vector2 LerpWithoutClamp(Vector2 a, Vector2 b, float t)
        {
            return new Vector2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        }

        private static Vector3 LerpWithoutClamp(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        }

        private static Vector4 LerpWithoutClamp(Vector4 a, Vector4 b, float t)
        {
            return new Vector4(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);
        }

        private static Color LerpWithoutClamp(Color a, Color b, float t)
        {
            return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        }


    }
}
