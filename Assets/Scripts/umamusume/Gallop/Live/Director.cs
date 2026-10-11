using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Gallop.ImageEffect;

namespace Gallop.Live
{
    public partial class Director : MonoBehaviour
    {
        private static Director _instance = null;
        public LiveTimelineControl _liveTimelineControl; //Edited to public
        [SerializeField]
        public float _liveCurrentTime;  //Edited to public
        public bool _isLiveSetup; //Edit to pulic
        public StageController _stageController; //Edited to public
        [SerializeField]
        private GameObject[] _cameraNodes;
        private Camera[] _cameraObjects;
        private Transform[] _cameraTransforms;
        [SerializeField]
        private CameraLookAt _cameraLookAt;
        private int _activeCameraIndex  = 1;
        private readonly int[] kTimelineCameraIndices = new int[3] { 1, 2, 3 };

        public static Director instance => _instance;

        //real work start
        public LiveEntry live;
        private const string CUTT_PATH = "cutt/cutt_son{0}/cutt_son{0}";
        private const string STAGE_PATH = "3d/env/live/live{0}/pfb_env_live{0}_controller000";
        private const string SONG_PATH = "sound/l/{0}/snd_bgm_live_{0}_oke_01";
        private const string VOCAL_PATH = "sound/l/{0}/snd_bgm_live_{0}_chara_{1}_01";
        private const string RANDOM_VOCAL_PATH = "sound/l/{0}/snd_bgm_live_{0}_chara";
        private const string LIVE_PART_PATH = "live/musicscores/m{0}/m{0}_part";

        private UmaViewerBuilder Builder => UmaViewerBuilder.Instance;

        public List<Transform> charaObjs;

        public List<UmaContainerCharacter> CharaContainerScript = new List<UmaContainerCharacter>();

        public List<Animation> charaAnims;
        public List<UmaViewerAudio.CuteAudioSource> liveVocal = new List<UmaViewerAudio.CuteAudioSource>();
        public UmaViewerAudio.CuteAudioSource liveMusic = new UmaViewerAudio.CuteAudioSource();

        public PartEntry partInfo;

        public bool _syncTime = false;
        public bool _soloMode = false;

        public int characterCount = 0;
        public int allowCount = 0;

        public int liveMode = 1;

        public LiveViewerUI UI;

        public float totalTime;

        public SliderControl sliderControl;

        public bool IsRecordVMD;

        public bool RequireStage = true;

        private bool _lateTimelineAppliedThisFrame;

        public Transform MainCameraTransform => _mainCameraTransform;

        /// <summary>
        /// 当前激活的 Live 主相机对象引用
        /// </summary>
        public Camera CurrentMainCamera
        {
            get
            {
                if (_cameraObjects != null && _activeCameraIndex >= 0 && _activeCameraIndex < _cameraObjects.Length)
                {
                    return _cameraObjects[_activeCameraIndex];
                }
                return Camera.main;
            }
        }

        private Transform _mainCameraTransform;

        // 角色色与全局光的 MaterialPropertyBlock 缓存在 Director.CharaColor.cs 中按 renderer 叠加维护。

        private static readonly Dictionary<string, UmaDatabaseEntry> _laserBundleCache
            = new Dictionary<string, UmaDatabaseEntry>();

        public bool isTimelineControlled
        {
            get
            {
                if (_liveTimelineControl != null)
                {
                    return _liveTimelineControl.data != null;
                }
                return false;
            }
        }

        public float CalcFrameJustifiedMusicTime()
        {
            if (isTimelineControlled)
            {
                return Mathf.RoundToInt(musicScoreTime * 60f) / 60f;
            }
            return musicScoreTime;
        }

        public float musicScoreTime => Mathf.Clamp(smoothMusicScoreTime, 0f, 99999f);

        private float smoothMusicScoreTime => _liveCurrentTime;//temp to liveCurrentTime

