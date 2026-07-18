using System;
using System.Collections.Generic;
using UnityEngine;
using static Gallop.Live.Cutt.LiveTimelineDefine;

namespace Gallop.Live.Cutt
{
    [Flags]
    public enum LiveTimelineKeyAttribute
    {
        Disable = 1,
        CameraDelayEnable = 2,
        CameraDelayInherit = 4,
        KeyCommonBitMask = 32768,
        kAttrCheek = 65536,
        kAttrTeary = 131072,
        kAttrTearful = 262144,
        kAttrTeardrop = 524288,
        kAttrMangame = 2097152,
        kAttrFaceShadow = 4194304,
        kAttrFaceShadowVisible = 8388608,
}

    public enum LiveTimelineKeyDataListAttr
    {
        Disable = 1
    }

    public enum TimelineKeyPlayMode
    {
        Always = 0,
        LightOnly = 1,
        DefaultOver = 2
    }

    public enum LiveCameraInterpolateType
    {
        None = 0,
        Linear = 1,
        Curve = 2,
        Ease = 3
    }

    public enum LiveCameraPositionType
    {
        Direct = 0,
        Character = 1
    }

    [Flags]
    public enum LiveCharaPositionFlag
    {
        Place01 = 1,
        Place02 = 2,
        Place03 = 4,
        Place04 = 8,
        Place05 = 16,
        Place06 = 32,
        Place07 = 64,
        Place08 = 128,
        Place09 = 256,
        Place10 = 512,
        Place11 = 1024,
        Place12 = 2048,
        Place13 = 4096,
        Place14 = 8192,
        Place15 = 16384,
        Place16 = 32768,
        Place17 = 65536,
        Place18 = 131072,
        Center = 1,
        Left = 2,
        Right = 4,
        Side = 6,
        Back = 262136,
        Other = 262142,
        All = 262143
    }

    public enum LiveCameraCharaParts
    {
        Face = 0,
        Waist = 1,
        LeftHandWrist = 2,
        RightHandAttach = 3,
        Chest = 4,
        Foot = 5,
        InitFaceHeight = 6,
        InitWaistHeight = 7,
        InitChestHeight = 8,
        RightHandWrist = 9,
        LeftHandAttach = 10,
        ConstFaceHeight = 11,
        ConstChestHeight = 12,
        ConstWaistHeight = 13,
        ConstFootHeight = 14,
        Position = 15,
        PositionWithoutOffset = 16,
        InitialHeightFace = 17,
        InitialHeightChest = 18,
        InitialHeightWaist = 19,
        Max = 20
    }

    public enum LiveCameraCullingLayer
    {
        None = 0,
        TransparentFX = 1,
        Background3d_NotReflect = 2,
        Background3d = 4,
        Character3d = 8,
        Character3d_0 = 16,
        Character3d_1 = 32,
        Character3d_NotReflect = 64,
        NotLayerDefault = 128,
        NotLayer3d = 256,
        Effect = 512
    }

    public enum LiveCameraBgColorType
    {
        Direct = 0,
        CharacterImageColorMain = 1,
        CharacterImageColorSub = 2,
        CharacterUIColorMain = 3,
        CharacterUIColorSub = 4
    }


    [Serializable]
    public class LiveTimelineKeyTimescaleData : LiveTimelineKey
    {
        public override LiveTimelineKeyDataType dataType
        {
            get
            {
                return LiveTimelineKeyDataType.Timescale;
            }
        }

        public float Timescale;
    }


    [System.Serializable]
    public class LiveTimelineKeyCameraPositionData : LiveTimelineKeyWithInterpolate
    {
        public override LiveTimelineKeyDataType dataType
        {
            get
            {
            return LiveTimelineKeyDataType.CameraPos;
            }
        }
        public LiveCameraPositionType setType;
        public Vector3 position;
        public Vector3 charaPos;
        public Vector3[] bezierPoints;
        public LiveCharaPositionFlag charaRelativeBase;
        public LiveCameraCharaParts charaRelativeParts;
        public float traceSpeed;
        public float nearClip;
        public float farClip;
        public LiveCameraCullingLayer cullingLayer;
        public LiveCameraBgColorType BgColorType;
        public Color BgColor;
        public int BgColorTargetCharacterIndex;

