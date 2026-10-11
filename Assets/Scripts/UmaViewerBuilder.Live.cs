using CriWareFormats;
using Gallop;
using Gallop.Live;
using NAudio.Wave;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UmaMusumeAudio;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;

/// <summary>
/// UmaViewerBuilder 的 Live 演出与音频播放分部类
/// 负责 Live 场景驱动、Live 角色组装与进度上报、演唱会音频与伴奏播放、预览试听及歌词解析
/// </summary>
public partial class UmaViewerBuilder : MonoBehaviour
{
    // Live 与音频相关专属字段
    public GameObject LiveControllerPrefab;
    public List<UmaDatabaseEntry> CurrentLiveSoundAWB = new List<UmaDatabaseEntry>();
    public int CurrentLiveSoundAWBIndex = -1;
    public List<AudioSource> CurrentAudioSources = new List<AudioSource>();
    public List<UmaLyricsData> CurrentLyrics = new List<UmaLyricsData>();

    /// <summary>
    /// 加载 Live 演出马娘角色模型并挂载至舞台节点，并在实例化各角色过程中分步刷新加载进度
    /// </summary>
    /// <param name="characters">角色配置列表</param>
    public void LoadLiveUma(List<LiveCharacterLoadData> characters)
    {
        for (int i = 0; i < characters.Count; i++)
        {
            // 组装角色模型循环中每实例化一个角色，刷新一次进度上报，使模型加载阶段进度平滑可视
            UmaSceneController.instance?.LoadingProgressChange(i + 1, characters.Count, $"Loading Characters ({i + 1}/{characters.Count})...");

            if (characters[i].CharaEntry.Name != "")
            {
                var umaContainer = new GameObject($"Chara_{characters[i].CharaEntry.Id}_{characters[i].CostumeId}").AddComponent<UmaContainerCharacter>();
                umaContainer.IsLive = true;
                umaContainer.CharaData = UmaDatabaseController.ReadCharaData(characters[i].CharaEntry);
                var charObjs = Gallop.Live.Director.instance.charaObjs;
                umaContainer.transform.parent = charObjs[i];
                umaContainer.transform.localPosition = new Vector3();

                if (characters[i].CharaEntry.IsMob)
                {
                    LoadMobUma(umaContainer, characters[i].CharaEntry, characters[i].CostumeId);
                }
                else
                {
                    LoadNormalUma(umaContainer, characters[i].CharaEntry, characters[i].CostumeId, false, characters[i].HeadCostumeId);
                }

                if (umaContainer.UmaAnimator != null)
                {
                    umaContainer.UmaAnimator.enabled = false;
                    umaContainer.isAnimatorControl = false;
                }

                umaContainer.ConfigureLivePhysics();

                Gallop.Live.Director.instance.CharaContainerScript.Add(umaContainer);
            }
        }
    }