        public void Initialize()
        {
            if (live != null)
            {
                _instance = this;
                Debug.Log(string.Format(CUTT_PATH, live.MusicId));
                Builder.LoadAssetPath(string.Format(CUTT_PATH, live.MusicId), transform);
                if (RequireStage)
                {
                    Debug.Log(live.BackGroundId);

                    string stagePath = string.Format(STAGE_PATH, live.BackGroundId);
                    if (UmaViewerMain.Instance.AbList.TryGetValue(stagePath, out var stageEntry) &&
                        !UmaAssetManager.Exist(stageEntry))
                    {
                        // 正常从 LoadLive 进入时已经异步预载；这里仅作为其他入口的同步兜底。
                        PreloadStageBundlesBeforeInstantiate(live.BackGroundId);
                    }

                    Builder.LoadAssetPath(stagePath, transform);
                    

                    _liveTimelineControl.StageObjectMap = _stageController.StageObjectMap;
                }


                //Make CharacterObject

                var characterStandPos = _liveTimelineControl.transform.Find("CharacterStandPos");
                int counter = 0;
                var standPos = characterStandPos.GetComponentsInChildren<Transform>();
                var count = _liveTimelineControl.data.characterSettings.useHighPolygonModel.Length;
                for (int i = 0; i < count; i++)
                {
                    if (i < characterStandPos.childCount)
                    {
                        var newObj = Instantiate(standPos[i + 1], transform);
                        newObj.gameObject.name = string.Format("CharacterObject{0}", counter);
                        charaObjs.Add(newObj.transform);
                        counter++;
                    }
                    else
                    {
                        var newObj = Instantiate(standPos[i % characterStandPos.childCount + 1], transform);
                        newObj.gameObject.name = string.Format("CharacterObject{0}", counter);
                        charaObjs.Add(newObj.transform);
                        counter++;
                    }
                };


                //Get live parts info
                UmaDatabaseEntry partAsset = UmaViewerMain.Instance.AbList[string.Format(LIVE_PART_PATH, live.MusicId)];
                UmaViewerAudio.LastAudioPartIndex = -1;

                Debug.Log(partAsset.Name);

                AssetBundle bundle = UmaAssetManager.LoadAssetBundle(partAsset);
                TextAsset partData = bundle.LoadAsset<TextAsset>($"m{live.MusicId}_part");
                partInfo = new PartEntry(partData.text);

            }

        }

        public void InitializeUI()
        {
            UI = GameObject.Find("LiveUI").GetComponent<LiveViewerUI>();

            sliderControl = UI.ProgressBar.GetComponent<SliderControl>();
            LiveViewerUI.Instance.RecordingUI.SetActive(IsRecordVMD);
            LiveViewerUI.Instance.RecordingText.text = $"�� Recording...\r\n VMD will be saved in {Path.GetFullPath(Application.dataPath + UnityHumanoidVMDRecorder.FileSavePath)}";
        }