        public Vector3 offset = Vector3.zero;

        public Vector3 posDirect = Vector3.zero;

        public bool newBezierCalcMethod;

        public CullingLayer cullingMask = defCameraCullingLayer;

        public float outlineZOffset = 1f;

        public CharacterLOD characterLODMask = (CharacterLOD)((uint)outlineLODMask + (uint)shaderLODMask);

        protected const CullingLayer defCameraCullingLayer = (CullingLayer)0x7FE;

        protected const CharacterLOD outlineLODMask = (CharacterLOD)0x3FF;

        protected const CharacterLOD shaderLODMask = (CharacterLOD)0x1FF8000;

        public int GetCullingMask()
        {
            return GetCullingMask(cullingMask);
        }

        protected static int GetCullingMask(CullingLayer layer)
        {
            int num = 257;
            if ((layer & CullingLayer.TransparentFX) != 0)
            {
                num |= 2;
            }
            if ((layer & CullingLayer.Background3D_NotReflect) != 0)
            {
                num |= 0x80000;
            }
            if ((layer & CullingLayer.Background3d) != 0)
            {
                num |= 0x100000;
            }
            if ((layer & CullingLayer.Character3d) != 0)
            {
                num |= 0x200000;
            }
            if ((layer & CullingLayer.Character3d_0) != 0)
            {
                num |= 0x400000;
            }
            if ((layer & CullingLayer.Character3d_1) != 0)
            {
                num |= 0x800000;
            }
            if ((layer & CullingLayer.Character3d_2) != 0)
            {
                num |= 0x1000000;
            }
            if ((layer & CullingLayer.Character3d_3) != 0)
            {
                num |= 0x2000000;
            }
            if ((layer & CullingLayer.Character3d_4) != 0)
            {
                num |= 0x4000000;
            }
            if ((layer & CullingLayer.Character3D_NotReflect) != 0)
            {
                num |= 0x8000000;
            }
            if ((layer & CullingLayer.Background3D_Other) != 0)
            {
                num |= 0x40000;
            }
            return num;

            /*
            uint bittest = (uint)layer;

            uint result = (uint)LayerMask.GetMask("Default");

            if ((bittest & (uint)CullingLayer.TransparentFX) > 0)
                result |= (uint)LayerMask.GetMask("TransparentFX");
            if ((bittest & (uint)CullingLayer.Background3D_NotReflect) > 0)
                result |= (uint)LayerMask.GetMask("background_NotReflect");
            if ((bittest & (uint)CullingLayer.Background3d) > 0)
                result |= (uint)LayerMask.GetMask("background");
            if ((bittest & (uint)CullingLayer.Character3d) > 0)
                result |= (uint)LayerMask.GetMask("charas");
            if ((bittest & (uint)CullingLayer.Character3d_0) > 0)
                result |= (uint)LayerMask.GetMask("chara1");
            if ((bittest & (uint)CullingLayer.Character3d_1) > 0)
                result |= (uint)LayerMask.GetMask("chara2");
            if ((bittest & (uint)CullingLayer.Character3d_2) > 0)
                result |= (uint)LayerMask.GetMask("chara3");
            if ((bittest & (uint)CullingLayer.Character3d_3) > 0)
                result |= (uint)LayerMask.GetMask("chara4");
            if ((bittest & (uint)CullingLayer.Character3d_4) > 0)
                result |= (uint)LayerMask.GetMask("chara5");
            if ((bittest & (uint)CullingLayer.Character3D_NotReflect) > 0)
                result |= (uint)LayerMask.GetMask("otherChara");
            if ((bittest & (uint)CullingLayer.Background3D_Other) > 0)
                result |= (uint)LayerMask.GetMask("background_Other");
            return (int)result;
            */
        }

        public static int GetDefaultCullingMask()
        {
            return GetCullingMask(defCameraCullingLayer);
        }

        public virtual Vector3 GetValue(LiveTimelineControl timelineControl)
        {
            return GetValue(timelineControl, setType, containOffset: true);
        }

