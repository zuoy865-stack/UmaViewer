using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    public class LiveTimelineControl : MonoBehaviour
    {
        public LiveTimelineData data;
        public const int kTargetFps = 60;
        public const float kTargetFpsF = 60;
        public const float kFrameToSec = 0.016666668f;
        public Transform cameraPositionLocatorsRoot;
        public Transform cameraLookAtLocatorsRoot;
        [SerializeField]
        private Transform[] characterStandPosLocators;
        public const float BaseCharaHeight = 158;
        public const float BaseCharaHeightMin = 130;
        public const float BaseCharaHeightMax = 190;
        public const float BaseCharaHeightDiff = 60;



        public struct FindTimelineConfig
        {
            public enum KeyType
            {
                KeyDirect,
                CurrentFrame
            }

            public KeyType keyType;

            public ILiveTimelineKeyDataList posKeys;

            public ILiveTimelineKeyDataList lookAtKeys;

            public LiveTimelineKey curKey;

            public LiveTimelineKey nextKey;

            public int extraCameraIndex;
        }

        public struct MirrorReflectionUpdateInfo
        {
            public int TimelineNameHash;
            public LiveTimelineDefine.MirrorReflectionBaseCameraType BaseCameraType;
            public int BaseCameraIndex;
            public bool EnableMirror;
            public bool EnableBgLayer;
            public bool Enable3dLayer;
            public bool IsToonMirror;
            public float MirrorReflectionRate;
            public LiveCharaPositionFlag TargetChara;
            public bool EnableCharaHead;
            public LiveCharaPositionFlag TargetCharaHead;
        }

        public delegate void MirrorReflectionUpdateInfoDelegate(in MirrorReflectionUpdateInfo updateInfo);

        public event MirrorReflectionUpdateInfoDelegate OnUpdateMirrorReflection;

        private LiveTimelineMotionSequence[] _motionSequenceArray;

        public LiveTimelineKeyCharaMotionSeqDataList[] _keyArray;

        public event Action<LiveTimelineKeyIndex, float> OnUpdateLipSync;

        public event Action<FacialDataUpdateInfo, float, int> OnUpdateFacial;

        public event Action<int> OnUpdateCameraSwitcher;

        public event GlobalLightUpdateInfoDelegate OnUpdateGlobalLight;

        public event EnvironmentMirrorDelegate OnEnvironmentMirror;

        public event BgColor1UpdateInfoDelegate OnUpdateBgColor1;

        public event BgColor2UpdateInfoDelegate OnUpdateBgColor2;

        public event TransformUpdateInfoDelegate OnUpdateTransform;

        public event ObjectUpdateInfoDelegate OnUpdateObject;

        public event MobCyalumeUpdateInfoDelegate OnUpdateMobControl;
        public event MobCyalumeUpdateInfoDelegate OnUpdateCyalumeControl;

        public event BlinkLightUpdateInfoDelegate OnUpdateBlinkLight;
        public event WashLightUpdateInfoDelegate OnUpdateWashLight;
        public event AnimationUpdateInfoDelegate OnUpdateAnimation;

        public event LaserUpdateInfoDelegate OnUpdateLaser;
        private LaserUpdateInfo _laserUpdateInfo;
        private int _laserRuntimeIndexOffset;
        public event HdrBloomUpdateInfoDelegate OnUpdateHdrBloom;

        public event Action<PostEffectUpdateInfo_BloomDiffusion> OnUpdatePostEffect_BloomDiffusion;

        public event UVScrollLightUpdateInfoDelegate OnUpdateUVScrollLight;

        private static Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> fnGetCameraPosValue = GetCameraPosValue;

        private static Func<LiveTimelineKeyCameraLookAtData, LiveTimelineControl, Vector3, FindTimelineConfig, Vector3> fnGetCameraLookAtValue = GetCameraLookAtValue;

        private Vector3 _cameraLayerOffset = Vector3.zero;

        private CacheCamera[] _cameraArray = new CacheCamera[3];

        private LiveTimelineCamera[] _cameraScriptArray = new LiveTimelineCamera[3];

        private Dictionary<string, Transform> _cameraPositionLocatorDict;

        private Dictionary<string, Transform> _cameraLookAtLocatorDict;

        private CacheCamera[] _multiCameraCache;

        private MultiCamera[] _multiCamera;

        private bool _isMultiCameraEnable;

        private bool _isNowAlterUpdate;

        private float _oldFrame;

        private float _oldLiveTime;

        private float _currentLiveTime;

        private float _deltaTimeRatio;

        private float _deltaTime;

        private float _baseCameraAspectRatio = 1.77777779f;

        public bool _limitFovForWidth = false;

        private float _currentFrame;

        private bool _isExtraCameraLayer;

        public bool IsRecordVMD;
        public List<LiveCameraFrame> RecordFrames = new List<LiveCameraFrame>();
        public List<List<LiveCameraFrame>> MultiRecordFrames = new List<List<LiveCameraFrame>>();
        public event Action RecordUma;

        public Dictionary<string, GameObject> StageObjectMap = new Dictionary<string, GameObject>();

        public float currentLiveTime
        {
            get
            {
                return _currentLiveTime;
            }
            private set
            {
                _currentLiveTime = value;
            }
        }

        public event CameraPosUpdateInfoDelegate OnUpdateCameraPos;

        public CacheCamera[] cameraArray
        {
            get
            {
                return _cameraArray;
            }
            private set
            {
                _cameraArray = value;
            }
        }

        private LiveTimelineCamera[] cameraScriptArray => _cameraScriptArray;

        private Dictionary<string, Transform> cameraPositionLocatorDict
        {
            get
            {
                if (_cameraPositionLocatorDict == null)
                {
                    _cameraPositionLocatorDict = new Dictionary<string, Transform>();
                    if (cameraPositionLocatorsRoot != null)
                    {
                        Transform[] componentsInChildren = cameraPositionLocatorsRoot.GetComponentsInChildren<Transform>();
                        foreach (Transform transform in componentsInChildren)
                        {
                            _cameraPositionLocatorDict[transform.name] = transform;
                        }
                    }
                }
                return _cameraPositionLocatorDict;
            }
        }

        private Dictionary<string, Transform> cameraLookAtLocatorDict
        {
            get
            {
                if (_cameraLookAtLocatorDict == null)
                {
                    _cameraLookAtLocatorDict = new Dictionary<string, Transform>();
                    if (cameraLookAtLocatorsRoot != null)
                    {
                        Transform[] componentsInChildren = cameraLookAtLocatorsRoot.GetComponentsInChildren<Transform>();
                        foreach (Transform transform in componentsInChildren)
                        {
                            _cameraLookAtLocatorDict[transform.name] = transform;
                        }
                    }
                }
                return _cameraLookAtLocatorDict;
            }
        }

        public ILiveTimelineCharactorLocator[] liveCharactorLocators => _liveCharactorLocators;

        private ILiveTimelineCharactorLocator[] _liveCharactorLocators = new ILiveTimelineCharactorLocator[liveCharaPositionMax];

        public Vector3 liveStageCenterPos => _liveStageCenterPos;

        private Vector3 _liveStageCenterPos = Vector3.zero;

        private TimelinePlayerMode _playMode = TimelinePlayerMode.Default;

        public TimelinePlayerMode PlayMode => _playMode;

        public static int liveCharaPositionMax
        {
            get
            {
                if (_liveCharaPositionMax < 0)
                {
                    _liveCharaPositionMax = Enum.GetValues(typeof(LiveCharaPosition)).Length;
                }
                return _liveCharaPositionMax;
            }
        }

        public CacheCamera GetCamera(int index)
        {
            if (index < 0 || index >= _cameraArray.Length)
            {
                return null;
            }
            return _cameraArray[index];
        }

        private static int _liveCharaPositionMax = -1;

        private static bool availableFindKeyCache => true;

        private static Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> fnGetMultiCameraPositionValueFunc = GetMultiCameraPositionValue;

        private static Func<LiveTimelineKeyCameraLookAtData, LiveTimelineControl, Vector3, FindTimelineConfig, Vector3> fnGetMultiCameraLookAtValueFunc = GetMultiCameraLookAtValue;

        public void CopyValues<T>(T from, T to)
        {
            var json = JsonUtility.ToJson(from);
            JsonUtility.FromJsonOverwrite(json, to);
        }

        private void Awake()
        {
            InitializeTimeLineData();
            if (Director.instance)
            {
                Director.instance._liveTimelineControl = this;
                IsRecordVMD = Director.instance.IsRecordVMD;
            }
        }

        public void InitializeTimeLineData()
        {
            //var LoadData = gameObject.AddComponent<LiveTimelineData>();
            //CopyValues(data, LoadData);
            //foreach(LiveTimelineWorkSheet worksheet in LoadData.worksheetList)
            //{
            //    var LoadSheet = gameObject.AddComponent<LiveTimelineWorkSheet>();
            //    CopyValues(worksheet, LoadSheet);
            //}
        }

        public void InitCharaMotionSequence(int[] motionSequence)
        {
            //Create Animation
            foreach (var obj in Director.instance.charaObjs)
            {
                var container = obj.GetComponentInChildren<UmaContainer>();
                if (container)
                {
                    var animation = container.gameObject.AddComponent<Animation>();
                    Director.instance.charaAnims.Add(animation);
                }
            }

            //Get KeyArray
            var listCount = data.worksheetList[0].charaMotSeqList.Count;

            _keyArray = new LiveTimelineKeyCharaMotionSeqDataList[listCount];

            for (int i = 0; i < listCount; i++)
            {
                _keyArray[i] = data.worksheetList[0].charaMotSeqList[i].keys;
            }

            if (Director.instance.liveMode == 1)
            {
                //Set Motion
                int CharaPositionMax = Director.instance.allowCount;

                _motionSequenceArray = new LiveTimelineMotionSequence[CharaPositionMax];


                for (int i = 0; i < CharaPositionMax; i++)
                {
                    Debug.Log(i);
                    _motionSequenceArray[i] = new LiveTimelineMotionSequence();
                    _motionSequenceArray[i].Initialize(Director.instance.charaObjs[i], i, motionSequence[i], this);
                }
            }
            else if (Director.instance.liveMode == 0)
            {
                List<AnimationClip> Anims = new List<AnimationClip>();
                //Get MotionList
                foreach (var motion in UmaViewerMain.Instance.AbMotions.Where(a => a.Name.StartsWith($"3d/motion/live/body/son{Director.instance.live.MusicId}") && Path.GetFileName(a.Name).Split('_').Length == 4))
                {
                    AssetBundle motionAB = UmaAssetManager.LoadAssetBundle(motion);
                    AnimationClip motionAnim = motionAB.LoadAsset<AnimationClip>(Path.GetFileName(motion.Name).Split('.')[0]);
                    Anims.Add(motionAnim);
                }

                //Set Motion
                int CharaPositionMax = Director.instance.allowCount;

                _motionSequenceArray = new LiveTimelineMotionSequence[CharaPositionMax];


                for (int i = 0; i < CharaPositionMax; i++)
                {
                    _motionSequenceArray[i] = new LiveTimelineMotionSequence();
                    _motionSequenceArray[i].Initialize(Director.instance.charaObjs[i], i, motionSequence[i], this, Anims);
                }
            }

        }

        public void AlterUpdate(float liveTime)
        {
            _isNowAlterUpdate = true;
            _isMultiCameraEnable = false;
            _isNowAlterUpdate = true;
            _oldLiveTime = currentLiveTime;
            currentLiveTime = liveTime;
            _currentFrame = currentLiveTime * 60f;
            _oldFrame = _oldLiveTime * 60f;
            _deltaTime = currentLiveTime - _oldLiveTime;
            _deltaTimeRatio = _deltaTime / 0.0166666675f;
            AlterUpdate_CharaMotionSequence(liveTime);
            AlterUpdate_FacialData(liveTime);
            AlterUpdate_LipSync(liveTime);
            AlterUpdate_LipSync2(liveTime);
            _isNowAlterUpdate = false;
        }

        public void AlterLateUpdate()
        {
            if (data == null || data.worksheetList == null || data.worksheetList.Count == 0)
                return;

            LiveTimelineWorkSheet camSheet = data.worksheetList[0];

            _isNowAlterUpdate = true;

            Vector3 outLookAt = Vector3.zero;

            AlterLateUpdate_FormationOffset(currentLiveTime);
            AlterUpdate_CameraSwitcher(camSheet, _currentFrame);
            AlterUpdate_CameraPos(camSheet, _currentFrame);
            AlterUpdate_CameraLookAt(camSheet, _currentFrame, ref outLookAt);
            AlterUpdate_CameraFov(camSheet, _currentFrame);
            AlterUpdate_CameraRoll(camSheet, _currentFrame);
            AlterUpdate_MultiCamera(camSheet, _currentFrame);

            AlterUpdate_GlobalLight(camSheet, _currentFrame);
            AlterUpdate_EnvironmentMirror(camSheet, _currentFrame);
            AlterUpdate_MirrorReflection(camSheet, _currentFrame);
            AlterUpdate_HdrBloom(camSheet, Mathf.RoundToInt(_currentFrame));
            AlterUpdate_PostEffect_BloomDiffusion(camSheet, Mathf.RoundToInt(_currentFrame));

            AlterUpdate_BgColor1(camSheet, _currentFrame);
            AlterUpdate_BgColor1(camSheet, _currentFrame);
            _laserRuntimeIndexOffset = 0;
            int wsCount = data.worksheetList.Count;
            for (int w = 0; w < wsCount; w++)
            {
                var ws = data.worksheetList[w];
                if (ws == null) continue;

                AlterUpdate_TransformControl(ws, _currentFrame);
                AlterUpdate_ObjectControl(ws, _currentFrame);
                AlterUpdate_MobControl(ws, _currentFrame);
                AlterUpdate_CyalumeControl(ws, _currentFrame);
                AlterUpdate_BlinkLight(ws, _currentFrame);
                AlterUpdate_WashLight(ws, _currentFrame);
                AlterUpdate_Laser(ws, _currentFrame);
                AlterUpdate_UVScrollLight(ws, _currentFrame);
            }

            //BgColor2属于全局舞台颜色控制，只使用主 worksheet。
            //不遍历所有 worksheet,避免同名LaserA/LaserB轨道在同一帧互相覆盖。
            AlterUpdate_BgColor2(camSheet, _currentFrame);

            _isNowAlterUpdate = false;

            if (IsRecordVMD)
            {
                var currentFrame = Mathf.RoundToInt(_currentFrame);
                var oldFrame = Mathf.RoundToInt(_oldFrame);
                CacheCamera cacheCamera = GetCamera(camSheet.targetCameraIndex);
                if ((oldFrame == currentFrame && currentFrame > 0) || cacheCamera == null) return;

                LiveCameraFrame lastframe = RecordFrames.Count > 0 ? RecordFrames[RecordFrames.Count - 1] : null;

                var transform = cacheCamera.cacheTransform;
                var camera = cacheCamera.camera;
                LiveCameraFrame frame = new LiveCameraFrame(currentFrame, transform, camera.fieldOfView, lastframe);
                RecordFrames.Add(frame);

                for (int i = 0; i < MultiRecordFrames.Count; i++)
                {
                    var MulFrame = MultiRecordFrames[i];
                    LiveCameraFrame lastMulframe = MulFrame.Count > 0 ? MulFrame[MulFrame.Count - 1] : null;
                    MultiCamera MulCamera = _multiCamera[i];
                    var cam = MulCamera.GetCamera();
                    LiveCameraFrame mulframe = new LiveCameraFrame(currentFrame, MulCamera.transform, cam.fieldOfView, lastMulframe);
                    MulFrame.Add(mulframe);
                }

                if (currentFrame % 2 == 0) RecordUma?.Invoke(); //Set 30 FPS
            }
        }

        public void AlterUpdate_CharaMotionSequence(float liveTime)
        {
            foreach (var motion in _motionSequenceArray)
            {
                motion.AlterUpdate(liveTime, data.worksheetList[0].timescaleKeys);
            }
        }

        public void AlterUpdate_FacialData(float liveTime)
        {
            var facialDataList = data.worksheetList[0].facial1Set;
            FacialDataUpdateInfo updateInfo = default(FacialDataUpdateInfo);
            if (facialDataList != null)
            {
                SetupFacialUpdateInfo_Mouth(ref updateInfo, facialDataList.mouthKeys, liveTime);
                SetupFacialUpdateInfo_Eye(ref updateInfo, facialDataList.eyeKeys, liveTime);
                SetupFacialUpdateInfo_Eyebrow(ref updateInfo, facialDataList.eyebrowKeys, liveTime);
                SetupFacialUpdateInfo_Ear(ref updateInfo, facialDataList.earKeys, liveTime);
                SetupFacialUpdateInfo_EyeTrack(ref updateInfo, facialDataList.eyeTrackKeys, liveTime);
                SetupFacialUpdateInfo_Effect(ref updateInfo, facialDataList.effectKeys, liveTime);
                this.OnUpdateFacial(updateInfo, liveTime, 0);
            }

            var otherFacialDataList = data.worksheetList[0].other4FacialArray;
            for (int i = 0; i < otherFacialDataList.Length && i < Director.instance.characterCount - 1; i++)
            {
                SetupFacialUpdateInfo_Mouth(ref updateInfo, otherFacialDataList[i].mouthKeys, liveTime);
                SetupFacialUpdateInfo_Eye(ref updateInfo, otherFacialDataList[i].eyeKeys, liveTime);
                SetupFacialUpdateInfo_Eyebrow(ref updateInfo, otherFacialDataList[i].eyebrowKeys, liveTime);
                SetupFacialUpdateInfo_Ear(ref updateInfo, otherFacialDataList[i].earKeys, liveTime);
                SetupFacialUpdateInfo_EyeTrack(ref updateInfo, otherFacialDataList[i].eyeTrackKeys, liveTime);
                SetupFacialUpdateInfo_Effect(ref updateInfo, otherFacialDataList[i].effectKeys, liveTime);
                this.OnUpdateFacial(updateInfo, liveTime, i + 1);
            }
        }
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
        private void SetupFacialUpdateInfo_Mouth(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialMouthDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.mouthPrev = liveTimelineKey as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthCur = liveTimelineKey2 as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthNext = liveTimelineKey3 as LiveTimelineKeyFacialMouthData;
                updateInfo.mouthKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Eye(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyeDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyePrev = liveTimelineKey as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeCur = liveTimelineKey2 as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeNext = liveTimelineKey3 as LiveTimelineKeyFacialEyeData;
                updateInfo.eyeKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Effect(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEffectDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.key;

                updateInfo.effect = liveTimelineKey as LiveTimelineKeyFacialEffectData;
                updateInfo.effectKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Eyebrow(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyebrowDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyebrowPrev = liveTimelineKey as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowCur = liveTimelineKey2 as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowNext = liveTimelineKey3 as LiveTimelineKeyFacialEyebrowData;
                updateInfo.eyebrowKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_Ear(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEarDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.earPrev = liveTimelineKey as LiveTimelineKeyFacialEarData;
                updateInfo.earCur = liveTimelineKey2 as LiveTimelineKeyFacialEarData;
                updateInfo.earNext = liveTimelineKey3 as LiveTimelineKeyFacialEarData;
                updateInfo.earKeyIndex = curKey.index;
            }
        }

        private void SetupFacialUpdateInfo_EyeTrack(ref FacialDataUpdateInfo updateInfo, LiveTimelineKeyFacialEyeTrackDataList keys, float time)
        {
            LiveTimelineKey liveTimelineKey = null;
            LiveTimelineKey liveTimelineKey2 = null;
            LiveTimelineKey liveTimelineKey3 = null;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(keys, time);
            if (curKey != null)
            {
                liveTimelineKey = curKey.prevKey;
                liveTimelineKey2 = curKey.key;
                liveTimelineKey3 = curKey.nextKey;

                updateInfo.eyeTrackPrev = liveTimelineKey as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackCur = liveTimelineKey2 as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackNext = liveTimelineKey3 as LiveTimelineKeyFacialEyeTrackData;
                updateInfo.eyeTrackKeyIndex = curKey.index;
            }
        }

        public void AlterUpdate_LipSync(float liveTime)
        {
            var lipDataList = data.worksheetList[0].ripSyncKeys;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(lipDataList, liveTime);

            if (curKey != null && curKey.index != -1)
            {
                this.OnUpdateLipSync(curKey, liveTime);
            }
        }

        public void AlterUpdate_LipSync2(float liveTime)
        {
            var lipDataList = data.worksheetList[0].ripSync2Keys;

            LiveTimelineKeyIndex curKey = AlterUpdate_Key(lipDataList, liveTime);

            if (curKey != null && curKey.index != -1)
            {
                this.OnUpdateLipSync(curKey, liveTime);
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


        public Quaternion GetMultiCameraWorldRotation(int index)
        {
            if (_multiCameraCache == null || _multiCameraCache.Length <= index)
            {
                return Quaternion.identity;
            }
            return _multiCameraCache[index].cacheTransform.rotation;
        }

        public Vector3 GetMultiCameraWorldPosition(int index)
        {
            if (_multiCameraCache == null || _multiCameraCache.Length <= index)
            {
                return Vector3.zero;
            }
            return _multiCameraCache[index].cacheTransform.position;
        }


        public bool ExistsMultiCamera(int index)
        {
            if (_multiCameraCache != null && index < _multiCameraCache.Length)
            {
                return _multiCameraCache[index] != null;
            }
            return false;
        }

        public float GetCharacterHeight(LiveCharaPosition position)
        {
            return liveCharactorLocators[(int)position].liveCharaHeightValue;
        }

        public Vector3 GetPositionWithCharacters(LiveCharaPositionFlag posFlags, LiveCameraCharaParts parts, Vector3 charaPos, Vector3 cameraOffset)
        {
            Vector3 retPos = Vector3.zero;
            Vector3 tmpPos = Vector3.zero;
            if (posFlags == 0)
            {
                retPos = liveStageCenterPos;
                tmpPos = liveStageCenterPos;
            }
            else
            {
                int num = 0;
                switch (parts)
                {
                    case LiveCameraCharaParts.Face:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaHeadPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.Waist:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaWaistPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.LeftHandWrist:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaLeftHandWristPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.LeftHandAttach:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaLeftHandAttachPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.RightHandWrist:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaRightHandWristPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.RightHandAttach:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaRightHandAttachPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.Chest:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaChestPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.Foot:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos += liveCharactorLocators[i].liveCharaFootPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.ConstFaceHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos.y += liveCharactorLocators[i].liveCharaConstHeightHeadPosition.y;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += charaPos;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.ConstWaistHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos.y += liveCharactorLocators[i].liveCharaConstHeightWaistPosition.y;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += charaPos;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.ConstChestHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos.y += liveCharactorLocators[i].liveCharaConstHeightChestPosition.y;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += charaPos;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.ConstFootHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    retPos.y += liveCharactorLocators[i].liveCharaFootPosition.y;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += charaPos;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.InitFaceHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    /*
                                    retPos += liveCharactorLocators[i].liveCharaHeadPosition;
                                    tmpPos += liveCharactorLocators[i].liveCharaConstHeightHeadPosition;
                                    Vector3 vector3 = cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += vector3;
                                    tmpPos += vector3;
                                    num++;
                                    */

                                    retPos += liveCharactorLocators[i].liveCharaInitialHeightHeadPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.InitChestHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    /*
                                    retPos += liveCharactorLocators[i].liveCharaChestPosition;
                                    tmpPos += liveCharactorLocators[i].liveCharaConstHeightChestPosition;
                                    Vector3 vector2 = cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += vector2;
                                    tmpPos += vector2;
                                    num++;
                                    */

                                    retPos += liveCharactorLocators[i].liveCharaInitialHeightChestPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                    case LiveCameraCharaParts.InitWaistHeight:
                        {
                            for (int i = 0; i < liveCharaPositionMax; i++)
                            {
                                if (posFlags.hasFlag((LiveCharaPosition)i) && liveCharactorLocators[i] != null)
                                {
                                    /*
                                    retPos += liveCharactorLocators[i].liveCharaWaistPosition;
                                    tmpPos += liveCharactorLocators[i].liveCharaConstHeightWaistPosition;
                                    Vector3 vector = cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    retPos += vector;
                                    tmpPos += vector;
                                    num++;
                                    */

                                    retPos += liveCharactorLocators[i].liveCharaInitialHeightWaistPosition;
                                    retPos += cameraOffset * liveCharactorLocators[i].liveCharaHeightRatio;
                                    num++;
                                }
                            }
                            break;
                        }
                }
                bool flag = num > 1;
                if (flag)
                {
                    retPos /= (float)num;
                }
                /*
                if ((uint)(parts - 11) <= 2u)
                {
                    if (flag)
                    {
                        tmpPos /= (float)num;
                    }
                    retPos.y = tmpPos.y;
                }
                */
            }
            return retPos;
        }

        public Vector3 GetPositionWithCharacters(LiveCharaPositionFlag posFlags, LiveCameraCharaParts parts, Vector3 charaPos)
        {
            return GetPositionWithCharacters(posFlags, parts, charaPos, _cameraLayerOffset);
        }

        public static float CalculateInterpolationValue(LiveTimelineKey curKey, LiveTimelineKeyWithInterpolate nextKey, float frame)
        {
            float result = 0f;
            switch (nextKey.interpolateType)
            {
                case LiveCameraInterpolateType.Linear:
                    result = LinearInterpolateKeyframes(curKey, nextKey, frame);
                    break;
                case LiveCameraInterpolateType.Curve:
                    result = CurveInterpolateKeyframes(curKey, nextKey, frame);
                    break;
                case LiveCameraInterpolateType.Ease:
                    result = EaseInterpolateKeyframes(curKey, nextKey, frame);
                    break;
            }
            return result;
        }

        public void SetTimelineCamera(Camera cam, int index)
        {
            if (index < _cameraArray.Length)
            {
                if (_cameraArray[index] == null)
                {
                    _cameraArray[index] = new CacheCamera(cam);
                }
                else
                {
                    _cameraArray[index].Set(cam);
                }
                LiveTimelineCamera liveTimelineCamera = cam.gameObject.GetComponent<LiveTimelineCamera>();
                if (liveTimelineCamera == null)
                {
                    liveTimelineCamera = cam.gameObject.AddComponent<LiveTimelineCamera>();
                }
                _cameraScriptArray[index] = liveTimelineCamera;
                if (liveTimelineCamera != null)
                {
                    liveTimelineCamera.AlterAwake();
                }
            }
        }

        private void AlterUpdate_CameraSwitcher(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraSwitcherKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraSwitcherKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            LiveTimelineKey curKey = null;
            FindTimelineKeyCurrent(out curKey, sheet.cameraSwitcherKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraSwitcherData liveTimelineKeyCameraSwitcherData = curKey as LiveTimelineKeyCameraSwitcherData;
            if (this.OnUpdateCameraSwitcher != null)
            {
                this.OnUpdateCameraSwitcher(liveTimelineKeyCameraSwitcherData.cameraIndex);
            }
            else
            {
                if (liveTimelineKeyCameraSwitcherData.cameraIndex >= cameraArray.Length)
                {
                    return;
                }
                for (int i = 0; i < cameraArray.Length; i++)
                {
                    if (cameraArray[i] == null)
                    {
                        continue;
                    }
                    if (i == liveTimelineKeyCameraSwitcherData.cameraIndex)
                    {
                        if (!cameraArray[i].camera.enabled)
                        {
                            cameraArray[i].camera.enabled = true;
                            cameraScriptArray[i].enabled = true;
                        }
                    }
                    else if (cameraArray[i].camera.enabled)
                    {
                        cameraArray[i].camera.enabled = false;
                        cameraScriptArray[i].enabled = false;
                    }
                }
            }
        }

        private void AlterUpdate_CameraPos(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraPosKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraPosKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }

            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }

            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraPosKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData = curKey as LiveTimelineKeyCameraPositionData;
            camera.camera.nearClipPlane = liveTimelineKeyCameraPositionData.nearClip;
            camera.camera.farClipPlane = liveTimelineKeyCameraPositionData.farClip;
            if (CalculateCameraPos(out var pos, sheet, curKey, nextKey, currentFrame))
            {
                camera.cacheTransform.position = pos;

                // TODO �����õĻ���CGSS��layerö�٣�Ҫ�����滻
                /*
                int num = liveTimelineKeyCameraPositionData.GetCullingMask();
                if (num == 0)
                {
                    num = LiveTimelineKeyCameraPositionData.GetDefaultCullingMask();
                }
                camera.camera.cullingMask = num;
                if (OnUpdateCameraPos != null)
                {
                    CameraPosUpdateInfo updateInfo = default(CameraPosUpdateInfo);
                    updateInfo.outlineZOffset = liveTimelineKeyCameraPositionData.outlineZOffset;
                    updateInfo.characterLODMask = (int)liveTimelineKeyCameraPositionData.characterLODMask;
                    OnUpdateCameraPos(ref updateInfo);
                }
                */
            }
        }

        private static Vector3 GetCameraPosValue(LiveTimelineKeyCameraPositionData keyData, LiveTimelineControl timelineControl, FindTimelineConfig config)
        {
            return keyData.GetValue(timelineControl);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, float currentFrame)
        {
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = null;
            config.nextKey = null;
            config.keyType = FindTimelineConfig.KeyType.CurrentFrame;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = null;
            config.extraCameraIndex = 0;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            return CalculateCameraPos(out pos, sheet, currentFrame, camera, ref config, ref fnGetCameraPosValue);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, LiveTimelineKey curKey, LiveTimelineKey nextKey, float currentFrame)
        {
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = curKey;
            config.nextKey = nextKey;
            config.keyType = FindTimelineConfig.KeyType.KeyDirect;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = null;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            config.extraCameraIndex = 0;
            return CalculateCameraPos(out pos, sheet, currentFrame, camera, ref config, ref fnGetCameraPosValue);
        }

        public bool CalculateCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, float currentFrame, CacheCamera targetCamera, ref FindTimelineConfig config, ref Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> getFunc)
        {
            pos = Vector3.zero;
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            if (config.posKeys == null)
            {
                return false;
            }
            if (config.keyType == FindTimelineConfig.KeyType.CurrentFrame)
            {
                FindTimelineKey(out curKey, out nextKey, config.posKeys, currentFrame);
            }
            else
            {
                curKey = config.curKey;
                nextKey = config.nextKey;
            }
            if (curKey == null)
            {
                return false;
            }
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData = curKey as LiveTimelineKeyCameraPositionData;
            LiveTimelineKeyCameraPositionData liveTimelineKeyCameraPositionData2 = nextKey as LiveTimelineKeyCameraPositionData;
            if (liveTimelineKeyCameraPositionData2 != null && liveTimelineKeyCameraPositionData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraPositionData, liveTimelineKeyCameraPositionData2, currentFrame);
                int bezierPointCount = liveTimelineKeyCameraPositionData2.GetBezierPointCount();
                if (bezierPointCount == 0)
                {
                    pos = LerpWithoutClamp(getFunc(liveTimelineKeyCameraPositionData, this, config), getFunc(liveTimelineKeyCameraPositionData2, this, config), t);
                }
                else
                {
                    BezierCalcWork.cameraPos.Set(getFunc(liveTimelineKeyCameraPositionData, this, config), getFunc(liveTimelineKeyCameraPositionData2, this, config), bezierPointCount);
                    BezierCalcWork.cameraPos.UpdatePoints(liveTimelineKeyCameraPositionData2, this);
                    BezierCalcWork.cameraPos.Calc(bezierPointCount, t, out pos);
                }
            }
            else
            {
                pos = getFunc(liveTimelineKeyCameraPositionData, this, config);
            }
            if (_isNowAlterUpdate && liveTimelineKeyCameraPositionData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayEnable) && (_oldFrame >= liveTimelineKeyCameraPositionData.frame || currentFrame < liveTimelineKeyCameraPositionData.frame || liveTimelineKeyCameraPositionData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayInherit)))
            {
                if (targetCamera == null)
                {
                    return false;
                }
                float t2 = liveTimelineKeyCameraPositionData.traceSpeed * _deltaTimeRatio;
                pos = Vector3.Slerp(targetCamera.cacheTransform.position, pos, t2);
            }
            return true;
        }

        private void AlterUpdate_CameraLookAt(LiveTimelineWorkSheet sheet, float currentFrame, ref Vector3 outLookAt)
        {
            if (!sheet.cameraLookAtKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) && sheet.cameraLookAtKeys.EnablePlayModeTimeline(_playMode))
            {
                CacheCamera camera = GetCamera(sheet.targetCameraIndex);
                if (camera != null && CalculateCameraLookAt(out var lookAtPos, sheet, currentFrame))
                {
                    camera.cacheTransform.LookAt(lookAtPos, Vector3.up);
                    outLookAt = lookAtPos;
                }
            }
        }

        private static Vector3 GetCameraLookAtValue(LiveTimelineKeyCameraLookAtData keyData, LiveTimelineControl timelineControl, Vector3 camPos, FindTimelineConfig config)
        {
            return keyData.GetValue(timelineControl, camPos);
        }

        public bool CalculateCameraLookAt(out Vector3 lookAtPos, LiveTimelineWorkSheet sheet, float currentFrame)
        {
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = null;
            config.nextKey = null;
            config.keyType = FindTimelineConfig.KeyType.CurrentFrame;
            config.posKeys = sheet.cameraPosKeys;
            config.lookAtKeys = sheet.cameraLookAtKeys;
            config.extraCameraIndex = 0;
            return CalculateCameraLookAt(out lookAtPos, sheet, currentFrame, camera, ref config, ref fnGetCameraLookAtValue, ref fnGetCameraPosValue);
        }

        private bool CalculateCameraLookAt(out Vector3 lookAtPos, LiveTimelineWorkSheet sheet, float currentFrame, CacheCamera targetCamera, ref FindTimelineConfig config, ref Func<LiveTimelineKeyCameraLookAtData, LiveTimelineControl, Vector3, FindTimelineConfig, Vector3> getLookAtValueFunc, ref Func<LiveTimelineKeyCameraPositionData, LiveTimelineControl, FindTimelineConfig, Vector3> getPosValueFunc)
        {
            lookAtPos = Vector3.zero;
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null || config.lookAtKeys == null || config.posKeys == null)
            {
                return false;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, config.lookAtKeys, currentFrame);
            if (curKey == null)
            {
                return false;
            }
            Vector3 position = camera.cacheTransform.position;
            LiveTimelineKeyCameraLookAtData liveTimelineKeyCameraLookAtData = curKey as LiveTimelineKeyCameraLookAtData;
            LiveTimelineKeyCameraLookAtData liveTimelineKeyCameraLookAtData2 = nextKey as LiveTimelineKeyCameraLookAtData;
            if (liveTimelineKeyCameraLookAtData2 != null && liveTimelineKeyCameraLookAtData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraLookAtData, liveTimelineKeyCameraLookAtData2, currentFrame);
                int bezierPointCount = liveTimelineKeyCameraLookAtData2.GetBezierPointCount();
                if (bezierPointCount == 0)
                {
                    lookAtPos = LerpWithoutClamp(getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config), getLookAtValueFunc(liveTimelineKeyCameraLookAtData2, this, position, config), t);
                }
                else if (liveTimelineKeyCameraLookAtData2.necessaryToUseNewBezierCalcMethod)
                {
                    Vector3 zero = Vector3.zero;
                    BezierCalcWork.cameraLookAt.Set(getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config), getLookAtValueFunc(liveTimelineKeyCameraLookAtData2, this, zero, config), bezierPointCount);
                    BezierCalcWork.cameraLookAt.UpdatePoints(liveTimelineKeyCameraLookAtData2, this, zero);
                    BezierCalcWork.cameraLookAt.Calc(bezierPointCount, t, out lookAtPos);
                }
                else
                {
                    Vector3 zero2 = Vector3.zero;
                    Vector3 end = getLookAtValueFunc(liveTimelineKeyCameraLookAtData2, this, zero2, config);
                    Vector3 cp = liveTimelineKeyCameraLookAtData2.GetBezierPoint(0, this, zero2);
                    Vector3 cp2 = liveTimelineKeyCameraLookAtData2.GetBezierPoint(1, this, zero2);
                    Vector3 cp3 = liveTimelineKeyCameraLookAtData2.GetBezierPoint(2, this, zero2);
                    switch (liveTimelineKeyCameraLookAtData2.GetBezierPointCount())
                    {
                        default:
                            lookAtPos = LerpWithoutClamp(getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config), getLookAtValueFunc(liveTimelineKeyCameraLookAtData2, this, position, config), t);
                            break;
                        case 1:
                            {
                                Vector3 start3 = getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config);
                                BezierUtil.Calc(ref start3, ref end, ref cp, t, out lookAtPos);
                                break;
                            }
                        case 2:
                            {
                                Vector3 start2 = getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config);
                                BezierUtil.Calc(ref start2, ref end, ref cp, ref cp2, t, out lookAtPos);
                                break;
                            }
                        case 3:
                            {
                                Vector3 start = getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config);
                                BezierUtil.Calc(ref start, ref end, ref cp, ref cp2, ref cp3, t, out lookAtPos);
                                break;
                            }
                    }
                }
            }
            else
            {
                lookAtPos = getLookAtValueFunc(liveTimelineKeyCameraLookAtData, this, position, config);
            }
            if (_isNowAlterUpdate && liveTimelineKeyCameraLookAtData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayEnable) && (_oldFrame >= liveTimelineKeyCameraLookAtData.frame || currentFrame < liveTimelineKeyCameraLookAtData.frame || liveTimelineKeyCameraLookAtData.attribute.hasFlag(LiveTimelineKeyAttribute.CameraDelayInherit)))
            {
                Vector3 b = lookAtPos - camera.cacheTransform.position;
                float magnitude = b.magnitude;
                if (magnitude >= float.Epsilon)
                {
                    b /= magnitude;
                    float t2 = liveTimelineKeyCameraLookAtData.traceSpeed * _deltaTimeRatio;
                    lookAtPos = position + Vector3.Slerp(camera.cacheTransform.forward, b, t2) * magnitude;
                }
            }
            return true;
        }

        private void AlterUpdate_CameraFov(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraFovKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraFovKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraFovKeys, currentFrame);
            if (curKey == null)
            {
                return;
            }
            LiveTimelineKeyCameraFovData liveTimelineKeyCameraFovData = curKey as LiveTimelineKeyCameraFovData;
            LiveTimelineKeyCameraFovData liveTimelineKeyCameraFovData2 = nextKey as LiveTimelineKeyCameraFovData;
            float num = 80f;
            if (liveTimelineKeyCameraFovData2 != null && liveTimelineKeyCameraFovData2.interpolateType != 0)
            {
                float t = CalculateInterpolationValue(liveTimelineKeyCameraFovData, liveTimelineKeyCameraFovData2, currentFrame);
                num = LerpWithoutClamp(liveTimelineKeyCameraFovData.fov, liveTimelineKeyCameraFovData2.fov, t);
            }
            else if (liveTimelineKeyCameraFovData.fovType == LiveCameraFovType.Direct)
            {
                num = liveTimelineKeyCameraFovData.fov;
            }
            if (_limitFovForWidth)
            {
                float num2 = (float)camera.camera.pixelWidth / (float)camera.camera.pixelHeight;
                if (num2 > _baseCameraAspectRatio)
                {
                    float num3 = num2 / _baseCameraAspectRatio;
                    num /= num3;
                }
            }
            camera.camera.fieldOfView = num;
        }

        private void AlterUpdate_CameraRoll(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (sheet.cameraRollKeys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !sheet.cameraRollKeys.EnablePlayModeTimeline(_playMode))
            {
                return;
            }
            CacheCamera camera = GetCamera(sheet.targetCameraIndex);
            if (camera == null)
            {
                return;
            }
            LiveTimelineKey curKey = null;
            LiveTimelineKey nextKey = null;
            FindTimelineKey(out curKey, out nextKey, sheet.cameraRollKeys, currentFrame);
            if (curKey != null)
            {
                LiveTimelineKeyCameraRollData liveTimelineKeyCameraRollData = curKey as LiveTimelineKeyCameraRollData;
                LiveTimelineKeyCameraRollData liveTimelineKeyCameraRollData2 = nextKey as LiveTimelineKeyCameraRollData;
                float num = 80f;
                if (liveTimelineKeyCameraRollData2 != null && liveTimelineKeyCameraRollData2.interpolateType != 0)
                {
                    float t = CalculateInterpolationValue(liveTimelineKeyCameraRollData, liveTimelineKeyCameraRollData2, currentFrame);
                    num = LerpWithoutClamp(liveTimelineKeyCameraRollData.degree, liveTimelineKeyCameraRollData2.degree, t);
                }
                else
                {
                    num = liveTimelineKeyCameraRollData.degree;
                }
                camera.cacheTransform.Rotate(0f, 0f, num);
            }
        }

        //public void SetMultiCamera(MultiCameraManager manager, MultiCamera[] multiCamera)
        public void SetMultiCamera(MultiCamera[] multiCamera)
        {
            _multiCamera = multiCamera;
            //_multiCameraManager = manager;
            if (multiCamera != null)
            {
                _multiCameraCache = new CacheCamera[multiCamera.Length];
                for (int i = 0; i < multiCamera.Length; i++)
                {
                    _multiCameraCache[i] = new CacheCamera(multiCamera[i].GetCamera());
                }
            }
        }

        private static Vector3 GetMultiCameraPositionValue(LiveTimelineKeyCameraPositionData keyData, LiveTimelineControl timelineControl, FindTimelineConfig config)
        {
            return (keyData as LiveTimelineKeyMultiCameraPositionData).GetValue(timelineControl);
        }

        public bool CalculateMultiCameraPos(out Vector3 pos, LiveTimelineWorkSheet sheet, LiveTimelineKey curKey, LiveTimelineKey nextKey, float currentFrame, int timelineIndex)
        {
            if (sheet.multiCameraPosKeys.Count <= timelineIndex)
            {
                pos = Vector3.zero;
                return false;
            }
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = curKey;
            config.nextKey = nextKey;
            config.keyType = FindTimelineConfig.KeyType.KeyDirect;
            config.posKeys = sheet.multiCameraPosKeys[timelineIndex].keys;
            config.lookAtKeys = null;
            config.extraCameraIndex = timelineIndex;
            return CalculateCameraPos(out pos, sheet, currentFrame, _multiCameraCache[timelineIndex], ref config, ref fnGetMultiCameraPositionValueFunc);
        }

        private void AlterUpdate_MultiCameraPosition(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            int count = sheet.multiCameraPosKeys.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelineKeyMultiCameraPositionDataList keys = sheet.multiCameraPosKeys[i].keys;
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode) || i >= _multiCameraCache.Length)
                {
                    continue;
                }
                LiveTimelineKey curKey = null;
                LiveTimelineKey nextKey = null;
                FindTimelineKey(out curKey, out nextKey, keys, currentFrame);
                if (!CalculateMultiCameraPos(out var pos, sheet, curKey, nextKey, currentFrame, i))
                {
                    continue;
                }
                CacheCamera cacheCamera = _multiCameraCache[i];
                Camera camera = cacheCamera.camera;
                if (curKey == null)
                {
                    continue;
                }
                LiveTimelineKeyMultiCameraPositionData liveTimelineKeyMultiCameraPositionData = curKey as LiveTimelineKeyMultiCameraPositionData;
                LiveTimelineKeyMultiCameraPositionData liveTimelineKeyMultiCameraPositionData2 = nextKey as LiveTimelineKeyMultiCameraPositionData;
                // camera.enabled = liveTimelineKeyMultiCameraPositionData.enableMultiCamera;
                if (liveTimelineKeyMultiCameraPositionData.enableMultiCamera)
                {

                    _isMultiCameraEnable = true;
                    //camera.cullingMask = 0x10000000 | liveTimelineKeyMultiCameraPositionData.GetCullingMask();

                    float zAngle;
                    float fieldOfView;
                    Vector3 maskOffset;
                    float maskRoll;
                    if (liveTimelineKeyMultiCameraPositionData2 != null && liveTimelineKeyMultiCameraPositionData2.interpolateType != 0)
                    {
                        float t = CalculateInterpolationValue(liveTimelineKeyMultiCameraPositionData, liveTimelineKeyMultiCameraPositionData2, currentFrame);
                        fieldOfView = LerpWithoutClamp(liveTimelineKeyMultiCameraPositionData.fov, liveTimelineKeyMultiCameraPositionData2.fov, t);
                        maskOffset = LerpWithoutClamp(liveTimelineKeyMultiCameraPositionData.maskOffset, liveTimelineKeyMultiCameraPositionData2.maskOffset, t);
                        maskRoll = LerpWithoutClamp(liveTimelineKeyMultiCameraPositionData.maskRoll, liveTimelineKeyMultiCameraPositionData2.maskRoll, t);
                        zAngle = LerpWithoutClamp(liveTimelineKeyMultiCameraPositionData.roll, liveTimelineKeyMultiCameraPositionData2.roll, t);
                    }
                    else
                    {
                        fieldOfView = liveTimelineKeyMultiCameraPositionData.fov;
                        maskOffset = liveTimelineKeyMultiCameraPositionData.maskOffset;
                        maskRoll = liveTimelineKeyMultiCameraPositionData.maskRoll;
                        zAngle = liveTimelineKeyMultiCameraPositionData.roll;
                    }



                    camera.nearClipPlane = liveTimelineKeyMultiCameraPositionData.nearClip;
                    camera.farClipPlane = liveTimelineKeyMultiCameraPositionData.farClip;
                    camera.fieldOfView = fieldOfView;
                    cacheCamera.cacheTransform.localPosition = pos;
                    cacheCamera.cacheTransform.Rotate(0f, 0f, zAngle);
                    if (_multiCamera[i].maskIndex != (int)liveTimelineKeyMultiCameraPositionData.maskType)
                    {
                        //_multiCameraManager.AttachMask(i, (int)liveTimelineKeyMultiCameraPositionData.maskType);
                    }

                    if (_multiCamera[i].maskIndex >= 0)
                    {
                        _multiCamera[i].MaskOffset = maskOffset;
                        _multiCamera[i].MaskRoll = maskRoll;
                        //_multiCamera[i].maskScale = maskScale;
                    }
                }
            }
        }

        private static Vector3 GetMultiCameraLookAtValue(LiveTimelineKeyCameraLookAtData keyData, LiveTimelineControl timelineControl, Vector3 camPos, FindTimelineConfig config)
        {
            return (keyData as LiveTimelineKeyMultiCameraLookAtData).GetValue(timelineControl, camPos);
        }

        public bool CalculateMultiCameraLookAt(out Vector3 pos, LiveTimelineWorkSheet sheet, LiveTimelineKey curKey, LiveTimelineKey nextKey, float currentFrame, int timelineIndex = 0)
        {
            if (sheet.multiCameraPosKeys.Count <= timelineIndex || sheet.multiCameraLookAtKeys.Count <= timelineIndex)
            {
                pos = Vector3.zero;
                return false;
            }
            FindTimelineConfig config = default(FindTimelineConfig);
            config.curKey = curKey;
            config.nextKey = nextKey;
            config.keyType = FindTimelineConfig.KeyType.KeyDirect;
            config.posKeys = sheet.multiCameraPosKeys[timelineIndex].keys;
            config.lookAtKeys = sheet.multiCameraLookAtKeys[timelineIndex].keys;
            config.extraCameraIndex = timelineIndex;
            return CalculateCameraLookAt(out pos, sheet, currentFrame, _multiCameraCache[timelineIndex], ref config, ref fnGetMultiCameraLookAtValueFunc, ref fnGetMultiCameraPositionValueFunc);
        }


        private void AlterUpdate_MultiCameraLookAt(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            int count = sheet.multiCameraLookAtKeys.Count;
            for (int i = 0; i < count; i++)
            {
                LiveTimelineKeyMultiCameraLookAtDataList keys = sheet.multiCameraLookAtKeys[i].keys;
                if (i >= _multiCameraCache.Length)
                {
                    break;
                }
                if (keys.HasAttribute(LiveTimelineKeyDataListAttr.Disable) || !keys.EnablePlayModeTimeline(_playMode))
                {
                    continue;
                }
                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                if (curKey != null && CalculateMultiCameraLookAt(out var pos, sheet, curKey, nextKey, currentFrame, i))
                {
                    Transform cacheTransform = _multiCameraCache[i].cacheTransform;
                    cacheTransform.LookAt(pos);
                }
            }
        }

        private void AlterUpdate_MultiCamera(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            //if (_multiCameraCache != null && !(_multiCameraManager == null) && _multiCameraManager.isInitialize)
            if (_multiCameraCache != null)
            {
                AlterUpdate_MultiCameraPosition(sheet, currentFrame);
                if (_isMultiCameraEnable)
                    AlterUpdate_MultiCameraLookAt(sheet, currentFrame);
            }
        }

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
                else if (!validBgColorNames.Contains(sheet.bgColor1List[i].name))
                {
                    continue;
                }
                FindTimelineKey(out var curKey, out var nextKey, keys, currentFrame);
                if (curKey == null)
                {
                    continue;
                }
                BgColor1UpdateInfo updateInfo = default;
                LiveTimelineKeyBgColor1Data bgColorData = curKey as LiveTimelineKeyBgColor1Data;
                LiveTimelineKeyBgColor1Data bgColorData2 = nextKey as LiveTimelineKeyBgColor1Data;

                updateInfo.TimelineName = sheet.bgColor1List[i].name;
                updateInfo.TimelineNameHash = !string.IsNullOrEmpty(updateInfo.TimelineName) ? Animator.StringToHash(updateInfo.TimelineName) : 0;
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
                updateInfo.TimelineNameHash = !string.IsNullOrEmpty(bgData.name) ? Animator.StringToHash(bgData.name) : 0;
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
