using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Live 角色着色与全局光回调。
    /// 同一帧里 OnUpdateGlobalLight 和 OnUpdateBgColor1 都会 SetPropertyBlock，
    /// 必须按 renderer 缓存同一份 MPB 再叠加写入，禁止 Clear 后整块替换。
    /// </summary>
    public partial class Director
    {
        private static readonly int CharaColorId = Shader.PropertyToID("_CharaColor");
        private static readonly int ColorPowerId = Shader.PropertyToID("_ColorPower");
        private static readonly int ToonDarkColorId = Shader.PropertyToID("_ToonDarkColor");
        private static readonly int ToonBrightColorId = Shader.PropertyToID("_ToonBrightColor");
        private static readonly int OutlineColorId = Shader.PropertyToID("_OutlineColor");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");
        private static readonly int RimShadowRateId = Shader.PropertyToID("_RimShadowRate");
        private static readonly int RimColorId = Shader.PropertyToID("_RimColor");
        private static readonly int RimStepId = Shader.PropertyToID("_RimStep");
        private static readonly int RimFeatherId = Shader.PropertyToID("_RimFeather");
        private static readonly int RimSpecRateId = Shader.PropertyToID("_RimSpecRate");
        private static readonly int RimHorizonOffsetId = Shader.PropertyToID("_RimHorizonOffset");
        private static readonly int RimVerticalOffsetId = Shader.PropertyToID("_RimVerticalOffset");
        private static readonly int RimHorizonOffset2Id = Shader.PropertyToID("_RimHorizonOffset2");
        private static readonly int RimVerticalOffset2Id = Shader.PropertyToID("_RimVerticalOffset2");
        private static readonly int RimColor2Id = Shader.PropertyToID("_RimColor2");
        private static readonly int RimStep2Id = Shader.PropertyToID("_RimStep2");
        private static readonly int RimFeather2Id = Shader.PropertyToID("_RimFeather2");
        private static readonly int RimSpecRate2Id = Shader.PropertyToID("_RimSpecRate2");
        private static readonly int RimShadowRate2Id = Shader.PropertyToID("_RimShadowRate2");
        private static readonly int UseOriginalDirectionalLightId = Shader.PropertyToID("_UseOriginalDirectionalLight");
        private static readonly int OriginalDirectionalLightDirId = Shader.PropertyToID("_OriginalDirectionalLightDir");

        /// <summary>
        /// 角色 BgColor1 轨道名。除了名称含 chara 的轨道外，显式包含 CharaBack / CharaOther。
        /// </summary>
        private static readonly string[] CharacterColorTimelineNames =
        {
            "CharaCenter",
            "CharaLeft",
            "CharaRight",
            "CharaBack",
            "CharaOther",
            "CharaColor"
        };

        /// <summary>
        /// 按 renderer 缓存 MPB。不能全场共用一块：不同角色的颜色不同，
        /// 后一个角色写共享块会污染前一个角色。
        /// </summary>
        private readonly Dictionary<int, MaterialPropertyBlock> _charaRendererPropertyBlocks =
            new Dictionary<int, MaterialPropertyBlock>();

        /// <summary>
        /// 全局光：写入 Rim 与方向光，但不 Clear 已有角色色。
        /// </summary>
        private void OnUpdateGlobalLight(ref GlobalLightUpdateInfo updateInfo)
        {
            Vector3 lightDir = -(updateInfo.lightRotation * Vector3.forward).normalized;
            ILiveTimelineCharactorLocator[] locators = _liveTimelineControl != null
                ? _liveTimelineControl.liveCharactorLocators
                : null;
            if (locators == null)
                return;

            for (int i = 0; i < locators.Length; i++)
            {
                ILiveTimelineCharactorLocator locator = locators[i];
                if (locator == null || !updateInfo.flags.hasFlag(locator.liveCharaStandingPosition))
                    continue;

                LiveTimelineCharaLocator charaLocator = locator as LiveTimelineCharaLocator;
                if (charaLocator == null)
                    continue;

                UmaContainerCharacter container = charaLocator.UmaContainer;
                if (container == null || container.Renderers == null)
                    continue;

                ApplyGlobalLightToContainer(container, ref updateInfo, lightDir);
            }
        }

        /// <summary>
        /// 角色色：只吃角色轨道，带 colorPower，且不 Clear 刚写进去的全局光。
        /// flags=0 仍按全站位处理。
        /// </summary>
        private void OnUpdateBgColor1(ref BgColor1UpdateInfo updateInfo)
        {
            if (!IsCharacterColorTimeline(updateInfo.TimelineName))
                return;

            ILiveTimelineCharactorLocator[] locators = _liveTimelineControl != null
                ? _liveTimelineControl.liveCharactorLocators
                : null;
            if (locators == null)
                return;

            LiveCharaPositionFlag positionFlags = (LiveCharaPositionFlag)updateInfo.flags;
            bool applyAllPositions = updateInfo.flags == 0;

            for (int i = 0; i < locators.Length; i++)
            {
                ILiveTimelineCharactorLocator locator = locators[i];
                if (locator == null)
                    continue;
                if (!applyAllPositions && !positionFlags.hasFlag(locator.liveCharaStandingPosition))
                    continue;

                LiveTimelineCharaLocator charaLocator = locator as LiveTimelineCharaLocator;
                if (charaLocator == null)
                    continue;

                UmaContainerCharacter container = charaLocator.UmaContainer;
                if (container == null || container.Renderers == null)
                    continue;

                ApplyCharaColorToContainer(container, ref updateInfo);
            }
        }

        /// <summary>
        /// 只认角色色轨道。天空 / 舞台名一律丢掉，避免把深蓝天空写到角色上。
        /// </summary>
        private static bool IsCharacterColorTimeline(string timelineName)
        {
            if (string.IsNullOrEmpty(timelineName))
                return false;

            if (timelineName.IndexOf("sky", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (timelineName.IndexOf("BG_BL", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (timelineName.IndexOf("chara", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            for (int i = 0; i < CharacterColorTimelineNames.Length; i++)
            {
                if (string.Equals(timelineName, CharacterColorTimelineNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 取出该 renderer 已缓存的 MPB。第一次从 renderer 拷一份进来，之后只叠加、不 Clear。
        /// </summary>
        private MaterialPropertyBlock GetOrCreateCharaPropertyBlock(Renderer renderer)
        {
            int instanceId = renderer.GetInstanceID();
            MaterialPropertyBlock block;
            if (!_charaRendererPropertyBlocks.TryGetValue(instanceId, out block) || block == null)
            {
                block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                _charaRendererPropertyBlocks[instanceId] = block;
            }

            return block;
        }

        private void ApplyGlobalLightToContainer(
            UmaContainerCharacter container,
            ref GlobalLightUpdateInfo updateInfo,
            Vector3 lightDir)
        {
            List<Renderer> renderers = container.Renderers;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                MaterialPropertyBlock block = GetOrCreateCharaPropertyBlock(renderer);
                WriteGlobalLightToBlock(block, ref updateInfo, lightDir);
                renderer.SetPropertyBlock(block);
            }
        }

        private void ApplyCharaColorToContainer(
            UmaContainerCharacter container,
            ref BgColor1UpdateInfo updateInfo)
        {
            List<Renderer> renderers = container.Renderers;
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                MaterialPropertyBlock block = GetOrCreateCharaPropertyBlock(renderer);
                WriteCharaColorToBlock(block, ref updateInfo);
                renderer.SetPropertyBlock(block);
            }
        }

        private static void WriteGlobalLightToBlock(
            MaterialPropertyBlock block,
            ref GlobalLightUpdateInfo updateInfo,
            Vector3 lightDir)
        {
            block.SetFloat(RimShadowRateId, updateInfo.globalRimShadowRate);
            block.SetColor(RimColorId, updateInfo.rimColor);
            block.SetFloat(RimStepId, updateInfo.rimStep);
            block.SetFloat(RimFeatherId, updateInfo.rimFeather);
            block.SetFloat(RimSpecRateId, updateInfo.rimSpecRate);
            block.SetFloat(RimHorizonOffsetId, updateInfo.RimHorizonOffset);
            block.SetFloat(RimVerticalOffsetId, updateInfo.RimVerticalOffset);
            block.SetFloat(RimHorizonOffset2Id, updateInfo.RimHorizonOffset2);
            block.SetFloat(RimVerticalOffset2Id, updateInfo.RimVerticalOffset2);
            block.SetColor(RimColor2Id, updateInfo.rimColor2);
            block.SetFloat(RimStep2Id, updateInfo.rimStep2);
            block.SetFloat(RimFeather2Id, updateInfo.rimFeather2);
            block.SetFloat(RimSpecRate2Id, updateInfo.rimSpecRate2);
            block.SetFloat(RimShadowRate2Id, updateInfo.globalRimShadowRate2);
            block.SetFloat(UseOriginalDirectionalLightId, 1f);
            block.SetVector(OriginalDirectionalLightDirId, lightDir);
        }

        private static void WriteCharaColorToBlock(
            MaterialPropertyBlock block,
            ref BgColor1UpdateInfo updateInfo)
        {
            // 角色着色必须带 colorPower。时间轴没给 power 时 struct 默认是 0，
            // 直接相乘会把角色乘成黑的，这种情况按 1 处理。
            float colorPower = updateInfo.colorPower;
            if (colorPower <= 0f)
                colorPower = 1f;

            Color source = updateInfo.color;
            Color scaledColor = new Color(
                source.r * colorPower,
                source.g * colorPower,
                source.b * colorPower,
                source.a);

            block.SetColor(CharaColorId, scaledColor);
            block.SetFloat(ColorPowerId, colorPower);
            block.SetColor(ToonDarkColorId, updateInfo.toonDarkColor);
            block.SetColor(ToonBrightColorId, updateInfo.toonBrightColor);
            block.SetColor(OutlineColorId, updateInfo.outlineColor);
            block.SetFloat(SaturationId, updateInfo.Saturation);
        }
    }
}