    /// <summary>
    /// 主 Live 加载流程入口：捕获角色信息、预载资源包、切换场景并初始化 Live 控制器
    /// </summary>
    /// <param name="live">Live 歌曲条目数据</param>
    /// <param name="characters">选人面板角色选择列表</param>
    public void LoadLive(LiveEntry live, List<LiveCharacterSelect> characters)
    {
        characters.ForEach(a =>
        {
            if (a.CharaEntry == null || string.IsNullOrEmpty(a.CostumeId))
            {
                a.CharaEntry = Main.Characters[Random.Range(0, Main.Characters.Count / 2)];
                a.CostumeId = "0002_00_00";
            }
        });

        // 在旧 UI 场景卸载前复制成普通 C# 数据。后续异步加载不会丢角色选择。
        List<LiveCharacterLoadData> liveCharacters =
            LiveCharacterLoadData.CaptureAll(characters);

        bool requireStage = UI.isRequireStage;
        List<UmaDatabaseEntry> preloadEntries =
            Director.GetLivePreloadEntries(live, liveCharacters, requireStage);

        UmaAssetManager.PreLoadAndRun(preloadEntries, delegate
        {
            UmaSceneController.LoadScene(
                "LiveScene",
                delegate
                {
                    GameObject mainLive = Instantiate(LiveControllerPrefab);
                    Director controller = mainLive.GetComponentInChildren<Director>();
                    controller.live = live;
                    controller.IsRecordVMD = UI.isRecordVMD;
                    controller.RequireStage = requireStage;

                    var binder = mainLive.GetComponent<CyalumeAutoBinder>() ??
                                 mainLive.AddComponent<CyalumeAutoBinder>();

                    if (mainLive.GetComponent<Gallop.Live.StageBlinkLightDriver>() == null)
                        mainLive.AddComponent<Gallop.Live.StageBlinkLightDriver>();

                    if (mainLive.GetComponent<Gallop.Live.StageUVScrollLightDriver>() == null)
                        mainLive.AddComponent<Gallop.Live.StageUVScrollLightDriver>();

                    if (mainLive.GetComponent<Gallop.Live.MonitorUvMovieProvider>() == null)
                        mainLive.AddComponent<Gallop.Live.MonitorUvMovieProvider>();

                    if (mainLive.GetComponent<Gallop.Live.StageMonitorDriver>() == null)
                        mainLive.AddComponent<Gallop.Live.StageMonitorDriver>();

                    binder.musicId = live.MusicId;

                    var transferObjs = new List<GameObject>
                    {
                        mainLive,
                        GameObject.Find("ViewerMain"),
                        GameObject.Find("Directional Light"),
                        GameObject.Find("GlobalShaderController"),
                        GameObject.Find("AudioManager")
                    };

                    foreach (GameObject obj in transferObjs)
                    {
                        if (obj != null)
                        {
                            SceneManager.MoveGameObjectToScene(
                                obj,
                                SceneManager.GetSceneByName("LiveScene"));
                        }
                    }

                    controller.Initialize();

                    int actualMemberCount = controller
                        ._liveTimelineControl
                        .data
                        .worksheetList[0]
                        .charaMotSeqList
                        .Count;

                    // 当舞台站位节点数（伴舞/多机位目标）大于动作轨人数时，扩充至全部站位，确保高序号伴舞角色定位器就绪
                    if (controller.charaObjs != null && controller.charaObjs.Count > actualMemberCount)
                    {
                        actualMemberCount = controller.charaObjs.Count;
                    }

                    if (actualMemberCount > liveCharacters.Count && liveCharacters.Count > 0)
                    {
                        Debug.LogWarning(
                            $"actual member count is {actualMemberCount} current {liveCharacters.Count}");

                        var expandedCharacters = new List<LiveCharacterLoadData>();
                        for (int i = 0; i < actualMemberCount; i++)
                            expandedCharacters.Add(liveCharacters[i % liveCharacters.Count]);

                        liveCharacters = expandedCharacters;
                    }

                    LoadLiveUma(liveCharacters);

                    var lyrics = LoadLiveLyrics(live.MusicId);
                    if (lyrics != null)
                        LiveViewerUI.Instance.CurrentLyrics = lyrics;
                },
                delegate
                {
                    Director.instance.InitializeUI();
                    Director.instance.InitializeTimeline(liveCharacters, UI.LiveMode);
                    Director.instance.InitializeMusic(live.MusicId, liveCharacters);

                    // 在 LoadLiveUma 完成、场景切换完毕且在准备执行 Director.instance.Play() 开播前，才调用隐藏进度条
                    // 保证初次与二次进入体验完全一致，杜绝主线程同步实例化模型时的无进度条假死卡顿假象
                    if (UmaSceneController.instance != null)
                    {
                        UmaSceneController.instance.LoadingProgressChange(-1, -1);
                    }

                    Director.instance.Play();
                });
        });
    }

    /// <summary>
    /// 加载 Live 伴奏与角色歌声音轨
    /// </summary>
    /// <param name="songid">歌曲 Id</param>
    /// <param name="SongAwb">人声音轨资源条目</param>
    /// <param name="needLyrics">是否同时加载歌词</param>
    public void LoadLiveSound(int songid, UmaDatabaseEntry SongAwb, bool needLyrics = true)
    {
        CurrentLiveSoundAWBIndex = -1; // mix awb together
        ClearLiveSounds();
        // 加载角色人声
        if (SongAwb != null)
        {
            PlaySound(SongAwb);
            AddLiveSound(SongAwb);
        }

        // 加载背景伴奏
        string nameVar = $"snd_bgm_live_{songid}_oke";
        UmaDatabaseEntry BGawb = Main.AbSounds.FirstOrDefault(a => a.Name.Contains(nameVar) && a.Name.EndsWith("awb"));
        if (BGawb != null)
        {
            var BGclip = LoadAudio(BGawb);
            if (BGclip.Count > 0)
            {
                AddAudioSource(BGclip[0]);
                AddLiveSound(BGawb);
            }
            else
            {
                // 伴奏文件存在但未解析出有效音轨
                UmaErrorManager.ReportAudioLoadError(BGawb.Name, "伴奏音频解析失败或未包含有效音轨。");
            }
        }
        else
        {
            // 数据库中未匹配到该伴奏资源
            UmaErrorManager.ReportAudioLoadError(nameVar, "未在音轨数据库中找到该 Live 伴奏 (oke) 资源条目。");
        }

        if (needLyrics)
        {
            LoadLiveLyrics(songid);
        }
    }

