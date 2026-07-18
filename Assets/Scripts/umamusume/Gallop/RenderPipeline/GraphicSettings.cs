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