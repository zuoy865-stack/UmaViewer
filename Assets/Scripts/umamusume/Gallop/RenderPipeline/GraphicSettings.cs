using UnityEngine;

namespace Gallop
{
    public static class GraphicSettings
    {
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
            int layer = GetLayer(layerIndex);

            if (layer < 0)
            {
                Debug.LogWarning(
                    $"[GraphicSettings] Unity Layer 不存在: {GetLayerName(layerIndex)}");

                return 0;
            }

            return 1 << layer;
        }

        public static int GetLayer(LayerIndex layerIndex)
        {
            return LayerMask.NameToLayer(GetLayerName(layerIndex));
        }

        public static LayerIndex GetLayerIndex(int layer)
        {
            for (int i = 0; i <= (int)LayerIndex.LayerCharacter3D_1; i++)
            {
                LayerIndex index = (LayerIndex)i;
                if (GetLayer(index) == layer)
                    return index;
            }

            return LayerIndex.LayerDefault;
        }

        public static string GetLayerName(LayerIndex layerIndex)
        {
            switch (layerIndex)
            {
                case LayerIndex.LayerDefault:
                    return "Default";
                case LayerIndex.LayerCHAR:
                    return "CHAR";
                case LayerIndex.LayerBG:
                    return "BG";
                case LayerIndex.LayerEFFECT:
                    return "EFFECT";
                case LayerIndex.LayerWater:
                    return "Water";
                case LayerIndex.LayerCircleProfile:
                    return "CircleProfile";
                case LayerIndex.LayerIgnoreRaycast:
                    return "Ignore Raycast";
                case LayerIndex.Layer3D:
                    return "3D";
                case LayerIndex.LayerTransparentFX:
                    return "TransparentFX";
                case LayerIndex.LayerTouchEffect:
                    return "TouchEffect";
                case LayerIndex.LayerNO_VISIBLE:
                    return "NO_VISIBLE";
                case LayerIndex.LayerUI:
                    return "UI";
                case LayerIndex.Layer3DUI:
                    return "3D_UI";
                case LayerIndex.LayerBackground3D_NotReflect:
                    return "Background3D_NotReflect";
                case LayerIndex.LayerCharacter3D_NotReflect:
                    return "Character3D_NotReflect";
                case LayerIndex.LayerCharacter3D_0:
                    return "Character3D_0";
                case LayerIndex.LayerCharacter3D_1:
                    return "Character3D_1";
                default:
                    return "Default";
            }
        }
    }
}
