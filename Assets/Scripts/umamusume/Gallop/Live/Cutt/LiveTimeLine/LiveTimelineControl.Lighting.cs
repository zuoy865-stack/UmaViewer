using System;
using System.Collections.Generic;
using UnityEngine;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// LiveTimelineControl 分部类：负责舞台镜面反射、环境光照、背景颜色、舞台聚光灯、洗墙灯、激光与荧光棒
    /// </summary>
    public partial class LiveTimelineControl : MonoBehaviour
    {
        private void AlterUpdate_EnvironmentMirror(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (OnEnvironmentMirror == null) return;
            if (sheet == null || sheet.environmentDataLists == null) return;

            int count = sheet.environmentDataLists.Count;
            for (int i = 0; i < count; i++)
            {
                var envData = sheet.environmentDataLists[i];
                if (envData == null || envData.keys == null) continue;

                var keys = envData.keys;
                if (keys.Count <= 0) continue;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;
                if (!string.IsNullOrEmpty(envData.name) && !string.Equals(envData.name, "Environment", StringComparison.OrdinalIgnoreCase))
                    continue;

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                var cur = curKey as LiveTimelineKeyMirrorReflectionData;
                var next = nextKey as LiveTimelineKeyMirrorReflectionData;
                if (cur == null) continue;

                EnvironmentMirrorUpdateInfo info = default;
                info.isValid = cur.GetIsValidMirror();
                info.mirror = cur.GetMirrorEnabled();
                info.bgMirror = cur.GetBgMirrorEnabled();
                info.IsMirrorBg3d = cur.GetBg3dMirrorEnabled();
                info.mirrorReflectionRate = cur.GetMirrorReflectionRate();
                info.charaPositionFlag = cur.GetCharacterMirrorFlag();
                info.VisibleHeadFlag = cur.GetCharacterMirrorHeadFlag();
                info.IsToonMirror = cur.IsToonMirror;
                info.IsEnabledCharacterMirrorHead = cur.GetEnableCharacterMirrorHead();
                info.EnableCharacterMirrorExpandFaceBounds = cur.EnableCharacterMirrorExpandFaceBounds;
                info.CharacterMirrorExpandFaceBounds = cur.CharacterMirrorExpandFaceBounds;

                if (next != null && next.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(cur, next, currentFrame);
                    info.mirrorReflectionRate = LerpWithoutClamp(cur.GetMirrorReflectionRate(), next.GetMirrorReflectionRate(), t);
                }

                OnEnvironmentMirror.Invoke(ref info);
            }
        }

        private static int GenerateMirrorTimelineHash(string timelineName)
        {
            if (string.IsNullOrEmpty(timelineName))
                return 0;

            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;

                uint hash = offset;
                for (int i = 0; i < timelineName.Length; i++)
                {
                    hash ^= timelineName[i];
                    hash *= prime;
                }
                return (int)hash;
            }
        }

        private void AlterUpdate_MirrorReflection(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (OnUpdateMirrorReflection == null) return;
            if (sheet == null || sheet.environmentDataLists == null) return;

            int count = sheet.environmentDataLists.Count;
            for (int i = 0; i < count; i++)
            {
                var envData = sheet.environmentDataLists[i];
                if (envData == null || envData.keys == null) continue;
                if (string.IsNullOrEmpty(envData.name) || string.Equals(envData.name, "Environment", StringComparison.OrdinalIgnoreCase))
                    continue;

                var keys = envData.keys;
                if (keys.Count <= 0) continue;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                var cur = curKey as LiveTimelineKeyMirrorReflectionData;
                var next = nextKey as LiveTimelineKeyMirrorReflectionData;
                if (cur == null) continue;

                MirrorReflectionUpdateInfo info = default;
                info.TimelineNameHash = GenerateMirrorTimelineHash(envData.name);
                info.BaseCameraType = LiveTimelineDefine.MirrorReflectionBaseCameraType.MainCamera;
                info.BaseCameraIndex = 0;
                info.EnableMirror = cur.GetMirrorEnabled();
                info.EnableBgLayer = cur.GetBgMirrorEnabled();
                info.Enable3dLayer = cur.GetBg3dMirrorEnabled();
                info.IsToonMirror = cur.IsToonMirror;
                info.MirrorReflectionRate = cur.GetMirrorReflectionRate();
                info.TargetChara = cur.GetCharacterMirrorFlag();
                info.EnableCharaHead = cur.GetEnableCharacterMirrorHead();
                info.TargetCharaHead = cur.GetCharacterMirrorHeadFlag();

                if (next != null && next.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(cur, next, currentFrame);
                    info.MirrorReflectionRate = LerpWithoutClamp(cur.GetMirrorReflectionRate(), next.GetMirrorReflectionRate(), t);
                }

                OnUpdateMirrorReflection.Invoke(in info);
            }
        }

        private void AlterUpdate_GlobalLight(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            GlobalLightUpdateInfo updateInfo = default;
            int count = sheet.globalLightDataLists.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelineKeyGlobalLightDataList keys = sheet.globalLightDataLists[i].keys;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }
                else if (sheet.globalLightDataLists[i].name != "GlobalLight")
                {
                    continue;
                }
                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                if (curKey != null)
                {
                    LiveTimelineKeyGlobalLightData lightData = curKey as LiveTimelineKeyGlobalLightData;
                    LiveTimelineKeyGlobalLightData lightData2 = nextKey as LiveTimelineKeyGlobalLightData;
                    Quaternion quaternion = Quaternion.identity;
                    if (lightData.cameraFollow)
                    {
                        quaternion = GetCamera(sheet.targetCameraIndex).cacheTransform.rotation;
                    }
                    updateInfo.flags = lightData.flags;
                    if (lightData2 != null && lightData2.interpolateType != 0)
                    {
                        float ratio = CalculateInterpolationValue(lightData, lightData2, currentFrame);
                        Quaternion a = Quaternion.Euler(lightData.lightDir);
                        Quaternion b = Quaternion.Euler(lightData2.lightDir);
                        updateInfo.lightRotation = Quaternion.Lerp(a, b, ratio) * quaternion;
                        updateInfo.globalRimShadowRate = LerpWithoutClamp(lightData.globalRimShadowRate, lightData2.globalRimShadowRate, ratio);
                        updateInfo.rimColor = Color.Lerp(lightData.rimColor, lightData2.rimColor, ratio);
                        updateInfo.rimStep = LerpWithoutClamp(lightData.rimStep, lightData2.rimStep, ratio);
                        updateInfo.rimFeather = LerpWithoutClamp(lightData.rimFeather, lightData2.rimFeather, ratio);
                        updateInfo.rimSpecRate = LerpWithoutClamp(lightData.rimSpecRate, lightData2.rimSpecRate, ratio);
                        updateInfo.flags = lightData2.flags;
                        updateInfo.RimHorizonOffset = LerpWithoutClamp(lightData.RimHorizonOffset, lightData2.RimHorizonOffset, ratio);
                        updateInfo.RimVerticalOffset = LerpWithoutClamp(lightData.RimVerticalOffset, lightData2.RimVerticalOffset, ratio);
                        updateInfo.RimHorizonOffset2 = LerpWithoutClamp(lightData.RimHorizonOffset2, lightData2.RimHorizonOffset2, ratio);
                        updateInfo.RimVerticalOffset2 = LerpWithoutClamp(lightData.RimVerticalOffset2, lightData2.RimVerticalOffset2, ratio);
                        updateInfo.rimColor2 = Color.Lerp(lightData.rimColor2, lightData2.rimColor2, ratio);
                        updateInfo.rimStep2 = LerpWithoutClamp(lightData.rimStep2, lightData2.rimStep2, ratio);
                        updateInfo.rimFeather2 = LerpWithoutClamp(lightData.rimFeather2, lightData2.rimFeather2, ratio);
                        updateInfo.rimSpecRate2 = LerpWithoutClamp(lightData.rimSpecRate2, lightData2.rimSpecRate2, ratio);
                        updateInfo.globalRimShadowRate2 = LerpWithoutClamp(lightData.globalRimShadowRate2, lightData2.globalRimShadowRate2, ratio);
                    }
                    else
                    {
                        updateInfo.lightRotation = Quaternion.Euler(lightData.lightDir) * quaternion;
                        updateInfo.globalRimShadowRate = lightData.globalRimShadowRate;
                        updateInfo.rimColor = lightData.rimColor;
                        updateInfo.rimStep = lightData.rimStep;
                        updateInfo.rimFeather = lightData.rimFeather;
                        updateInfo.rimSpecRate = lightData.rimSpecRate;
                        updateInfo.flags = lightData.flags;
                        updateInfo.RimHorizonOffset = lightData.RimHorizonOffset;
                        updateInfo.RimVerticalOffset = lightData.RimVerticalOffset;
                        updateInfo.RimHorizonOffset2 = lightData.RimHorizonOffset2;
                        updateInfo.RimVerticalOffset2 = lightData.RimVerticalOffset2;
                        updateInfo.rimColor2 = lightData.rimColor2;
                        updateInfo.rimStep2 = lightData.rimStep2;
                        updateInfo.rimFeather2 = lightData.rimFeather2;
                        updateInfo.rimSpecRate2 = lightData.rimSpecRate2;
                        updateInfo.globalRimShadowRate2 = lightData.globalRimShadowRate2;
                    }
                    OnUpdateGlobalLight.Invoke(ref updateInfo);
                }
            }
        }

        // 角色默认背景色轨道名称集合（保留原有定义作为角色默认参考集合，不在时间轴层硬编码截断其它轨道）
        private HashSet<string> validBgColorNames = new HashSet<string> { "CharaCenter", "CharaLeft", "CharaRight", "CharaColor" };

        private void AlterUpdate_BgColor1(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            int count = sheet.bgColor1List.Count;

            for (int i = 0; i < count; i++)
            {
                LiveTimelineKeyBgColor1DataList keys = sheet.bgColor1List[i].keys;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }
                // 移除原有的白名单硬编码过滤：允许所有 BgColor1 轨道（包括天空 sky_base_00、sky_grad_00、pfb_env_live_cmn_sky002 以及舞台道具）
                // 都能正常计算并触发 OnUpdateBgColor1 事件，由接收端（如 StageController/Director）按需过滤
                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                if (curKey == null)
                {
                    continue;
                }
                BgColor1UpdateInfo updateInfo = default;
                LiveTimelineKeyBgColor1Data bgColorData = curKey as LiveTimelineKeyBgColor1Data;
                LiveTimelineKeyBgColor1Data bgColorData2 = nextKey as LiveTimelineKeyBgColor1Data;

                updateInfo.TimelineName = sheet.bgColor1List[i].name;
                // 舞台物体按 FNV-1a 建索引，这里必须同一套哈希，不能用 Animator.StringToHash。
                updateInfo.TimelineNameHash = string.IsNullOrEmpty(updateInfo.TimelineName) ? 0 : FNVHash.Generate(updateInfo.TimelineName);
                updateInfo.TargetCharaIdArray = sheet.bgColor1List[i].TargetCharaIdArray;
                updateInfo.TargetDressIdArray = sheet.bgColor1List[i].TargetDressIdArray;
                updateInfo.IsSilhouette = bgColorData != null && bgColorData.IsSilhouette;
                updateInfo.IsProjector = bgColorData != null && bgColorData.IsProjector;
                updateInfo.IsSyncBlinkLight = bgColorData != null && bgColorData.IsSyncBlinkLight;
                updateInfo.BlinkLightNameHash = bgColorData != null ? bgColorData.BlinkLightNameHash : 0;
                updateInfo.BlinkLightContainerIndex = bgColorData != null ? bgColorData.BlinkLightContainerIndex : -1;
                updateInfo.BlinkLightBrightnessPower = bgColorData != null ? bgColorData.BlinkLightBrightnessPower : 0f;
                updateInfo.IsAdjustedBlinkLightColor = bgColorData != null && bgColorData.IsAdjustedBlinkLightColor;
                updateInfo.colorPower = bgColorData != null ? bgColorData.power : 0f;
                updateInfo.scale = bgColorData != null ? bgColorData.scale : 1f;
                updateInfo.vertexColorToonPower = bgColorData != null ? bgColorData.vertexColorToonPower : 0f;
                updateInfo.outlineWidthPower = bgColorData != null ? bgColorData.outlineWidthPower : 0f;
                updateInfo.LightBlendMode = bgColorData != null ? bgColorData.LightBlendMode : 0;
                updateInfo.CurrentColorType = bgColorData != null ? bgColorData.ColorType : 0;
                updateInfo.CurrentColor = bgColorData != null ? bgColorData.color : Color.white;
                updateInfo.CurrentFlags = bgColorData != null ? bgColorData.flags : 0;

                if (bgColorData2 != null && bgColorData2.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(bgColorData, bgColorData2, currentFrame);
                    updateInfo.InterpolateRatio = t;
                    updateInfo.flags = bgColorData.flags;
                    updateInfo.color = Color.Lerp(bgColorData.color, bgColorData2.color, t);
                    updateInfo.toonDarkColor = Color.Lerp(bgColorData.toonDarkColor, bgColorData2.toonDarkColor, t);
                    updateInfo.toonBrightColor = Color.Lerp(bgColorData.toonBrightColor, bgColorData2.toonBrightColor, t);
                    updateInfo.outlineColor = Color.Lerp(bgColorData.outlineColor, bgColorData2.outlineColor, t);
                    updateInfo.outlineColorBlend = bgColorData.outlineColorBlend;
                    updateInfo.Saturation = LerpWithoutClamp(bgColorData.Saturation, bgColorData2.Saturation, t);
                    // 补全对 colorPower 的实时插值计算，确保天空变色在过渡区间的亮度与发光强度平滑渐变
                    updateInfo.colorPower = (bgColorData != null && bgColorData2 != null) ? Mathf.Lerp(bgColorData.power, bgColorData2.power, t) : (bgColorData != null ? bgColorData.power : 1f);
                    updateInfo.NextColorType = bgColorData2.ColorType;
                    updateInfo.NextColor = bgColorData2.color;
                    updateInfo.NextFlags = bgColorData2.flags;
                    updateInfo.IsSyncBlinkLightNext = bgColorData2.IsSyncBlinkLight;
                }
                else
                {
                    updateInfo.flags = bgColorData.flags;
                    updateInfo.color = bgColorData.color;
                    updateInfo.toonDarkColor = bgColorData.toonDarkColor;
                    updateInfo.toonBrightColor = bgColorData.toonBrightColor;
                    updateInfo.outlineColor = bgColorData.outlineColor;
                    updateInfo.outlineColorBlend = bgColorData.outlineColorBlend;
                    updateInfo.Saturation = bgColorData.Saturation;
                    updateInfo.colorPower = bgColorData != null ? bgColorData.power : 1f;
                    updateInfo.NextColorType = updateInfo.CurrentColorType;
                    updateInfo.NextColor = updateInfo.CurrentColor;
                    updateInfo.NextFlags = updateInfo.CurrentFlags;
                }
                OnUpdateBgColor1?.Invoke(ref updateInfo);
            }
        }

        private bool TryGetBlinkLightColorRGB(string blinkLightName, int blinkLightNameHash, int blinkLightContainerIndex, bool isAdjustedBlinkLightColor, out Color color, out float colorPower)
        {
            color = Color.black;
            colorPower = 0f;

            if (blinkLightContainerIndex < 0)
                return false;

            var drivers = FindObjectsOfType<StageBlinkLightDriver>(true);
            if (drivers == null || drivers.Length == 0)
                return false;

            for (int i = 0; i < drivers.Length; i++)
            {
                StageBlinkLightDriver driver = drivers[i];
                if (driver == null)
                    continue;

                Color rawColor;
                float rawColorPower;

                if (!driver.TryGetCurrentBlinkColor(
                        blinkLightName,
                        blinkLightNameHash,
                        blinkLightContainerIndex,
                        out rawColor,
                        out rawColorPower))
                {
                    continue;
                }

                colorPower = rawColorPower;

                color = ApplyOfficialBlinkLightColorRGB( rawColor, rawColorPower, isAdjustedBlinkLightColor);

                return true;
            }

            return false;
        }

        private static Color ApplyOfficialBlinkLightColorRGB( Color rawColor, float colorPower, bool isAdjustedBlinkLightColor)
    {
        float r = rawColor.r;
        float g = rawColor.g;
        float b = rawColor.b;

        // 对应 Gallop.Math.IsFloatEqualLight(colorPower, 1.0f)
        if (!Gallop.Math.IsFloatEqualLight(colorPower, 1.0f))
        {
            float h;
            float s;
            float v;

            Color.RGBToHSV(new Color(r, g, b, rawColor.a), out h, out s, out v);

            v *= colorPower;

            // true = hdr，不能 clamp 到 0~1
            Color hsvColor = Color.HSVToRGB(h, s, v, true);

            r = hsvColor.r;
            g = hsvColor.g;
            b = hsvColor.b;
        }

        // 对应伪代码里的 a4 分支
        if (isAdjustedBlinkLightColor)
        {
            r += 1.0f;
            g += 1.0f;
            b += 1.0f;
        }

        return new Color(r, g, b, rawColor.a);
    }

        private void AlterUpdate_BgColor2(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (OnUpdateBgColor2 == null) return;
            if (sheet == null || sheet.bgColor2List == null) return;

            int count = sheet.bgColor2List.Count;
            for (int i = 0; i < count; i++)
            {
                var bgData = sheet.bgColor2List[i];
                if (bgData == null || bgData.keys == null) continue;

                var keys = bgData.keys;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                var cur = curKey as LiveTimelineKeyBgColor2Data;
                var next = nextKey as LiveTimelineKeyBgColor2Data;
                if (cur == null) continue;

                Color curColor1 = cur.color1;
                Color curColor2 = cur.color2;

                if ((cur.IsSyncBlinkLightToColor1 || cur.IsSyncBlinkLightToColor2) &&
                    TryGetBlinkLightColorRGB(cur.BlinkLightName, cur.BlinkLightNameHash, cur.BlinkLightContainerIndex, cur.IsAdjustedBlinkLightColor, out var blinkColor, out _))
                {
                    if (cur.IsSyncBlinkLightToColor1)
                    {
                        curColor1.r = blinkColor.r;
                        curColor1.g = blinkColor.g;
                        curColor1.b = blinkColor.b;
                    }

                    if (cur.IsSyncBlinkLightToColor2)
                    {
                        curColor2.r = blinkColor.r;
                        curColor2.g = blinkColor.g;
                        curColor2.b = blinkColor.b;
                    }
                }

                BgColor2UpdateInfo updateInfo = default;
                updateInfo.TimelineName = bgData.name;
                updateInfo.TimelineNameHash = string.IsNullOrEmpty(bgData.name) ? 0 : FNVHash.Generate(bgData.name);
                updateInfo.color1 = curColor1;
                updateInfo.color2 = curColor2;
                updateInfo.power = cur.power;
                updateInfo.randomTableIndex = cur.RandomTableIndex();

                if (next != null && next.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(cur, next, currentFrame);

                    Color nextColor1 = next.color1;
                    Color nextColor2 = next.color2;
                    if ((next.IsSyncBlinkLightToColor1 || next.IsSyncBlinkLightToColor2) &&
                        TryGetBlinkLightColorRGB(next.BlinkLightName, next.BlinkLightNameHash, next.BlinkLightContainerIndex, next.IsAdjustedBlinkLightColor, out var nextBlinkColor, out _))
                    {
                        if (next.IsSyncBlinkLightToColor1)
                        {
                            nextColor1.r = nextBlinkColor.r;
                            nextColor1.g = nextBlinkColor.g;
                            nextColor1.b = nextBlinkColor.b;
                        }

                        if (next.IsSyncBlinkLightToColor2)
                        {
                            nextColor2.r = nextBlinkColor.r;
                            nextColor2.g = nextBlinkColor.g;
                            nextColor2.b = nextBlinkColor.b;
                        }
                    }

                    updateInfo.color1 = Color.Lerp(curColor1, nextColor1, t);
                    updateInfo.color2 = Color.Lerp(curColor2, nextColor2, t);
                    updateInfo.power = LerpWithoutClamp(cur.power, next.power, t);
                }

                OnUpdateBgColor2?.Invoke(ref updateInfo);
            }
        }

        private void AlterUpdate_MobControl(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            AlterUpdate_MobCyalumeControl(
                sheet != null ? sheet.mobControlList : null,
                currentFrame,
                OnUpdateMobControl);
        }

        private void AlterUpdate_CyalumeControl(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            AlterUpdate_MobCyalumeControl(
                sheet != null ? sheet.cyalumeControlList : null,
                currentFrame,
                OnUpdateCyalumeControl);
        }

        private void AlterUpdate_MobCyalumeControl(
            List<LiveTimelineMobCyalumeControlData> dataList,
            float currentFrame,
            MobCyalumeUpdateInfoDelegate callback)
        {
            if (callback == null || dataList == null)
                return;

            int count = dataList.Count;
            for (int i = 0; i < count; i++)
            {
                var controlData = dataList[i];
                if (controlData == null || controlData.keys == null)
                    continue;

                var keys = controlData.keys;
                if (keys.Count <= 0)
                    continue;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable))
                    continue;
                if (!keys.EnablePlayModeTimeline(_playMode))
                    continue;

                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);

                var current = curKey as LiveTimelineKeyMobCyalumeControlData;
                var next = nextKey as LiveTimelineKeyMobCyalumeControlData;
                if (current == null)
                    continue;

                MobCyalumeUpdateInfo info = default;
                info.data = controlData;
                info.unk0 = (uint)keys.unk48 < 11u ? keys.unk48 : i;
                info.currentFrame = currentFrame;
                info.currentLiveTime = currentLiveTime;

                if (next != null && next.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(current, next, currentFrame);
                    info.position = Vector3.Lerp(current.position, next.position, t);
                    info.rotation = Quaternion.Lerp(current.GetRotation(), next.GetRotation(), t);
                    info.scale = Vector3.Lerp(current.scale, next.scale, t);
                }
                else
                {
                    info.position = current.position;
                    info.rotation = current.GetRotation();
                    info.scale = current.scale;
                }

                callback.Invoke(ref info);
            }
        }

        private const float kBlinkFps = 60f;

        private void AlterUpdate_BlinkLight(LiveTimelineWorkSheet workSheet, float currentFrame)
        {
            if (OnUpdateBlinkLight == null) return;
            if (workSheet == null || workSheet.blinkLightList == null) return;

            var list = workSheet.blinkLightList;
            if (list.Count <= 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                var blinkData = list[i];
                if (blinkData == null || blinkData.keys == null) continue;

                var keys = blinkData.keys;
                if (keys.Count <= 0) continue;

                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;

                FindTimelineKey(out LiveTimelineKey curKey, out LiveTimelineKey nextKey, keys, currentFrame);

                var curBlink = curKey as LiveTimelineKeyBlinkLightData;
                if (curBlink == null) continue;

                float localTime = Mathf.Max(0f, (currentFrame - curBlink.frame) * kFrameToSec);

                BlinkLightUpdateInfo info = BuildOfficialBlinkLightUpdateInfo(curBlink, localTime);

                OnUpdateBlinkLight(blinkData, ref info, currentLiveTime);
            }
        }
        private static BlinkLightUpdateInfo BuildOfficialBlinkLightUpdateInfo( LiveTimelineKeyBlinkLightData key, float localTime)
        {
            BlinkLightUpdateInfo info = new BlinkLightUpdateInfo();

            if (key == null)
                return info;

            info.progressTime = Mathf.Max(0f, localTime);
            info.keyIndex = GetBlinkLightKeyIndex(key);

            info.LightBlendMode = ToLightBlendMode(key.LightBlendMode);

            info.color0Array = key.color0Array;
            info.color1Array = key.color1Array;
            info.powerArray = key.powerArray;

            // 官方 BlinkLightUpdateInfo 是 bool[]
            info.isReverseHueArray = ConvertReverseHueArray(key.isReverseHueArray);

            info.pattern = (BlinkLightPattern)key.pattern;
            info.colorType = (BlinkLightColorType)key.colorType;

            info.powerMin = key.powerMin;
            info.powerMax = key.powerMax;
            info.loopCount = key.loopCount;
            info.waitTime = key.waitTime;
            info.turnOnTime = key.turnOnTime;
            info.turnOffTime = key.turnOffTime;
            info.keepTime = key.keepTime;
            info.intervalTime = key.intervalTime;

            info.UseWashLightBlendMode = ReadUseWashLightBlendModeFromKey(key);

            return info;
        }

        private static LiveDefine.LightBlendMode ToLightBlendMode(object raw)
        {
            if (raw == null)
                return LiveDefine.LightBlendMode.Multiply;

            if (raw is LiveDefine.LightBlendMode m)
                return m;

            try
            {
                return (LiveDefine.LightBlendMode)Convert.ToInt32(raw);
            }
            catch
            {
                return LiveDefine.LightBlendMode.Multiply;
            }
        }

        private static bool[] ConvertReverseHueArray(object src)
        {
            if (src == null)
                return null;

            if (src is bool[] boolArray)
                return boolArray;

            if (src is int[] intArray)
            {
                bool[] dst = new bool[intArray.Length];
                for (int i = 0; i < intArray.Length; i++)
                    dst[i] = intArray[i] != 0;
                return dst;
            }

            if (src is float[] floatArray)
            {
                bool[] dst = new bool[floatArray.Length];
                for (int i = 0; i < floatArray.Length; i++)
                    dst[i] = Mathf.Abs(floatArray[i]) > 0.0001f;
                return dst;
            }

            return null;
        }

        private static int GetBlinkLightKeyIndex(LiveTimelineKeyBlinkLightData key)
        {
            if (key == null)
                return 0;

            var t = key.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            string[] names =
            {
                "keyIndex",
                "KeyIndex",
                "index",
                "Index"
            };

            for (int i = 0; i < names.Length; i++)
            {
                var f = t.GetField(names[i], flags);
                if (f != null && f.FieldType == typeof(int))
                    return (int)f.GetValue(key);

                var p = t.GetProperty(names[i], flags);
                if (p != null && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0)
                    return (int)p.GetValue(key, null);
            }

            return key.frame;
        }

        private static bool ReadUseWashLightBlendModeFromKey(LiveTimelineKeyBlinkLightData key)
        {
            if (key == null)
                return false;

            var t = key.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            string[] names =
            {
                "UseWashLightBlendMode",
                "useWashLightBlendMode",
                "useWashLightBlend",
                "UseWashLightBlend",
                "isUseWashLightBlendMode",
                "IsUseWashLightBlendMode"
            };

            for (int i = 0; i < names.Length; i++)
            {
                var f = t.GetField(names[i], flags);
                if (f != null && f.FieldType == typeof(bool))
                    return (bool)f.GetValue(key);

                var p = t.GetProperty(names[i], flags);
                if (p != null && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0)
                    return (bool)p.GetValue(key, null);
            }

            return false;
        }

        private void AlterUpdate_WashLight(LiveTimelineWorkSheet workSheet, float currentFrame)
        {
            if (OnUpdateWashLight == null) return;
            if (workSheet == null || workSheet.washLightList == null) return;

            var list = workSheet.washLightList;
            if (list.Count <= 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                var washData = list[i];
                if (washData == null || washData.keys == null) continue;

                var keys = washData.keys;
                if (keys.Count <= 0) continue;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;

                FindTimelineKey(out LiveTimelineKey curKey, out LiveTimelineKey nextKey, keys, currentFrame);

                var curWash = curKey as LiveTimelineKeyWashLightData;
                var nextWash = nextKey as LiveTimelineKeyWashLightData;

                if (curWash == null)
                    continue;

                float raycastDistance = curWash.RaycastDistance;
                float cameraProjectionSide = curWash.CameraProjectionSide;
                float cameraProjectionColorPower = curWash.CameraProjectionColorPower;

                if (nextWash != null && nextWash.interpolateType != LiveCameraInterpolateType.None)
                {
                    float t = CalculateInterpolationValue(curWash, nextWash, currentFrame);

                    raycastDistance = LerpWithoutClamp(
                        curWash.RaycastDistance,
                        nextWash.RaycastDistance,
                        t
                    );

                    cameraProjectionSide = LerpWithoutClamp(
                        curWash.CameraProjectionSide,
                        nextWash.CameraProjectionSide,
                        t
                    );

                    cameraProjectionColorPower = LerpWithoutClamp(
                        curWash.CameraProjectionColorPower,
                        nextWash.CameraProjectionColorPower,
                        t
                    );
                }

                WashLightUpdateInfo info = default;

                info.NameHash = !string.IsNullOrEmpty(washData.name)
                    ? Animator.StringToHash(washData.name)
                    : 0;

                info.IsEnabledRaycast = curWash.IsEnabledRaycast;
                info.RaycastDistance = raycastDistance;
                info.IsAllSettings = washData._isAllSettings != 0;
                info.CameraProjectionSide = cameraProjectionSide;
                info.CameraProjectionColorPower = cameraProjectionColorPower;

                OnUpdateWashLight(ref info);
            }
        }
        private void AlterUpdate_Laser(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            int frame = Mathf.FloorToInt(currentFrame);
            AlterUpdate_LaserInternal(
                sheet,
                frame,
                currentLiveTime,
                ref _laserUpdateInfo,
                _laserRuntimeIndexOffset);

            if (sheet != null && sheet.laserList != null)
                _laserRuntimeIndexOffset += sheet.laserList.Count;
        }

        // Official public entry point/signature.
        public void AlterUpdate_Laser(
            LiveTimelineWorkSheet sheet,
            int currentFrame,
            float currentTime,
            ref LaserUpdateInfo updateInfo)
        {
            AlterUpdate_LaserInternal(sheet, currentFrame, currentTime, ref updateInfo, 0);
        }

        private void AlterUpdate_LaserInternal( LiveTimelineWorkSheet sheet, int currentFrame, float currentTime, ref LaserUpdateInfo updateInfo, int runtimeIndexOffset)
        {
            LaserUpdateInfoDelegate handler = OnUpdateLaser;
            if (handler == null || sheet == null || sheet.laserList == null)
                return;

            int count = sheet.laserList.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelineLaserData laserData = sheet.laserList[i];
                if (laserData == null || laserData.keys == null)
                    continue;

                LiveTimelineKeyLaserDataList keys = laserData.keys;
                if (keys.Count <= 0 ||
                    keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) ||
                    !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }

                FindTimelineKey( out LiveTimelineKey currentKeyBase, out LiveTimelineKey nextKeyBase, keys, currentFrame);

                LiveTimelineKeyLaserData currentKey = currentKeyBase as LiveTimelineKeyLaserData;
                if (currentKey == null)
                    continue;

                LiveTimelineKeyLaserData nextKey = nextKeyBase as LiveTimelineKeyLaserData;

                Quaternion objectRotation = Quaternion.Euler(currentKey.objectRotate);
                Quaternion rotation = Quaternion.Euler(currentKey.rotate);
                Vector3 objectPosition = currentKey.objectPosition;
                Vector3 objectScale = currentKey.objectScale;
                float degRootYaw = currentKey.degRootYaw;
                float degLaserPitch = currentKey.degLaserPitch;
                float positionInterval = currentKey.posInterval;
                float blinkPeriod = currentKey.blinkPeriod;

                // Official condition: interpolation belongs to the NEXT key.
                if (nextKey != null && nextKey.IsInterpolateKey())
                {
                    float t = CalculateInterpolationValue(currentKey, nextKey, currentFrame);

                    objectPosition.x = (nextKey.objectPosition.x - currentKey.objectPosition.x) * t + currentKey.objectPosition.x;
                    objectPosition.y = (nextKey.objectPosition.y - currentKey.objectPosition.y) * t + currentKey.objectPosition.y;
                    objectPosition.z = (nextKey.objectPosition.z - currentKey.objectPosition.z) * t + currentKey.objectPosition.z;

                    objectRotation = Quaternion.Lerp( objectRotation, Quaternion.Euler(nextKey.objectRotate), t);

                    objectScale.x = (nextKey.objectScale.x - currentKey.objectScale.x) * t + currentKey.objectScale.x;
                    objectScale.y = (nextKey.objectScale.y - currentKey.objectScale.y) * t + currentKey.objectScale.y;
                    objectScale.z = (nextKey.objectScale.z - currentKey.objectScale.z) * t + currentKey.objectScale.z;

                    rotation = Quaternion.Lerp(rotation, Quaternion.Euler(nextKey.rotate), t);

                    degRootYaw = (nextKey.degRootYaw - currentKey.degRootYaw) * t + currentKey.degRootYaw;
                    degLaserPitch = (nextKey.degLaserPitch - currentKey.degLaserPitch) * t + currentKey.degLaserPitch;
                    positionInterval = (nextKey.posInterval - currentKey.posInterval) * t + currentKey.posInterval;
                    blinkPeriod = (nextKey.blinkPeriod - currentKey.blinkPeriod) * t + currentKey.blinkPeriod;
                }

                updateInfo.timelineIndex = runtimeIndexOffset + i;

                //必须是当前Key开始后的相对帧,不是 Live 全局帧
                updateInfo.ProgressFrame =Mathf.Max(0,currentFrame - currentKey.frame);

                //currentTime - GetTimeFromFrame(currentKey.frame)。
                updateInfo.ProgressTime = Mathf.Max(0f, currentTime - currentKey.frame / 60f);

                updateInfo.objectPosition = objectPosition;
                updateInfo.objectRotation = objectRotation;
                updateInfo.objectScale = objectScale;
                updateInfo.isEnabledRender = currentKey.IsEnabledRender;

                updateInfo.formation = currentKey.formation;
                updateInfo.rotation = rotation;

                updateInfo.degRootYaw = degRootYaw;
                updateInfo.degLaserPitch = degLaserPitch;
                updateInfo.posInterval = positionInterval;

                updateInfo.blink = currentKey.blink;
                updateInfo.blinkPeroid = blinkPeriod;
                updateInfo.IsDisabledRootLight = currentKey.IsDisabledRootLight;
                updateInfo.IsEnabledRaycast = currentKey.IsEnabledRaycast;
                updateInfo.RaycastDistance = currentKey.RaycastDistance;

                handler(ref updateInfo);
            }
        }

        private void AlterUpdate_UVScrollLight(LiveTimelineWorkSheet workSheet, float currentFrame)
        {
            if (OnUpdateUVScrollLight == null) return;
            if (workSheet == null || workSheet.uvScrollLightList == null) return;

            var list = workSheet.uvScrollLightList;
            if (list.Count <= 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                var uvData = list[i];
                if (uvData == null || uvData.keys == null) continue;

                var keys = uvData.keys;
                if (keys.Count <= 0) continue;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable)) continue;
                if (!keys.EnablePlayModeTimeline(_playMode)) continue;

                FindTimelineKey(out LiveTimelineKey curKey, out LiveTimelineKey nextKey, keys, currentFrame);

                var curUv = curKey as LiveTimelineKeyUVScrollLightData;
                if (curUv == null) continue;

                float elapsedTime = (currentFrame - curUv.frame) * kFrameToSec;

                UVScrollLightUpdateInfo info = UVScrollLightUpdateInfo.Create(uvData, curUv, elapsedTime);
                OnUpdateUVScrollLight(ref info);
            }
        }

    }
}
