using UnityEngine;

namespace Gallop
{
    public static class GraphicSettings
    {
  
        //* 图像效果能力开关
        //* 当前项目固定使用最高画质,因此静态初始化时全部开启
        public static bool IsGlobalFog;
        public static bool IsSunShaft;
        public static bool IsTiltShift;
        public static bool IsIndirectLightShaft;

        public static bool IsBloom;
        public static bool IsDOF;
        public static bool IsScreenOverlay;

        // cy拼错单词了,字段名就是 Diffution,不要改成 Diffusion
        public static bool IsDiffution;

        public static bool IsRadialBlur;
        public static bool IsFluctuation;
        public static bool IsLensDistortion;
        public static bool IsChromaticAberration;
        public static bool IsColorCorrection;
        public static bool IsColorGrading;
        public static bool IsToneCurve;
        public static bool IsExposure;
        public static bool IsTransmittedLight;
        public static bool IsRainSplash;

        static GraphicSettings()
        {
            // 项目固定使用最高图像效果画质
            SetGameQualityImageEffect(true);
        }

        public static void SetGameQualityImageEffect(bool isNormal)
        {
            // 无论isNormal是什么,都强制开启这两个
            IsScreenOverlay = true;
            IsRadialBlur = true;

            IsBloom = isNormal;
            IsDOF = isNormal;
            IsDiffution = isNormal;

            IsGlobalFog = isNormal;
            IsSunShaft = isNormal;
            IsTiltShift = isNormal;
            IsIndirectLightShaft = isNormal;

            IsFluctuation = isNormal;
            IsLensDistortion = isNormal;
            IsChromaticAberration = isNormal;

            IsColorGrading = isNormal;
            IsToneCurve = isNormal;
            IsExposure = isNormal;
            IsTransmittedLight = isNormal;

            
        }

        public enum LayerIndex
        {
            LayerDefault = 0,
            LayerCHAR = 1,
            LayerBG = 2,
            LayerEFFECT = 3,
            LayerWater = 4,
            LayerCircleProfile = 5,
            LayerIgnoreRaycast = 6,
            Layer3D = 7,
            LayerTransparentFX = 8,
            LayerTouchEffect = 9,
            LayerNO_VISIBLE = 10,
            LayerUI = 11,
            Layer3DUI = 12,
            LayerBackground3D_NotReflect = 13,
            LayerCharacter3D_NotReflect = 14,
            LayerCharacter3D_0 = 15,
            LayerCharacter3D_1 = 16
        }

        public static int GetCullingLayer(LayerIndex layerIndex)
        {
            string layerName;

            switch (layerIndex)
            {
                case LayerIndex.LayerCHAR:
                    layerName = "CHAR";
                    break;

                case LayerIndex.LayerCharacter3D_NotReflect:
                    layerName = "Character3D_NotReflect";
                    break;

                case LayerIndex.LayerCharacter3D_0:
                    layerName = "Character3D_0";
                    break;

                case LayerIndex.LayerCharacter3D_1:
                    layerName = "Character3D_1";
                    break;

                default:
                    return 0;
            }

            int layer = LayerMask.NameToLayer(layerName);

            if (layer < 0)
            {
                Debug.LogWarning(
                    $"[GraphicSettings] Unity Layer 不存在: {layerName}");

                return 0;
            }

            return 1 << layer;
        }
    }
}