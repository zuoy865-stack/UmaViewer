using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using Gallop.Live;

namespace Gallop.Live.Cutt
{
    public partial class LiveTimelineControl : MonoBehaviour
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
        private Vector3 _cameraPosLayerOffset = Vector3.zero;
        private Vector3 _cameraLookAtLayerOffset = Vector3.zero;
        private int _currentCameraPosKeyFrame;
        private int _currentCameraLookAtKeyFrame;
        private Vector3 _cameraLayerOffsetMin = Vector3.zero;
        private Vector3 _cameraLayerOffsetDiff = Vector3.zero;

        public Vector3 CameraLayerOffsetMin => _cameraLayerOffsetMin;
        public Vector3 CameraLayerOffsetDiff => _cameraLayerOffsetDiff;
        public int CurrentCameraPosKeyFrame => _currentCameraPosKeyFrame;
        public int CurrentCameraLookAtKeyFrame => _currentCameraLookAtKeyFrame;

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

        /// <summary>
        /// 注册指定站位角色的时间轴定位器（Locator）
        /// </summary>
        public void SetCharactorLocator(int index, ILiveTimelineCharactorLocator locator)
        {
            if (index < 0 || index >= _liveCharactorLocators.Length)
            {
                return;
            }

            if (locator == null)
            {
                _liveCharactorLocators[index] = null;
                return;
            }

            locator.liveCharaStandingPosition = (LiveCharaPosition)index;
            _liveCharactorLocators[index] = locator;
        }

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


        public void AlterUpdate(float liveTime)
        {
            LiveFrameProfiler.Begin(LiveFrameProfiler.AlterUpdate);

            _isNowAlterUpdate = true;
            _isMultiCameraEnable = false;
            _isNowAlterUpdate = true;
            _oldLiveTime = currentLiveTime;
            currentLiveTime = liveTime;
            _currentFrame = currentLiveTime * 60f;
            _oldFrame = _oldLiveTime * 60f;
            _deltaTime = currentLiveTime - _oldLiveTime;
            _deltaTimeRatio = _deltaTime / 0.0166666675f;

            LiveFrameProfiler.Begin(LiveFrameProfiler.MotionSequence);
            AlterUpdate_CharaMotionSequence(liveTime);
            LiveFrameProfiler.End(LiveFrameProfiler.MotionSequence);

            LiveFrameProfiler.Begin(LiveFrameProfiler.Facial);
            AlterUpdate_FacialData(liveTime);
            LiveFrameProfiler.End(LiveFrameProfiler.Facial);

            AlterUpdate_LipSync(liveTime);
            AlterUpdate_LipSync2(liveTime);

            // 道具（Props）不在这里驱动。
            // AlterLateUpdate 才是骨骼姿态解算完成之后的时间点，AlterUpdate_PropsControl /
            // AlterUpdate_PropsAttachControl 统一在那边按 worksheet 各跑一次即可。
            // 此前两处都调用，等于每帧对每个 worksheet 重复派发两遍道具更新
            // （订阅方 UpdateProps 会做 renderer.enabled、SetPropertyBlock、Animation.Sample），
            // 是 Live 掉帧的直接来源之一。

            _isNowAlterUpdate = false;

            LiveFrameProfiler.End(LiveFrameProfiler.AlterUpdate);
        }