        public void InitializeTimeline(List<LiveCharacterLoadData> characters, int mode)
        {
            totalTime = _liveTimelineControl.data.timeLength;

            liveMode = mode;

            allowCount = characters.Count;

            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i].CharaEntry.Name != "")
                {
                    characterCount += 1;
                }
            }
            if (characterCount == 1)
            {
                _soloMode = true;
            }

            _liveTimelineControl.InitCharaMotionSequence(_liveTimelineControl.data.characterSettings.motionSequenceIndices);

            _liveTimelineControl.OnUpdateLipSync += delegate (LiveTimelineKeyIndex keyData_, float liveTime_)
            {
                var prevKey = keyData_.prevKey as LiveTimelineKeyLipSyncData;
                var curKey = keyData_.key as LiveTimelineKeyLipSyncData;
                var nextKey = keyData_.nextKey as LiveTimelineKeyLipSyncData;
                for (int k = 0; k < charaObjs.Count; k++)
                {
                    if (k < CharaContainerScript.Count)
                    {
                        var container = CharaContainerScript[k];
                        container.FaceDrivenKeyTarget.AlterUpdateAutoLip(prevKey, curKey, liveTime_, ((int)curKey.character >> k) % 2);
                    }
                }
            };

            _liveTimelineControl.OnUpdateFacial += delegate (FacialDataUpdateInfo updateInfo_, float liveTime_, int position)
            {
                // 加固边界防御与有效容器断言，彻底杜绝高序号空槽位越界和空指针报错
                if (position >= 0 && position < CharaContainerScript.Count && CharaContainerScript[position] != null)
                {
                    var container = CharaContainerScript[position];
                    if (container.FaceDrivenKeyTarget != null)
                    {
                        container.FaceDrivenKeyTarget.AlterUpdateFacialNew(ref updateInfo_, liveTime_);
                    }
                }
            };

            _liveTimelineControl.OnUpdateGlobalLight += OnUpdateGlobalLight;
            _liveTimelineControl.OnUpdateBgColor1 += OnUpdateBgColor1;

            SetupCharacterLocator();
            // 在参演马娘组装与定位器就绪后，初始化全局手持道具装配
            InitializeLiveProps();
            InitializeCamera();
            InitializeMirrorReflections();
            UpdateMainCamera();
            InitializeMultiCamera(_liveTimelineControl);
            InitializeMonitorCamera(_liveTimelineControl);
            for (int i = 0; i < kTimelineCameraIndices.Length; i++)
            {
                int num = kTimelineCameraIndices[i];
                if (num < _cameraObjects.Length)
                {
                    _liveTimelineControl.SetTimelineCamera(_cameraObjects[num], i);
                }
            }
            _liveTimelineControl.OnUpdatePostEffect_BloomDiffusion += OnUpdatePostEffect_BloomDiffusion;
            _liveTimelineControl.OnUpdateHdrBloom += OnUpdateHdrBloom;


            _liveTimelineControl.OnUpdateCameraSwitcher += delegate (int cameraIndex_)
            {
                if (cameraIndex_ < 0)
                {
                    _activeCameraIndex = 0;
                }
                else if (cameraIndex_ < kTimelineCameraIndices.Length)
                {
                    _activeCameraIndex = kTimelineCameraIndices[cameraIndex_];
                }
            };
            
        }

        public void InitializeCamera()
        {
            if (_cameraObjects == null)
            {
                _cameraObjects = new Camera[_cameraNodes.Length + 1];
                _cameraTransforms = new Transform[_cameraNodes.Length + 1];
                for (int i = 0; i < _cameraNodes.Length; i++)
                {
                    GameObject gameObject = _cameraNodes[i];
                    Camera camera = gameObject.GetComponent<Camera>();
                    if (camera == null)
                    {
                        camera = gameObject.GetComponentInChildren<Camera>();
                    }
                    //camera.cullingMask = num;
                    _cameraObjects[i] = camera;
                    _cameraTransforms[i] = camera.transform;
                }
            }
        }

        // InitializeMultiCamera 已迁移至 Director.MultiCamera.cs 分部类实现增强版本


        private void UpdateMainCamera()
        {
            if (_cameraObjects == null) return;
            for (int i = 0; i < _cameraNodes.Length; i++)
            {
                bool activeSelf = _cameraNodes[i].activeSelf;
                bool flag = i == _activeCameraIndex;
                _cameraNodes[i].SetActive(flag);
                if (i == 0 && activeSelf != flag && flag && _cameraLookAt != null)
                {
                    _cameraLookAt.ActivationUpdate();
                }
            }
            _mainCameraTransform = _cameraTransforms[_activeCameraIndex];
        }

        private void SetupCharacterLocator()
        {
            if (!_liveTimelineControl) return;
            int count = Mathf.Min(CharaContainerScript.Count, 20);
            for (int i = 0; i < count; i++)
            {
                var container = CharaContainerScript[i];
                if (container == null) continue;

                if (container.LiveLocator == null)
                {
                    container.LiveLocator = new LiveTimelineCharaLocator(container);
                }
                container.LiveLocator.liveCharaStandingPosition = (LiveCharaPosition)i;
                _liveTimelineControl.SetCharactorLocator(i, container.LiveLocator);
            }

            for (int i = count; i < 20; i++)
            {
                _liveTimelineControl.SetCharactorLocator(i, null);
            }
        }

        public void InitializeMusic(int songid, List<LiveCharacterLoadData> characters)
        {

            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i].CharaEntry.Name != "" && i < partInfo.SingerCount)
                {
                    var charaid = characters[i].CharaEntry.Id;

                    var entry = UmaViewerMain.Instance.AbSounds.FirstOrDefault(a => a.Name.Contains(string.Format(VOCAL_PATH, songid, charaid)) && a.Name.EndsWith("awb"));
                    if (entry == null)
                    {
                        List<UmaDatabaseEntry> entries = new List<UmaDatabaseEntry>();
                        foreach (var random in UmaViewerMain.Instance.AbSounds.Where(a => (a.Name.Contains(string.Format(RANDOM_VOCAL_PATH, songid)) && a.Name.EndsWith("awb"))))
                        {
                            entries.Add(random);
                        }
                        if (entries.Count > 0)
                        {
                            entry = entries[UnityEngine.Random.Range(0, entries.Count - 1)];
                        }
                    }

                    if (entry != null)
                    {
                        Debug.Log(entry.Name);
                        liveVocal.Add(UmaViewerAudio.ApplySound(entry.Name.Split('.')[0], i));
                    }
                }
            }


            liveMusic = UmaViewerAudio.ApplySound(string.Format(SONG_PATH, songid), -1);

            // 伴奏音轨缺失检测：当伴奏音轨未下载或列表为空时，通过全局弹窗告知用户，杜绝静默无声播放
            if (liveMusic == null || liveMusic.sourceList == null || liveMusic.sourceList.Count == 0)
            {
                UmaErrorManager.ShowUIMessage(
                    string.Format("未找到伴奏音轨（MusicId: {0}），请检查伴奏资源或网络下载。", live != null ? live.MusicId : songid),
                    UIMessageType.Error);
            }
        }

        public void Play()
        {

            foreach (var vocal in liveVocal)
            {
                UmaViewerAudio.Play(vocal);
            }
            UmaViewerAudio.Play(liveMusic);

            _isLiveSetup = true;
            _liveCurrentTime = 0;

            if (IsRecordVMD)
            {
                foreach (var container in CharaContainerScript)
                {
                    var rootbone = container.transform.Find("Position");
                    var newRecorder = rootbone.gameObject.AddComponent<UnityHumanoidVMDRecorder>();
                    newRecorder.UseParentOfAll = true;
                    newRecorder.UseAbsoluteCoordinateSystem = true;
                    newRecorder.Initialize();
                    if (!newRecorder.IsRecording)
                    {
                        newRecorder.StartRecording(true);
                    }
                }
            }
        }

        private void OnTimelineUpdate(float _liveCurrentTime)
        {
            _liveTimelineControl.AlterUpdate(_liveCurrentTime);
            if (!_soloMode)
            {
                LiveFrameProfiler.Begin(LiveFrameProfiler.AudioVocal);
                UmaViewerAudio.AlterUpdate(_liveCurrentTime, partInfo, liveVocal, sliderControl.is_Outed);
                LiveFrameProfiler.End(LiveFrameProfiler.AudioVocal);
            }
        }

        private void ApplyTimelineLateUpdate()
        {
            if (_lateTimelineAppliedThisFrame || _liveTimelineControl == null)
                return;

            _liveTimelineControl.AlterLateUpdate();
            _lateTimelineAppliedThisFrame = true;
        }

        bool isExit;
        void Update()
        {
            if (isExit) return;

            if (_isLiveSetup)
            {
                LiveFrameProfiler.BeginFrame();

                _lateTimelineAppliedThisFrame = false;

                if ((!UmaViewerMain.TryConsumeEscapeForFullScreen() && Input.GetKeyDown(KeyCode.Escape)) || _liveCurrentTime >= totalTime)
                {
                    ExitLive();
                }

                if (_syncTime == false)
                {
                    // 伴奏列表判空保护，杜绝 NullReferenceException
                    if (liveMusic == null || liveMusic.sourceList == null || liveMusic.sourceList.Count == 0)
                    {
                        _syncTime = true;
                    }
                    else if (liveMusic.sourceList[0] != null && liveMusic.sourceList[0].time > 0.01f)
                    {
                        _liveCurrentTime = UI.ProgressBar.value * totalTime;
                        _liveCurrentTime = Mathf.Clamp(_liveCurrentTime, 0f, Mathf.Max(0f, totalTime - 0.001f));
                        _liveCurrentTime = liveMusic.sourceList[0].time;
                        _syncTime = true;
                    }
                }
                else
                {
                    if (IsRecordVMD)
                    {
                        _liveCurrentTime += (1 / 60f);
                        if (liveMusic != null)
                        {
                            UmaViewerAudio.Stop(liveMusic);
                            foreach (var vocal in liveVocal)
                            {
                                UmaViewerAudio.Stop(vocal);
                            }
                        }

                        UI.ProgressBar.SetValueWithoutNotify(_liveCurrentTime / totalTime);
                        OnTimelineUpdate(_liveCurrentTime);
                        ApplyTimelineLateUpdate();
                    }
                    else if (sliderControl.is_Outed)
                    {
                        _liveCurrentTime = UI.ProgressBar.value * totalTime;

                        if (liveMusic != null)
                        {
                            UmaViewerAudio.SetTime(liveMusic, _liveCurrentTime);

                            foreach (var vocal in liveVocal)
                            {
                                UmaViewerAudio.SetTime(vocal, _liveCurrentTime);
                            }

                            UmaViewerAudio.Play(liveMusic);

                            foreach (var vocal in liveVocal)
                            {
                                UmaViewerAudio.Play(vocal);
                            }
                        }

                        OnTimelineUpdate(_liveCurrentTime);
                        ApplyTimelineLateUpdate();

                        sliderControl.is_Outed = false;
                        sliderControl.is_Touched = false;
                        _syncTime = false;
                    }
                    else if (sliderControl.is_Touched)
                    {
                        _liveCurrentTime = UI.ProgressBar.value * totalTime;

                        if (liveMusic != null)
                        {
                            UmaViewerAudio.Stop(liveMusic);
                            foreach (var vocal in liveVocal)
                            {
                                UmaViewerAudio.Stop(vocal);
                            }
                        }

                        OnTimelineUpdate(_liveCurrentTime);
                        ApplyTimelineLateUpdate();
                    }
                    else
                    {
                        _liveCurrentTime += Time.deltaTime;
                        UI.ProgressBar.SetValueWithoutNotify(_liveCurrentTime / totalTime);
                        OnTimelineUpdate(_liveCurrentTime);
                        ApplyTimelineLateUpdate();
                    }
                }

                UpdateMainCamera();

                // 时间轴和主相机都更新完后再同步 Laser Renderer/朝向。
                // 这样既不会读取上一帧 LaserUpdateInfo，也不会读取上一帧相机姿态。
                if (_stageController != null)
                {
                    LiveFrameProfiler.Begin(LiveFrameProfiler.StageController);
                    _stageController.AlterUpdateLaserControllers();
                    LiveFrameProfiler.End(LiveFrameProfiler.StageController);
                }

                LiveFrameProfiler.EndFrame();
            }
        }

        private void LateUpdate()
        {
            if (_isLiveSetup && _syncTime && !IsRecordVMD)
            {
                ApplyTimelineLateUpdate();
            }
            
            if (_enableMirrorReflection && _mirrorRenderInLateUpdate)
            {
                UpdateMirrorReflections();
            }

            // 驱动 URP 兼容的多机位全屏分屏呈现层更新
            UpdateMultiCameraDisplay();
        }

        private void FixedUpdate()
        {
            LiveViewerUI.Instance.UpdateLyrics(_liveCurrentTime);
        }

        public static List<UmaDatabaseEntry> GetLiveAllVoiceEntry(int songid, List<LiveCharacterLoadData> characters)
        {
            List<UmaDatabaseEntry> entryList = new List <UmaDatabaseEntry>();
            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i].CharaEntry.Name != "")
                {
                    var charaid = characters[i].CharaEntry.Id;

                    var entry = UmaViewerMain.Instance.AbSounds.FirstOrDefault(a => a.Name.Contains(string.Format(VOCAL_PATH, songid, charaid)) && a.Name.EndsWith("awb"));
                    if (entry == null)
                    {
                        List<UmaDatabaseEntry> entries = new List<UmaDatabaseEntry>();
                        foreach (var random in UmaViewerMain.Instance.AbSounds.Where(a => (a.Name.Contains(string.Format(RANDOM_VOCAL_PATH, songid)) && a.Name.EndsWith("awb"))))
                        {
                            entries.Add(random);
                        }
                        if (entries.Count > 0)
                        {
                            entry = entries[UnityEngine.Random.Range(0, entries.Count - 1)];
                        }
                    }

                    if (entry != null)
                    {
                        entryList.Add(entry);
                    }
                }
            }

            var bgEntry = UmaViewerMain.Instance.AbSounds.FirstOrDefault(a => a.Name.Contains(string.Format(SONG_PATH, songid)) && a.Name.EndsWith("awb"));
            if (bgEntry != null)
            {
                entryList.Add(bgEntry);
            }
            return entryList;
        }
        public static List<UmaDatabaseEntry> GetLivePreloadEntries(
            LiveEntry live,
            List<LiveCharacterLoadData> characters,
            bool requireStage)
        {
            var result = new List<UmaDatabaseEntry>();
            if (live == null)
                return result;

            result.AddRange(GetLiveAllVoiceEntry(live.MusicId, characters));

            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return result;

            void AddByKey(string key)
            {
                if (main.AbList.TryGetValue(key, out var entry) && entry != null)
                    result.Add(entry);
            }

            // Cutt 和歌曲 part 也提前加载，避免进入场景后同步卡顿。
            AddByKey(string.Format(CUTT_PATH, live.MusicId));
            AddByKey(string.Format(LIVE_PART_PATH, live.MusicId));
            AddByKey("livesettings");

            // 预加载当前 Live 所需的角色道具与道具动作资源
            result.AddRange(CollectLivePropsEntries(live));

            // 补全当前 Live 专属动作资源预载：
            // 遍历 main.AbList，匹配收集 3d/motion/live/body/son{live.MusicId} 的所有动作包，
            // 并同时支持收集 3d/motion/live/cutt/son{live.MusicId}（如果存在）。
            // 确保进入 Live 场景前动作资源包已被全部加入预载列表，杜绝漏加载引发的角色 T-pose。
            if (live.MusicId > 0)
            {
                string bodyMotionPrefix = $"3d/motion/live/body/son{live.MusicId}";
                string cuttMotionPrefix = $"3d/motion/live/cutt/son{live.MusicId}";

                foreach (var kv in main.AbList)
                {
                    UmaDatabaseEntry entry = kv.Value;
                    if (entry == null || !entry.IsAssetBundle)
                        continue;

                    // 检查资源 key 相对路径以及 entry.Name 是否匹配该歌曲的动作包前缀
                    bool isBodyMotion = (kv.Key != null && kv.Key.StartsWith(bodyMotionPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                       (entry.Name != null && entry.Name.StartsWith(bodyMotionPrefix, StringComparison.OrdinalIgnoreCase));
                    bool isCuttMotion = (kv.Key != null && kv.Key.StartsWith(cuttMotionPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                       (entry.Name != null && entry.Name.StartsWith(cuttMotionPrefix, StringComparison.OrdinalIgnoreCase));

                    if (isBodyMotion || isCuttMotion)
                    {
                        result.Add(entry);
                    }
                }
            }

            if (requireStage)
            {
                result.AddRange(CollectStageBundleEntries(live, requireStage));
            }

            return result
                .Where(e => e != null && !string.IsNullOrEmpty(e.Name))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// 收集舞台所需的全部 AssetBundle 资源条目（涵盖专属预制体模型、sourceresources 下的材质贴图包、common 公共天空/荧光棒部件，以及歌曲专属 UVMovie 视频包）
        /// </summary>
        public static List<UmaDatabaseEntry> CollectStageBundleEntries(LiveEntry live, bool requireStage)
        {
            var result = new List<UmaDatabaseEntry>();
            if (!requireStage || live == null)
                return result;

            // 若既没有背景舞台 ID 也没有有效歌曲 ID，则无需收集任何舞台或视频资源
            if (string.IsNullOrEmpty(live.BackGroundId) && live.MusicId <= 0)
                return result;

            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return result;

            string bgId = live.BackGroundId;
            bool hasBg = !string.IsNullOrEmpty(bgId);

            // 1. 舞台专属资源路径前缀（模型、预制体、控制器等）
            string folderPrefix = hasBg ? $"3d/env/live/live{bgId}/" : null;
            // 2. 舞台专属材质/贴图资源前缀（确保 10147 等舞台天空网格材质 mtl_env_live10147_sky000~014 等全部被加载）
            string sourcePrefix = hasBg ? $"sourceresources/3d/env/live/live{bgId}/" : null;
            // 3. 舞台公共部件前缀（如公共天空球 pfb_env_live_cmn_sky002、公共荧光棒控制器等）
            string commonPrefix = hasBg ? "3d/env/live/common/" : null;
            // 3.1 舞台公共材质与贴图资源前缀（如公共天空材质 mtl_env_live_cmn_sky*、云层材质 sky_cloud 等）
            string commonSourcePrefix = hasBg ? "sourceresources/3d/env/live/common/" : null;
            // 4. 歌曲对应 UVMovie 视频资源前缀（如 1175 的 39 个 gal_uvmovie_1175_001 及分镜纹理包）
            string uvMoviePrefix = live.MusicId > 0 ? $"live/uvmovie/gal_uvmovie_{live.MusicId}" : null;

            foreach (var kv in main.AbList)
            {
                UmaDatabaseEntry entry = kv.Value;
                if (entry == null || !entry.IsAssetBundle)
                    continue;

                // 分别检查 key（资源相对路径）和 entry.Name 是否匹配任一舞台、公共材质或 UVMovie 资源前缀
                bool keyMatches = (folderPrefix != null && kv.Key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (sourcePrefix != null && kv.Key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (commonPrefix != null && kv.Key.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (commonSourcePrefix != null && kv.Key.StartsWith(commonSourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (uvMoviePrefix != null && kv.Key.StartsWith(uvMoviePrefix, StringComparison.OrdinalIgnoreCase));

                bool nameMatches = entry.Name != null && (
                    (folderPrefix != null && entry.Name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (sourcePrefix != null && entry.Name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (commonPrefix != null && entry.Name.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (commonSourcePrefix != null && entry.Name.StartsWith(commonSourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (uvMoviePrefix != null && entry.Name.StartsWith(uvMoviePrefix, StringComparison.OrdinalIgnoreCase)));

                if (keyMatches || nameMatches)
                    result.Add(entry);
            }

            return result;
        }

        /// <summary>
        /// 在实例化舞台前同步预载舞台与 UVMovie 资源的兜底方法（用于非标准 LoadLive 入口）
        /// </summary>
        private void PreloadStageBundlesBeforeInstantiate(string bgId)
        {
            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return;

            // 获取当前 Director 实例的 live 数据以提取歌曲对应的 UVMovie 前缀
            LiveEntry currentLive = live ?? instance?.live;
            string uvMoviePrefix = (currentLive != null && currentLive.MusicId > 0)
                ? $"live/uvmovie/gal_uvmovie_{currentLive.MusicId}"
                : null;

            bool hasBg = !string.IsNullOrEmpty(bgId);
            if (!hasBg && string.IsNullOrEmpty(uvMoviePrefix))
                return;

            // 同步兜底路径匹配专属舞台、sourceresources 材质包、common 公共部件、公共材质包以及当前歌曲的 UVMovie
            string folderPrefix = hasBg ? $"3d/env/live/live{bgId}/" : null;
            string sourcePrefix = hasBg ? $"sourceresources/3d/env/live/live{bgId}/" : null;
            string commonPrefix = hasBg ? "3d/env/live/common/" : null;
            string commonSourcePrefix = hasBg ? "sourceresources/3d/env/live/common/" : null;
            var required = new List<UmaDatabaseEntry>();

            foreach (var kv in main.AbList)
            {
                UmaDatabaseEntry entry = kv.Value;
                if (entry == null || !entry.IsAssetBundle)
                    continue;

                // 同样匹配舞台专属模型、材质包、公共部件、公共材质包以及当前歌曲的 UVMovie 资源
                bool keyMatches = (folderPrefix != null && kv.Key.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (sourcePrefix != null && kv.Key.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (commonPrefix != null && kv.Key.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (commonSourcePrefix != null && kv.Key.StartsWith(commonSourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                                  (uvMoviePrefix != null && kv.Key.StartsWith(uvMoviePrefix, StringComparison.OrdinalIgnoreCase));

                bool nameMatches = entry.Name != null && (
                    (folderPrefix != null && entry.Name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (sourcePrefix != null && entry.Name.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (commonPrefix != null && entry.Name.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (commonSourcePrefix != null && entry.Name.StartsWith(commonSourcePrefix, StringComparison.OrdinalIgnoreCase)) ||
                    (uvMoviePrefix != null && entry.Name.StartsWith(uvMoviePrefix, StringComparison.OrdinalIgnoreCase)));

                if (keyMatches || nameMatches)
                    required.AddRange(UmaAssetManager.SearchAB(main, entry));
            }

            // 兜底路径也只做“新增加载”，不调用任何 Unload；依赖去重后每个只处理一次。
            foreach (UmaDatabaseEntry entry in required
                         .Where(e => e != null && !string.IsNullOrEmpty(e.Name))
                         .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                UmaAssetManager.LoadAssetBundle(
                    entry,
                    neverUnload: false,
                    isRecursive: false);
            }

            Debug.Log($"[StagePreloadFallback] bgId={bgId}, musicId={currentLive?.MusicId}, bundles={required.Count}");
        }
        /// <summary>
        /// 安全停止并释放正在播放的伴奏与角色人声音轨
        /// </summary>
        public void CleanupAudio()
        {
            if (liveMusic != null)
            {
                UmaViewerAudio.Stop(liveMusic);
            }

            if (liveVocal != null)
            {
                foreach (var vocal in liveVocal)
                {
                    UmaViewerAudio.Stop(vocal);
                }
                liveVocal.Clear();
            }
        }

        private void OnDestroy()
        {
            CleanupAudio();
            UnbindTimelineEvents();
            CleanupMultiCamera();
            CleanupMonitorCamera();
            ClearLiveProps();

            if (_instance == this)
                _instance = null;
        }
    }

}