    /// <summary>
    /// 加载并播放 Live 预览试听音频（正常音量，播放一次后自动停止）
    /// </summary>
    /// <param name="songid">Live歌曲Id</param>
    public void loadLivePreviewSound(int songid)
    {
        string nameVar = $"snd_bgm_live_{songid}_preview_02";
        UmaDatabaseEntry previewAwb = Main.AbSounds.FirstOrDefault(a => a.Name.Contains(nameVar) && a.Name.EndsWith("awb"));
        if (previewAwb == null)
        {
            // 兼容可能无 _02 后缀的试听音频资源
            string fallbackVar = $"snd_bgm_live_{songid}_preview";
            previewAwb = Main.AbSounds.FirstOrDefault(a => a.Name.Contains(fallbackVar) && a.Name.EndsWith("awb"));
        }

        if (previewAwb != null)
        {
            // 正常音量试听，播放一次后自动停止
            PlaySound(previewAwb, volume: 1.0f, loop: false);
        }
    }

    /// <summary>
    /// 播放音频资源条目
    /// </summary>
    public void PlaySound(UmaDatabaseEntry SongAwb, int subindex = -1, float volume = 1, bool loop = false)
    {
        CurrentLyrics.Clear();
        if (CurrentAudioSources.Count > 0)
        {
            var tmp = CurrentAudioSources[0];
            CurrentAudioSources.Clear();
            Destroy(tmp.gameObject);
            UI.AudioSettings.ResetPlayer();
        }
        if (subindex == -1)
        {
            var clips = LoadAudio(SongAwb);
            foreach (var clip in clips)
            {
                AddAudioSource(clip, volume, loop);
            }
        }
        else
        {
            var clips = LoadAudio(SongAwb);
            if (clips != null && subindex >= 0 && subindex < clips.Count)
            {
                AddAudioSource(clips[subindex], volume, loop);
            }
        }
        SetLastAudio(SongAwb, subindex);
    }

    /// <summary>
    /// 记录最后播放的音频资源及子索引
    /// </summary>
    public void SetLastAudio(UmaDatabaseEntry AudioAwb, int index)
    {
        ClearLiveSounds();
        AddLiveSound(AudioAwb);
        CurrentLiveSoundAWBIndex = index;
    }

    /// <summary>
    /// 向当前音频播放器中添加音频源
    /// </summary>
    private void AddAudioSource(AudioClip clip, float volume = 1, bool loop = false)
    {
        AudioSource source;
        if (CurrentAudioSources.Count > 0)
        {
            source = CurrentAudioSources[0].gameObject.AddComponent<AudioSource>();
        }
        else
        {
            source = new GameObject("SoundController").AddComponent<AudioSource>();
        }
        CurrentAudioSources.Add(source);
        source.clip = clip;
        source.volume = volume;
        source.loop = loop;
        source.Play();
    }

    /// <summary>
    /// 加载 AWB 容器内的音频流列表
    /// </summary>
    public List<UmaWaveStream> LoadAudioStreams(UmaDatabaseEntry awb)
    {
        var streams = new List<UmaWaveStream>();
        if (awb == null) return streams;

        string awbPath = awb.FilePath;
        if (!File.Exists(awbPath))
        {
            UmaErrorManager.ReportAudioLoadError(awb.Name ?? "未知音频", $"本地音频文件不存在: {awbPath}");
            return streams;
        }

        try
        {
            FileStream awbFile = File.OpenRead(awbPath);
            AwbReader awbReader = new AwbReader(awbFile);

            foreach (Wave wave in awbReader.Waves)
            {
                var stream = new UmaWaveStream(awbReader, wave.WaveId);
                streams.Add(stream);
            }
        }
        catch (Exception ex)
        {
            UmaErrorManager.ReportAudioLoadError(awb.Name ?? awbPath, $"音频流解析异常: {ex.Message}");
            Debug.LogError($"[UmaViewerBuilder] LoadAudioStreams failed for {awb.Name}: {ex}");
        }

        return streams;
    }