        public void AlterLateUpdate()
        {
            if (data == null || data.worksheetList == null || data.worksheetList.Count == 0)
                return;

            LiveTimelineWorkSheet camSheet = data.worksheetList[0];

            LiveFrameProfiler.Begin(LiveFrameProfiler.AlterLateUpdate);

            _isNowAlterUpdate = true;

            Vector3 outLookAt = Vector3.zero;

            LiveFrameProfiler.Begin(LiveFrameProfiler.CameraGroup);
            AlterLateUpdate_FormationOffset(currentLiveTime);
            AlterUpdate_CameraSwitcher(camSheet, _currentFrame);
            AlterUpdate_CameraPos(camSheet, _currentFrame);
            AlterUpdate_CameraLookAt(camSheet, _currentFrame, ref outLookAt);

            // 官方Cutt算法：在基础机位与注视点计算完成后，在相机自身旋转局部空间施加身高层级微调偏移
            CacheCamera targetCamera = GetCamera(camSheet.targetCameraIndex);
            if (targetCamera != null && targetCamera.cacheTransform != null)
            {
                Transform cameraTransform = targetCamera.cacheTransform;
                cameraTransform.position += cameraTransform.rotation * _cameraPosLayerOffset;
                outLookAt += cameraTransform.rotation * _cameraLookAtLayerOffset;
                cameraTransform.LookAt(outLookAt, Vector3.up);
            }

            AlterUpdate_CameraRoll(camSheet, _currentFrame);
            AlterUpdate_CameraFov(camSheet, _currentFrame);
            // 调度多机位：分屏图层分割线、多机位各路相机位置与朝向
            AlterUpdate_MultiCamera(camSheet, _currentFrame);
            // 调度舞台监视器摄像机位置与注视点
            Director.instance?.UpdateMonitorCamera(camSheet, _currentFrame);
            LiveFrameProfiler.End(LiveFrameProfiler.CameraGroup);

            LiveFrameProfiler.Begin(LiveFrameProfiler.LightingGroup);
            AlterUpdate_GlobalLight(camSheet, _currentFrame);
            AlterUpdate_EnvironmentMirror(camSheet, _currentFrame);
            AlterUpdate_MirrorReflection(camSheet, _currentFrame);
            AlterUpdate_HdrBloom(camSheet, Mathf.RoundToInt(_currentFrame));
            AlterUpdate_PostEffect_BloomDiffusion(camSheet, Mathf.RoundToInt(_currentFrame));

            AlterUpdate_BgColor1(camSheet, _currentFrame);
            LiveFrameProfiler.End(LiveFrameProfiler.LightingGroup);

            _laserRuntimeIndexOffset = 0;
            int wsCount = data.worksheetList.Count;
            LiveFrameProfiler.Begin(LiveFrameProfiler.WorksheetLoop);
            for (int w = 0; w < wsCount; w++)
            {
                var ws = data.worksheetList[w];
                if (ws == null) continue;

                LiveFrameProfiler.Begin(LiveFrameProfiler.WsTransformObject);
                AlterUpdate_TransformControl(ws, _currentFrame);
                AlterUpdate_ObjectControl(ws, _currentFrame);
                LiveFrameProfiler.End(LiveFrameProfiler.WsTransformObject);

                LiveFrameProfiler.Begin(LiveFrameProfiler.WsMobCyalume);
                AlterUpdate_MobControl(ws, _currentFrame);
                AlterUpdate_CyalumeControl(ws, _currentFrame);
                LiveFrameProfiler.End(LiveFrameProfiler.WsMobCyalume);

                LiveFrameProfiler.Begin(LiveFrameProfiler.WsBlinkWash);
                AlterUpdate_BlinkLight(ws, _currentFrame);
                AlterUpdate_WashLight(ws, _currentFrame);
                LiveFrameProfiler.End(LiveFrameProfiler.WsBlinkWash);

                LiveFrameProfiler.Begin(LiveFrameProfiler.WsLaserUv);
                AlterUpdate_Laser(ws, _currentFrame);
                AlterUpdate_UVScrollLight(ws, _currentFrame);
                LiveFrameProfiler.End(LiveFrameProfiler.WsLaserUv);

                LiveFrameProfiler.Begin(LiveFrameProfiler.WsAnimProps);
                // 补充调用被遗漏的时间轴动画控制轨道驱动，使舞台动画数据能够被正常读取和派发
                AlterUpdate_AnimationControl(ws, Mathf.RoundToInt(_currentFrame));
                // 驱动道具属性、显隐与动画采样
                AlterUpdate_PropsControl(ws, _currentFrame, currentLiveTime);
                // 在骨骼姿态解算完成后，二次对齐挂载道具姿态，消除帧延迟
                AlterUpdate_PropsAttachControl(ws, _currentFrame);
                LiveFrameProfiler.End(LiveFrameProfiler.WsAnimProps);
            }
            LiveFrameProfiler.End(LiveFrameProfiler.WorksheetLoop);

            //BgColor2属于全局舞台颜色控制，只使用主 worksheet。
            //不遍历所有 worksheet,避免同名LaserA/LaserB轨道在同一帧互相覆盖。
            LiveFrameProfiler.Begin(LiveFrameProfiler.BgColor2);
            AlterUpdate_BgColor2(camSheet, _currentFrame);
            LiveFrameProfiler.End(LiveFrameProfiler.BgColor2);

            // 多机位已在上面 AlterUpdate_CameraRoll 之后调度过一次。
            // 这里再调一次读的是同一个 _currentFrame，结果完全相同，属于纯重复计算，故移除。

            _isNowAlterUpdate = false;

            LiveFrameProfiler.End(LiveFrameProfiler.AlterLateUpdate);

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

    }
}