        protected virtual Vector3 GetValue(LiveTimelineControl timelineControl, LiveCameraPositionType type, bool containOffset)
        {
            Vector3 vector = position;
            switch (type)
            {
                case LiveCameraPositionType.Direct:
                    vector += posDirect;
                    break;
                case LiveCameraPositionType.Character:
                    vector += timelineControl.GetPositionWithCharacters(charaRelativeBase, charaRelativeParts, charaPos);
                    break;
            }
            if (!containOffset)
            {
                return vector;
            }
            return vector + offset;
        }

        public int GetBezierPointCount()
        {
            if (!HasBezier())
            {
                return 0;
            }
            return bezierPoints.Length;
        }

        public bool HasBezier()
        {
            if (bezierPoints != null)
            {
                return bezierPoints.Length != 0;
            }
            return false;
        }

        public bool necessaryToUseNewBezierCalcMethod
        {
            get
            {
                if (!newBezierCalcMethod)
                {
                    return GetBezierPointCount() > 3;
                }
                return true;
            }
        }

        public Vector3 GetBezierPoint(int index, LiveTimelineControl timelineControl)
        {
            if (HasBezier() && index < bezierPoints.Length)
            {
                return GetValue(timelineControl) + bezierPoints[index];
            }
            return GetValue(timelineControl) + Vector3.zero;
        }

        public void GetBezierPoints(LiveTimelineControl timelineControl, Vector3[] outPoints, int startIndex)
        {
            if (HasBezier())
            {
                for (int i = 0; i < bezierPoints.Length; i++)
                {
                    outPoints[startIndex + i] = GetValue(timelineControl) + bezierPoints[i];
                }
            }
        }
    }

    public enum CullingLayer
    {
        TransparentFX = 1,
        Background3D_NotReflect = 2,
        Background3d = 4,
        Character3d = 8,
        Character3d_0 = 0x10,
        Character3d_1 = 0x20,
        Character3d_2 = 0x40,
        Character3d_3 = 0x80,
        Character3d_4 = 0x100,
        Character3D_NotReflect = 0x200,
        Background3D_Other = 0x400
    }

    public enum CharacterLOD
    {
        Outline_0 = 1,
        Outline_1 = 2,
        Outline_2 = 4,
        Outline_3 = 8,
        Outline_4 = 0x10,
        Outline_5 = 0x20,
        Outline_6 = 0x40,
        Outline_7 = 0x80,
        Outline_8 = 0x100,
        Outline_9 = 0x200,
        Shader_0 = 0x8000,
        Shader_1 = 0x10000,
        Shader_2 = 0x20000,
        Shader_3 = 0x40000,
        Shader_4 = 0x80000,
        Shader_5 = 0x100000,
        Shader_6 = 0x200000,
        Shader_7 = 0x400000,
        Shader_8 = 0x800000,
        Shader_9 = 0x1000000
    }

    public enum TimelinePlayerMode
    {
        Light,
        Default
    }

    public delegate void CameraPosUpdateInfoDelegate(ref CameraPosUpdateInfo updateInfo);

    [Serializable]
    public class LiveTimelineKeyTimescaleDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyTimescaleData>
    {
        public bool _isCheckSameFrame;
    }

    [System.Serializable]
    public class LiveTimelineKeyCameraPositionDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyCameraPositionData>
    {

    }

    public struct FindKeyResult
    {
        public LiveTimelineKey key;

        public int index;
    }

    public class LiveTimelineWorkSheet : ScriptableObject
    {
        public string version;
        public int targetCameraIndex;
        public bool enableAtRuntime;
        public bool enableAtEdit;
        public float TotalTimeLength;
        public bool Lyrics;
        public LiveTimelineDefine.SheetIndex SheetType;
        [SerializeField] public LiveTimelineKeyTimescaleDataList timescaleKeys;
        [SerializeField] public LiveTimelineKeyCameraPositionDataList cameraPosKeys;
        [SerializeField] public List<LiveTimelineMultiCameraPositionData> multiCameraPosKeys;
        [SerializeField] public List<LiveTimelineMultiCameraLookAtData> multiCameraLookAtKeys;

        //[SerializeField]���ڸ����ڱ�Ľű��ﶨ���ʱ��
        [SerializeField] public LiveTimelineKeyCameraLookAtDataList cameraLookAtKeys;
        [SerializeField] public LiveTimelineKeyCameraFovDataList cameraFovKeys;
        [SerializeField] public LiveTimelineKeyCameraRollDataList cameraRollKeys;