    /// <summary>
    /// 根据本地音频文件路径加载并解码为 Unity AudioClip 列表
    /// </summary>
    /// <param name="audioPath">本地音频文件路径</param>
    /// <param name="clipNamePrefix">生成 AudioClip 的前缀名称</param>
    public static List<AudioClip> LoadAudio(string audioPath, string clipNamePrefix = null)
    {
        List<AudioClip> clips = new List<AudioClip>();
        if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
        {
            UmaErrorManager.ReportAudioLoadError(clipNamePrefix ?? audioPath ?? "未知音频", $"本地音频文件缺失或无法读取: {audioPath}");
            return clips;
        }

        try
        {
            FileStream awbFile = File.OpenRead(audioPath);
            AwbReader awbReader = new AwbReader(awbFile);
            string namePrefix = string.IsNullOrEmpty(clipNamePrefix) ? Path.GetFileNameWithoutExtension(audioPath) : clipNamePrefix;

            foreach (Wave wave in awbReader.Waves)
            {
                var stream = new UmaWaveStream(awbReader, wave.WaveId);
                var sampleProvider = stream.ToSampleProvider();

                int channels = stream.WaveFormat.Channels;
                int bytesPerSample = stream.WaveFormat.BitsPerSample / 8;
                int sampleRate = stream.WaveFormat.SampleRate;

                AudioClip clip = AudioClip.Create(
                    namePrefix + "_" + wave.WaveId.ToString(),
                    (int)(stream.Length / channels / bytesPerSample),
                    channels,
                    sampleRate,
                    true,
                    data => sampleProvider.Read(data, 0, data.Length),
                    position => stream.Position = position * channels * bytesPerSample);

                clips.Add(clip);
            }
        }
        catch (Exception ex)
        {
            UmaErrorManager.ReportAudioLoadError(clipNamePrefix ?? audioPath, $"音频解码或读取异常: {ex.Message}");
            Debug.LogError($"[UmaViewerBuilder] LoadAudio failed for '{clipNamePrefix ?? audioPath}': {ex}");
        }

        return clips;
    }

    /// <summary>
    /// 根据 Uma 资源数据库条目加载并解码音频
    /// </summary>
    /// <param name="awb">数据库音频资源条目</param>
    public static List<AudioClip> LoadAudio(UmaDatabaseEntry awb)
    {
        if (awb == null)
        {
            UmaErrorManager.ReportAudioLoadError("空音频资源", "请求加载的音频资源条目为 null。");
            return new List<AudioClip>();
        }

        return LoadAudio(awb.FilePath, Path.GetFileNameWithoutExtension(awb.Name));
    }

    /// <summary>
    /// 加载 Live 歌词数据
    /// </summary>
    /// <param name="songid">歌曲 Id</param>
    public List<UmaLyricsData> LoadLiveLyrics(int songid)
    {
        if (CurrentLyrics.Count > 0) CurrentLyrics.Clear();

        string lyricsVar = $"live/musicscores/m{songid}/m{songid}_lyrics";
        UmaDatabaseEntry lyricsAsset = Main.AbList[lyricsVar];
        AssetBundle bundle = UmaAssetManager.LoadAssetBundle(lyricsAsset);
        TextAsset asset = bundle.LoadAsset<TextAsset>(Path.GetFileNameWithoutExtension(lyricsVar));
        string[] lines = asset.text.Split("\n"[0]);

        for (int i = 1; i < lines.Length; i++)
        {
            string[] words = lines[i].Split(',');
            if (words.Length > 0)
            {
                try
                {
                    UmaLyricsData lyricsData = new UmaLyricsData()
                    {
                        time = float.Parse(words[0]) / 1000,
                        text = (words.Length > 1) ? words[1] : ""
                    };
                    CurrentLyrics.Add(lyricsData);
                }
                catch { }
            }
        }
        return CurrentLyrics;
    }

    /// <summary>
    /// 加载 Live 封面唱片图标
    /// </summary>
    /// <param name="musicid">Live 歌曲 Id</param>
    public Sprite LoadLiveIcon(int musicid)
    {
        string value = $"live/jacket/jacket_icon_l_{musicid}";

        if (UmaViewerMain.Instance.AbList.TryGetValue(value, out UmaDatabaseEntry entry))
        {
            AssetBundle assetBundle = UmaAssetManager.LoadAssetBundle(entry, true);
            if (assetBundle.Contains($"jacket_icon_l_{musicid}"))
            {
                Texture2D texture = (Texture2D)assetBundle.LoadAsset($"jacket_icon_l_{musicid}");
                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.zero);
                UmaAssetManager.UnloadAssetBundle(entry, false);
                return sprite;
            }
        }
        return null;
    }

    /// <summary>
    /// 登记正在播放的 Live 声音资源条目
    /// </summary>
    private void AddLiveSound(UmaDatabaseEntry entry)
    {
        if (!CurrentLiveSoundAWB.Contains(entry))
            CurrentLiveSoundAWB.Add(entry);
    }

    /// <summary>
    /// 清理已记录的 Live 声音资源
    /// </summary>
    private void ClearLiveSounds()
    {
        CurrentLiveSoundAWB.Clear();
    }
}
