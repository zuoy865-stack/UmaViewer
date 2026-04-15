using System;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    [Serializable]
    public class LiveTimelineKeyUVScrollLightData : LiveTimelineKeyWithInterpolate
    {
        public Color mulColor0 = Color.white;
        public Color mulColor1 = Color.white;
        public float colorPower = 1f;

        public float scrollOffsetX;
        public float scrollOffsetY;
        public float scrollSpeedX;
        public float scrollSpeedY;

        public Texture2D texture;

        public LiveTimelineDefine.ColorType ColorType0;
        public LiveTimelineDefine.ColorType ColorType1;
        public int CharacterIndex0;
        public int CharacterIndex1;
        public bool IsColorBlend0;
        public bool IsColorBlend1;
        public float ColorBlendRate0;
        public float ColorBlendRate1;
        public Color AltCharaColor0 = Color.white;
        public Color AltCharaColor1 = Color.white;

        public LiveTimelineKeyLoopType loopType;
        public int loopCount;
        public int loopExecutedCount;
        public int loopIntervalFrame;
        public bool isPasteLoopUnit = true;
        public bool isChangeLoopInterpolate = true;

        private const int ENABLE_TEXTURE = 65536;

        // 这里先给兼容版实现。
        // 原版更像是从某个 flags 位里判断 ENABLE_TEXTURE，
        // 但你目前没把那个 flags 字段一并贴出来，所以先保留这个等价近似。
        public bool IsEnabledTexture
        {
            get => texture != null;
            private set { }
        }

        public virtual void OnLoad(LiveTimelineControl timelineControl)
        {
            // 预留：原版这里大概率会根据 ColorType / CharacterIndex / Blend 等
            // 做一次颜色解析或资源准备。
        }
    }

    [Serializable]
    public class LiveTimelineKeyUVScrollLightDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyUVScrollLightData>
    {
    }

    [Serializable]
    public class LiveTimelineUVScrollLightData : ILiveTimelineGroupDataWithName
    {
        public string name;
        public LiveTimelineKeyUVScrollLightDataList keys;
    }
}
