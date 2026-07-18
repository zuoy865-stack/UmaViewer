using System;

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
                return IsEnableBloom || IsEnableDiffusion;
            }
        }
    }
}