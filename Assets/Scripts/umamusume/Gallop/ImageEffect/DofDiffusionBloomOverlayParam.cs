using System;
using UnityEngine;

namespace Gallop.ImageEffect
{
    [Serializable]
    public class DofDiffusionBloomOverlayParam
    {
        public enum BloomScreenBlendMode
        {
            Screen,
            Add
        }

        public enum DofDiffusionBloomType
        {
            None,
            DofBloom,
            DiffusionDofBloom,
            Bloom,
            DiffusionBloom,
            Dof,
            OldDof,
            OldDofFastBloom,
            OverlayOnly
        }

        public bool IsEnableDiffusion;
        public bool IsEnableBloom;

        // ===== HDR Bloom 扩展参数（驱动现代 Live 专属时间轴轨道） =====
        public bool IsEnableHdrBloom;
        public float HdrBloomIntensity;
        public float HdrBloomBlurSpread;

        public float BloomDofWeight;
        public float BloomThreshold;
        public float BloomIntensity;
        public float BloomBlurSize;
        public BloomScreenBlendMode BloomBlendMode;

        public float DiffusionBlurSize;
        public float DiffusionBright;
        public float DiffusionThreshold;
        public float DiffusionSaturation;
        public float DiffusionContrast;

        public bool IsEnable
        {
            get
            {
                return IsEnableBloom || IsEnableDiffusion || IsEnableHdrBloom;
            }
        }

        /// <summary>
        /// 映射到 URP Bloom 的有界结果。强度禁止无界叠加，阈值带下限以免把天空抽进高光。
        /// </summary>
        public struct UrpBloomMapping
        {
            public bool Active;
            public float Intensity;
            public float Threshold;
            public float Scatter;
            public float ExtractClamp;
        }

        // 官方 BloomIntensity 区间是 0-15，URP Bloom 同名参数更敏感，按比例收下。
        public const float OfficialToUrpIntensity = 0.28f;
        public const float HdrBloomToUrpIntensity = 0.35f;
        public const float MaxUrpIntensity = 4.5f;
        public const float MaxDiffusionBright = 2f;
        public const float DiffusionIntensityWeight = 0.35f;

        // 【同目异构优化】缺省高光安全阈值。当时间轴阈值为 0 或缺省时，若强行降至 0.05 会导致角色皮肤与天空被无差别卷积成大白雾。
        // 在开启 HDR 后，普通色调在 [0, 1]，安全阈值设为 0.9f 可确保只有真实物理高光或发光材质才触发泛光。
        public const float DefaultSafeThreshold = 0.9f;
        public const float SafeMinThreshold = 0.5f;
        public const float MaxThreshold = 1.5f;
        public const float MaxScatter = 0.95f;

        // 1175 开场太阳是高亮 HDR，clamp 太低会把光晕直接掐死。
        public const float ExtractClamp = 64f;

        public UrpBloomMapping ResolveUrpBloom()
        {
            UrpBloomMapping mapping = default;

            // 时间轴有强度就出光；属性位丢失时也不要把 1175 开场太阳晕灭掉。
            bool bloomOn = IsEnableBloom || BloomIntensity > 0.0001f;
            bool diffusionOn = IsEnableDiffusion || DiffusionBright > 0.0001f;
            bool hdrBloomOn = IsEnableHdrBloom || HdrBloomIntensity > 0.0001f;

            float bloomIntensity = bloomOn
                ? Mathf.Max(0f, BloomIntensity)
                : 0f;
            float diffusionBright = diffusionOn
                ? Mathf.Max(0f, DiffusionBright)
                : 0f;
            float hdrIntensity = hdrBloomOn
                ? Mathf.Max(0f, HdrBloomIntensity)
                : 0f;

            float intensity =
                bloomIntensity * OfficialToUrpIntensity +
                Mathf.Min(diffusionBright, MaxDiffusionBright) *
                DiffusionIntensityWeight +
                hdrIntensity * HdrBloomToUrpIntensity;
            mapping.Intensity = Mathf.Min(intensity, MaxUrpIntensity);

            mapping.Active = mapping.Intensity > 0.0001f;

            float threshold = 0f;
            if (bloomOn && diffusionOn)
            {
                // 取较低阈值，日落和角色边缘才能进光晕。
                threshold = Mathf.Min(BloomThreshold, DiffusionThreshold);
                if (threshold <= 0.0001f)
                {
                    threshold = Mathf.Max(BloomThreshold, DiffusionThreshold);
                }
            }
            else if (diffusionOn)
            {
                threshold = DiffusionThreshold;
            }
            else if (bloomOn)
            {
                threshold = BloomThreshold;
            }

            // 【同目异构优化】若时间轴未配置阈值（或为 0），则使用安全的 0.9f 高光阈值；若已配置则 clamp 到安全范围，杜绝整屏发白
            if (threshold <= 0.001f)
            {
                mapping.Threshold = DefaultSafeThreshold;
            }
            else
            {
                mapping.Threshold = Mathf.Clamp(threshold, SafeMinThreshold, MaxThreshold);
            }

            float bloomBlur = bloomOn ? Mathf.Max(0f, BloomBlurSize) : 0f;
            float diffusionBlur = diffusionOn
                ? Mathf.Max(0f, DiffusionBlurSize)
                : 0f;
            float hdrBlur = hdrBloomOn ? Mathf.Max(0f, HdrBloomBlurSpread) : 0f;
            float maxBlur = Mathf.Max(bloomBlur, Mathf.Max(diffusionBlur, hdrBlur));
            if (maxBlur < 0.01f && mapping.Active)
            {
                maxBlur = 4f;
            }
            mapping.Scatter = Mathf.Min(Mathf.Clamp01(maxBlur / 10f), MaxScatter);

            mapping.ExtractClamp = ExtractClamp;
            return mapping;
        }
    }
}