        [SerializeField]
        public LiveTimelineKeyPostEffectBloomDiffusionDataList postEffectBloomDiffusionKeys;
        [SerializeField]
        public List<LiveTimelineHdrBloomData> hdrBloomList;

        [SerializeField] public List<LiveTimelineCharaMotSeqData> charaMotSeqList;
        [SerializeField] public List<LiveTimelineAnimationData> animationList = new List<LiveTimelineAnimationData>();

        [SerializeField] public LiveTimelineKeyCameraSwitcherDataList cameraSwitcherKeys;
        [SerializeField] public LiveTimelineKeyLipSyncDataList ripSyncKeys;
        [SerializeField] public LiveTimelineKeyLipSyncDataList ripSync2Keys;

        [SerializeField] public LiveTimelineFacialData facial1Set;
        [SerializeField] public LiveTimelineFacialData[] other4FacialArray;
        [SerializeField] public LiveTimelineFormationOffsetData formationOffsetSet;

        [SerializeField] public List<LiveTimelineGlobalLightData> globalLightDataLists;
        [SerializeField] public List<LiveTimelineStageEnvironmentData> environmentDataLists;
        [SerializeField] public List<LiveTimelineBgColor1Data> bgColor1List;
        [SerializeField] public List<LiveTimelineBgColor2Data> bgColor2List;
        [SerializeField] public List<LiveTimelineBlinkLightData> blinkLightList;
        [SerializeField] public List<LiveTimelineWashLightData> washLightList;
        [SerializeField] public List<LiveTimelineMonitorControlData> monitorControlList;
        [SerializeField] public List<LiveTimelineMonitorCameraPositionData> monitorCameraPosKeys;
        [SerializeField] public List<LiveTimelineMonitorCameraLookAtData> monitorCameraLookAtKeys;
        [SerializeField]public List<LiveTimelineLaserData> laserList;
        [SerializeField] public List<LiveTimelineUVScrollLightData> uvScrollLightList;

        [SerializeField] public List<LiveTimelineTransformData> transformList;
        [SerializeField] public List<LiveTimelineObjectData> objectList;
        [SerializeField] public List<LiveTimelineMobCyalumeControlData> mobControlList;
        [SerializeField] public List<LiveTimelineMobCyalumeControlData> cyalumeControlList;

        /*
		//���ڿ��Ե���AB���ˣ���Ȼ���淢��ûʲô��...˵����ʲôʱ�����õ�
		private void Start()
		{
			LoadCharaMotion();
		}

		public void LoadCharaMotion()
		{
			foreach(LiveTimelineCharaMotSeqData liveCharaData in charaMotSeqList)
			{
				foreach(LiveTimelineKeyCharaMotionData charaMotionData in liveCharaData.keys.thisList)
				{
					foreach(var motionname in UmaViewerMain.Instance.AbList.Where(a => a.Name.StartsWith("3d/motion/live/body") && a.Name.EndsWith(charaMotionData.motionName)))
					{
						//UmaViewerBuilder.Instance.LoadComponent(motionname);
					}
				}
			}
		}
		*/
    }

    public static class LiveCharaPositionFlag_Helper
    {
        public static LiveCharaPositionFlag Default5 => LiveCharaPositionFlag.Center | LiveCharaPositionFlag.Place02 | LiveCharaPositionFlag.Place03 | LiveCharaPositionFlag.Place04 | LiveCharaPositionFlag.Place05;

        public static LiveCharaPositionFlag Everyone => LiveCharaPositionFlag.All;

        public static bool hasFlag(this LiveCharaPositionFlag This, LiveCharaPosition pos)
        {
            return This.hasFlag((LiveCharaPositionFlag)(1 << (int)pos));
        }

        public static bool hasFlag(this LiveCharaPositionFlag This, int bit)
        {
            return This.hasFlag((LiveCharaPositionFlag)bit);
        }

        public static bool hasFlag(this LiveCharaPositionFlag This, LiveCharaPositionFlag bit)
        {
            return (This & bit) != 0;
        }

        public static bool hasFlag(this LiveTimelineKeyAttribute This, LiveTimelineKeyAttribute bit)
    {
            return (This & bit) != 0;
        }
    }
    
}
