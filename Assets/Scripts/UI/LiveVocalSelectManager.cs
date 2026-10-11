using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Live 选人音轨提示与模式隔离管理器
/// 负责检测 Live 拥有的角色音轨、在角色列表显示音轨徽标、提供排序筛选开关，
/// 并确保与普通角色预览模式严格隔离。
/// </summary>
public class LiveVocalSelectManager : MonoBehaviour
{
    /// <summary>
    /// 单例实例
    /// </summary>
    private static LiveVocalSelectManager _instance;
    public static LiveVocalSelectManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<LiveVocalSelectManager>();
                if (_instance == null)
                {
                    GameObject go = new GameObject("LiveVocalSelectManager");
                    _instance = go.AddComponent<LiveVocalSelectManager>();
                    if (Application.isPlaying)
                    {
                        DontDestroyOnLoad(go);
                    }
                }
            }
            return _instance;
        }
        private set => _instance = value;
    }

    /// <summary>
    /// 筛选与排序模式枚举
    /// </summary>
    public enum VocalFilterMode
    {
        /// <summary>全部角色：默认 ID 升序，有音轨的角色显示徽章</summary>
        All = 0,
        /// <summary>音轨优先：有音轨的角色置顶排在最前</summary>
        VocalFirst = 1,
        /// <summary>仅限音轨：只显示有音轨的角色，其余隐藏</summary>
        VocalOnly = 2
    }

    /// <summary>
    /// 角色条目数据包装类
    /// </summary>
    private class CharacterItemInfo
    {
        public int CharaId;
        public UmaUIContainer Container;
        public int OriginalSiblingIndex;
        public GameObject BadgeObject;
    }

    /// <summary>
    /// 当前是否处于 Live 选人模式
    /// </summary>
    public bool IsLiveSelectMode { get; private set; } = false;

    /// <summary>
    /// 当前选中的 Live 槽位组件
    /// </summary>
    public LiveCharacterSelect CurrentLiveSlot { get; private set; }

    /// <summary>
    /// 当前 Live 的 MusicId
    /// </summary>
    public int CurrentMusicId { get; private set; } = -1;

    /// <summary>
    /// 当前筛选模式（默认全部角色）
    /// </summary>
    private VocalFilterMode _currentFilterMode = VocalFilterMode.All;

    /// <summary>
    /// 当前筛选与排序模式（暴露给 UI 与测试断言）
    /// </summary>
    public VocalFilterMode CurrentFilterMode
    {
        get => _currentFilterMode;
        set
        {
            _currentFilterMode = value;
            if (IsLiveSelectMode)
            {
                ApplyFilterAndSort();
            }
        }
    }

    /// <summary>
    /// 已经注册的角色条目列表
    /// </summary>
    private readonly List<CharacterItemInfo> _registeredItems = new List<CharacterItemInfo>();

    /// <summary>
    /// 角色 ID 到条目信息的映射
    /// </summary>
    private readonly Dictionary<int, CharacterItemInfo> _itemMap = new Dictionary<int, CharacterItemInfo>();

    /// <summary>
    /// 各 MusicId 对应的拥有音轨的角色 ID 集合缓存
    /// </summary>
    private readonly Dictionary<int, HashSet<int>> _musicVocalCache = new Dictionary<int, HashSet<int>>();

    /// <summary>
    /// 当前 Live 拥有的音轨角色 ID 集合
    /// </summary>
    private HashSet<int> _currentVocalCharaIds = new HashSet<int>();

    /// <summary>
    /// 动态生成的音轨小图标 Sprite 共享实例
    /// </summary>
    private static Sprite _vocalBadgeSprite;

    /// <summary>
    /// 筛选与排序切换按钮对象及其文本组件
    /// </summary>
    private GameObject _filterButtonObject;
    private TextMeshProUGUI _filterButtonTMP;
    private Text _filterButtonText;

    /// <summary>
    /// 分类栏（Type）容器的 RectTransform 及其原始宽度，用于在 Live 模式下适度展开并在退出时恢复
    /// </summary>
    private RectTransform _typeRectTransform;
    private float _originalTypeWidth = -1f;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    /// <summary>
    /// 向管理器注册角色列表中的卡片条目
    /// 在 LoadModelPanels() 创建 CharactersList 角色条目时调用
    /// </summary>
    /// <param name="charaId">角色 ID</param>
    /// <param name="container">条目容器组件</param>
    public void RegisterCharacterItem(int charaId, UmaUIContainer container)
    {
        if (container == null) return;

        // 若已注册过同 ID，先更新引用
        if (_itemMap.TryGetValue(charaId, out var existing))
        {
            existing.Container = container;
            existing.OriginalSiblingIndex = container.transform.GetSiblingIndex();
            EnsureBadgeCreated(existing);
            return;
        }

        var item = new CharacterItemInfo
        {
            CharaId = charaId,
            Container = container,
            OriginalSiblingIndex = container.transform.GetSiblingIndex()
        };

        EnsureBadgeCreated(item);

        _registeredItems.Add(item);
        _itemMap[charaId] = item;
    }

    /// <summary>
    /// 进入 Live 选人模式
    /// </summary>
    /// <param name="musicId">当前 Live 的 Music ID</param>
    /// <param name="slot">触发选择的 Live 角色槽位</param>
    public void EnterLiveSelectMode(int musicId, LiveCharacterSelect slot)
    {
        IsLiveSelectMode = true;
        CurrentMusicId = musicId;
        CurrentLiveSlot = slot;

        // 获取并缓存当前 Live 拥有的角色音轨
        _currentVocalCharaIds = GetVocalCharaIds(musicId);

        // 确保筛选按钮创建并显示
        EnsureFilterButtonCreated();
        if (_filterButtonObject != null)
        {
            _filterButtonObject.SetActive(true);
            _filterButtonObject.transform.SetAsLastSibling();
        }

        // 展开分类栏宽度以容纳新增的筛选按钮（120px 按钮 + 15px 间距）
        if (_typeRectTransform != null && _originalTypeWidth > 0f)
        {
            _typeRectTransform.sizeDelta = new Vector2(_originalTypeWidth + 135f, _typeRectTransform.sizeDelta.y);
        }

        // 应用当前的筛选与排序模式
        ApplyFilterAndSort();
    }

    /// <summary>
    /// 彻底退出 Live 选人模式，恢复到普通预览模式的初始干净状态
    /// </summary>
    public void ExitLiveSelectMode()
    {
        IsLiveSelectMode = false;
        CurrentMusicId = -1;
        CurrentLiveSlot = null;
        _currentFilterMode = VocalFilterMode.All;

        // 隐藏筛选切换按钮
        if (_filterButtonObject != null)
        {
            _filterButtonObject.SetActive(false);
        }

        // 还原分类栏原始宽度，确保普通模式布局完全不受影响
        if (_typeRectTransform != null && _originalTypeWidth > 0f)
        {
            _typeRectTransform.sizeDelta = new Vector2(_originalTypeWidth, _typeRectTransform.sizeDelta.y);
        }

        // 遍历所有注册的角色条目，隐藏小图标并全部激活显示
        foreach (var item in _registeredItems)
        {
            if (item.BadgeObject != null)
            {
                item.BadgeObject.SetActive(false);
            }

            if (item.Container != null)
            {
                item.Container.gameObject.SetActive(true);
            }
        }

        // 严格按照原始顺序（按角色初始位置）恢复 SiblingIndex
        var sortedByOriginal = _registeredItems.OrderBy(item => item.OriginalSiblingIndex).ToList();
        for (int i = 0; i < sortedByOriginal.Count; i++)
        {
            if (sortedByOriginal[i].Container != null)
            {
                sortedByOriginal[i].Container.transform.SetSiblingIndex(sortedByOriginal[i].OriginalSiblingIndex);
            }
        }

        // 滚动条复位到顶部
        if (UmaViewerUI.Instance != null && UmaViewerUI.Instance.CharactersList != null)
        {
            UmaViewerUI.Instance.CharactersList.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    /// 解析音轨文件名中的角色 ID
    /// 支持格式：snd_bgm_live_{musicId}_chara_{charaId}_{track}.awb 或 snd_bgm_live_{musicId}_chara_{charaId}.awb
    /// </summary>
    /// <param name="entryName">音频资源全路径或文件名</param>
    /// <param name="musicId">目标歌曲 ID</param>
    /// <param name="charaId">解析出的角色 ID</param>
    /// <returns>若符合该歌曲的角色音轨规则且解析成功则返回 true，否则 false</returns>
    public static bool ParseVocalCharaId(string entryName, int musicId, out int charaId)
    {
        charaId = -1;
        if (string.IsNullOrEmpty(entryName)) return false;
        if (!entryName.EndsWith("awb", StringComparison.OrdinalIgnoreCase)) return false;

        string targetKeyword = $"snd_bgm_live_{musicId}_chara";
        if (!entryName.Contains(targetKeyword)) return false;

        string fileName = Path.GetFileNameWithoutExtension(entryName);
        string[] split = fileName.Split('_');

        // 寻找 "chara" 关键字的下标位置，其后紧随的即为 charaId
        int charaIndex = -1;
        for (int i = 0; i < split.Length; i++)
        {
            if (string.Equals(split[i], "chara", StringComparison.OrdinalIgnoreCase))
            {
                charaIndex = i;
                break;
            }
        }

        if (charaIndex >= 0 && charaIndex + 1 < split.Length)
        {
            if (int.TryParse(split[charaIndex + 1], out charaId))
            {
                return true;
            }
        }

        // 容错后备策略：尝试倒数第二段
        if (split.Length >= 2 && int.TryParse(split[split.Length - 2], out charaId))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 检测指定 Live（musicId）包含的角色音轨 ID 集合
    /// </summary>
    public HashSet<int> GetVocalCharaIds(int musicId)
    {
        if (_musicVocalCache.TryGetValue(musicId, out var cached))
        {
            return cached;
        }

        var vocalSet = new HashSet<int>();
        if (UmaViewerMain.Instance != null && UmaViewerMain.Instance.AbSounds != null)
        {
            foreach (var entry in UmaViewerMain.Instance.AbSounds)
            {
                if (ParseVocalCharaId(entry.Name, musicId, out int charaId))
                {
                    vocalSet.Add(charaId);
                }
            }

            // 仅在数据源有效且扫描完成后才写入缓存，防止空集合提前固化
            _musicVocalCache[musicId] = vocalSet;
        }

        return vocalSet;
    }

    /// <summary>
    /// 循环切换筛选与排序模式：All(0) -> VocalFirst(1) -> VocalOnly(2) -> All(0)
    /// </summary>
    public void CycleFilterMode()
    {
        _currentFilterMode = (VocalFilterMode)(((int)_currentFilterMode + 1) % 3);
        if (IsLiveSelectMode)
        {
            ApplyFilterAndSort();
        }
    }

    /// <summary>
    /// 点击筛选按钮时的状态循环切换
    /// </summary>
    private void OnFilterButtonClicked()
    {
        if (!IsLiveSelectMode) return;
        CycleFilterMode();
        // 释放按钮选中焦点，防止高亮状态常驻
        UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
    }

    /// <summary>
    /// 获取当前注册的角色条目数量（供测试断言与状态检查）
    /// </summary>
    public int RegisteredItemCount => _registeredItems.Count;

    /// <summary>
    /// 清空所有注册的角色条目映射（供测试与重置使用）
    /// </summary>
    public void ClearRegisteredItems()
    {
        _registeredItems.Clear();
        _itemMap.Clear();
    }

    /// <summary>
    /// 清空歌曲音轨集合缓存（供测试使用）
    /// </summary>
    public void ClearVocalCache()
    {
        _musicVocalCache.Clear();
    }

    /// <summary>
    /// 手动设置歌曲的音轨角色集合（供单元测试注入模拟数据）
    /// </summary>
    public void SetVocalCacheForTest(int musicId, IEnumerable<int> charaIds)
    {
        _musicVocalCache[musicId] = new HashSet<int>(charaIds);
    }

    /// <summary>
    /// 应用当前的筛选与排序模式
    /// </summary>
    private void ApplyFilterAndSort()
    {
        UpdateFilterButtonText();

        // 1. 刷新音轨小徽标的显隐
        foreach (var item in _registeredItems)
        {
            bool hasVocal = _currentVocalCharaIds.Contains(item.CharaId);
            if (item.BadgeObject != null)
            {
                item.BadgeObject.SetActive(hasVocal);
            }
        }

        // 2. 根据模式控制角色的显隐与顺序
        switch (_currentFilterMode)
        {
            case VocalFilterMode.All:
                // 全部角色显示，按原始序号排列
                var allOriginal = _registeredItems.OrderBy(item => item.OriginalSiblingIndex).ToList();
                for (int i = 0; i < allOriginal.Count; i++)
                {
                    var item = allOriginal[i];
                    if (item.Container != null)
                    {
                        item.Container.gameObject.SetActive(true);
                        item.Container.transform.SetSiblingIndex(item.OriginalSiblingIndex);
                    }
                }
                break;

            case VocalFilterMode.VocalFirst:
                // 全部角色显示，有音轨的角色排在最前（内部按原始顺序），无音轨排在其后
                var vocalFirstList = _registeredItems
                    .OrderByDescending(item => _currentVocalCharaIds.Contains(item.CharaId))
                    .ThenBy(item => item.OriginalSiblingIndex)
                    .ToList();
                for (int i = 0; i < vocalFirstList.Count; i++)
                {
                    var item = vocalFirstList[i];
                    if (item.Container != null)
                    {
                        item.Container.gameObject.SetActive(true);
                        item.Container.transform.SetSiblingIndex(i);
                    }
                }
                break;

            case VocalFilterMode.VocalOnly:
                // 仅显示有音轨的角色，其余隐藏，有音轨的置顶排列
                var vocalOnlyList = _registeredItems
                    .OrderByDescending(item => _currentVocalCharaIds.Contains(item.CharaId))
                    .ThenBy(item => item.OriginalSiblingIndex)
                    .ToList();
                for (int i = 0; i < vocalOnlyList.Count; i++)
                {
                    var item = vocalOnlyList[i];
                    if (item.Container != null)
                    {
                        bool hasVocal = _currentVocalCharaIds.Contains(item.CharaId);
                        item.Container.gameObject.SetActive(hasVocal);
                        item.Container.transform.SetSiblingIndex(i);
                    }
                }
                break;
        }

        // 切换模式后将滚动条复位到顶部，方便查阅
        if (UmaViewerUI.Instance != null && UmaViewerUI.Instance.CharactersList != null)
        {
            UmaViewerUI.Instance.CharactersList.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    /// 刷新筛选切换按钮上的文本显示
    /// 采用全英文避免 TMP_FontAsset 中文字符集缺失导致的 Fallback 异常，
    /// 且与 Normal, Mini, Mob, Unload 等原生 UI 风格完美契合。
    /// </summary>
    private void UpdateFilterButtonText()
    {
        string label;
        Color textColor;

        switch (_currentFilterMode)
        {
            case VocalFilterMode.All:
                label = "All";
                textColor = new Color(0.2f, 0.2f, 0.2f, 1f); // 与 Unload 等白色按钮字体颜色一致的深灰
                break;
            case VocalFilterMode.VocalFirst:
                label = "Vocal First";
                textColor = new Color(0.88f, 0.45f, 0.05f, 1f); // 暖橙色，与界面选中状态呼应
                break;
            case VocalFilterMode.VocalOnly:
                label = "Vocal Only";
                textColor = new Color(0.05f, 0.48f, 0.78f, 1f); // 天蓝色，凸显仅限音轨角色
                break;
            default:
                label = "All";
                textColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                break;
        }

        if (_filterButtonTMP != null)
        {
            _filterButtonTMP.text = label;
            _filterButtonTMP.color = textColor;
            _filterButtonTMP.ForceMeshUpdate();
        }

        if (_filterButtonText != null)
        {
            _filterButtonText.text = label;
            _filterButtonText.color = textColor;
        }
    }

    /// <summary>
    /// 确保角色条目上挂载了音轨小徽标
    /// </summary>
    private void EnsureBadgeCreated(CharacterItemInfo item)
    {
        if (item.Container == null) return;

        Transform existingBadge = item.Container.transform.Find("VocalBadge");
        if (existingBadge != null)
        {
            item.BadgeObject = existingBadge.gameObject;
            item.BadgeObject.SetActive(false);
            return;
        }

        // 创建徽章子对象
        GameObject badgeGo = new GameObject("VocalBadge");
        badgeGo.transform.SetParent(item.Container.transform, false);

        RectTransform rect = badgeGo.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.sizeDelta = new Vector2(20f, 20f);
        rect.anchoredPosition = new Vector2(-4f, -4f);

        Image image = badgeGo.AddComponent<Image>();
        image.sprite = GetOrCreateVocalBadgeSprite();
        image.raycastTarget = false;

        badgeGo.SetActive(false);
        item.BadgeObject = badgeGo;
    }

    /// <summary>
    /// 动态创建或获取筛选切换按钮
    /// 挂载在 Characters 面板分类栏（Type）末尾，位于 Unload 按钮右侧，
    /// 统一使用与 Unload 等同的白色圆角按钮背景、点击反馈与字体风格。
    /// </summary>
    private void EnsureFilterButtonCreated()
    {
        if (_filterButtonObject != null) return;
        if (UmaViewerUI.Instance == null) return;

        // 获取 Characters 角色选择面板下的 Type 分类栏节点
        Transform typeTransform = null;
        if (UmaViewerUI.Instance.SelectCharacterPannel != null)
        {
            typeTransform = UmaViewerUI.Instance.SelectCharacterPannel.transform.Find("Type");
        }
        if (typeTransform == null && UmaViewerUI.Instance.CharactersList != null && UmaViewerUI.Instance.CharactersList.transform.parent != null)
        {
            typeTransform = UmaViewerUI.Instance.CharactersList.transform.parent.Find("Type");
        }
        if (typeTransform == null) return;

        // 记录 Type 容器的 RectTransform 与原始宽度，用于在 Live 模式下展开并在退出时恢复
        _typeRectTransform = typeTransform.GetComponent<RectTransform>();
        if (_typeRectTransform != null && _originalTypeWidth <= 0f)
        {
            _originalTypeWidth = _typeRectTransform.sizeDelta.x;
        }

        // 检查是否已经存在
        Transform existing = typeTransform.Find("LiveVocalFilterButton");
        if (existing != null)
        {
            _filterButtonObject = existing.gameObject;
            Button btn = _filterButtonObject.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnFilterButtonClicked);
            }

            _filterButtonTMP = _filterButtonObject.GetComponentInChildren<TextMeshProUGUI>();
            _filterButtonText = _filterButtonObject.GetComponentInChildren<Text>();
            if (_filterButtonTMP != null)
            {
                _filterButtonTMP.enableAutoSizing = true;
                _filterButtonTMP.fontSizeMin = 12f;
                _filterButtonTMP.fontSizeMax = 18f;
                _filterButtonTMP.enableWordWrapping = false;
                _filterButtonTMP.alignment = TextAlignmentOptions.Center;
            }
            UpdateFilterButtonText();
            return;
        }

        // 优先以 Type 栏下已有的白色按钮（Unload 按钮）为模板克隆，保证白底圆角、材质与字体完全统一
        Transform unloadBtn = typeTransform.Find("Button");
        if (unloadBtn != null)
        {
            _filterButtonObject = Instantiate(unloadBtn.gameObject, typeTransform);
            _filterButtonObject.name = "LiveVocalFilterButton";

            // 清空从模板继承的原有监听事件，挂载切换模式回调
            Button btn = _filterButtonObject.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnFilterButtonClicked);
            }

            _filterButtonTMP = _filterButtonObject.GetComponentInChildren<TextMeshProUGUI>();
            _filterButtonText = _filterButtonObject.GetComponentInChildren<Text>();
            if (_filterButtonTMP != null)
            {
                _filterButtonTMP.enableAutoSizing = true;
                _filterButtonTMP.fontSizeMin = 12f;
                _filterButtonTMP.fontSizeMax = 18f;
                _filterButtonTMP.enableWordWrapping = false;
                _filterButtonTMP.alignment = TextAlignmentOptions.Center;
            }
        }
        else
        {
            // 后备方案：动态创建白色圆角按钮
            _filterButtonObject = new GameObject("LiveVocalFilterButton");
            _filterButtonObject.transform.SetParent(typeTransform, false);

            RectTransform rt = _filterButtonObject.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(120f, 30f);

            Image bg = _filterButtonObject.AddComponent<Image>();
            bg.color = new Color(0.96f, 0.96f, 0.96f, 1f);

            Button btn = _filterButtonObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            ColorBlock colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.selectedColor = new Color(0.96f, 0.96f, 0.96f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(OnFilterButtonClicked);

            GameObject textGo = new GameObject("Text (TMP)");
            textGo.transform.SetParent(_filterButtonObject.transform, false);
            RectTransform textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;
            textRt.anchoredPosition = Vector2.zero;

            _filterButtonTMP = textGo.AddComponent<TextMeshProUGUI>();
            _filterButtonTMP.alignment = TextAlignmentOptions.Center;
            _filterButtonTMP.enableAutoSizing = true;
            _filterButtonTMP.fontSizeMin = 12f;
            _filterButtonTMP.fontSizeMax = 18f;
            _filterButtonTMP.enableWordWrapping = false;
            _filterButtonTMP.raycastTarget = false;
        }

        // 排在末尾，位于 Unload 按钮右侧
        _filterButtonObject.transform.SetAsLastSibling();
        _filterButtonObject.SetActive(false);
        UpdateFilterButtonText();
    }

    /// <summary>
    /// 动态生成精致抗锯齿的金色音符 🎵 徽标纹理并转换为 Sprite
    /// 纹理尺寸 32x32，在 20x20 渲染下平滑细腻
    /// </summary>
    private static Sprite GetOrCreateVocalBadgeSprite()
    {
        if (_vocalBadgeSprite != null) return _vocalBadgeSprite;

        int size = 32;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;

        Color[] pixels = new Color[size * size];
        Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
        float radius = 13.5f;

        // 颜色定义
        Color goldOuter = new Color(0.96f, 0.65f, 0.14f, 0.95f); // 金色外圈
        Color goldInner = new Color(1f, 0.82f, 0.25f, 0.95f);    // 金色内层
        Color noteColor = new Color(1f, 1f, 1f, 1f);             // 纯白音符

        // 1. 绘制带有平滑抗锯齿的金色徽章背景
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = y * size + x;
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float edgeAlpha = Mathf.Clamp01(radius - dist + 0.5f);

                if (edgeAlpha > 0f)
                {
                    float t = Mathf.Clamp01(dist / radius);
                    Color baseCol = Color.Lerp(goldInner, goldOuter, t);
                    baseCol.a *= edgeAlpha;
                    pixels[index] = baseCol;
                }
                else
                {
                    pixels[index] = Color.clear;
                }
            }
        }

        // 2. 绘制精致的双音符 (♫) 图案
        // 左符头中心 (10.5, 9.5)，右符头中心 (19.5, 12.5)
        Vector2 leftHead = new Vector2(10.5f, 9.5f);
        Vector2 rightHead = new Vector2(19.5f, 12.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = y * size + x;
                Vector2 pt = new Vector2(x + 0.5f, y + 0.5f);
                float noteAlpha = 0f;

                // 左符头（倾斜椭圆）
                Vector2 d1 = pt - leftHead;
                float rotX1 = d1.x * 0.866f + d1.y * 0.5f;
                float rotY1 = -d1.x * 0.5f + d1.y * 0.866f;
                float ellipse1 = (rotX1 * rotX1) / (3.2f * 3.2f) + (rotY1 * rotY1) / (2.2f * 2.2f);
                if (ellipse1 <= 1f)
                {
                    noteAlpha = Mathf.Max(noteAlpha, Mathf.Clamp01((1f - ellipse1) * 3f));
                }

                // 右符头（倾斜椭圆）
                Vector2 d2 = pt - rightHead;
                float rotX2 = d2.x * 0.866f + d2.y * 0.5f;
                float rotY2 = -d2.x * 0.5f + d2.y * 0.866f;
                float ellipse2 = (rotX2 * rotX2) / (3.2f * 3.2f) + (rotY2 * rotY2) / (2.2f * 2.2f);
                if (ellipse2 <= 1f)
                {
                    noteAlpha = Mathf.Max(noteAlpha, Mathf.Clamp01((1f - ellipse2) * 3f));
                }

                // 左符杆：x 在 [12.5, 14.5]，y 在 [9.5, 23.5]
                if (pt.x >= 12.5f && pt.x <= 14.2f && pt.y >= 9.5f && pt.y <= 23.5f)
                {
                    float stemAlpha = Mathf.Clamp01(Mathf.Min(pt.x - 12.5f, 14.2f - pt.x) * 2f);
                    noteAlpha = Mathf.Max(noteAlpha, stemAlpha);
                }

                // 右符杆：x 在 [21.5, 23.2]，y 在 [12.5, 25.5]
                if (pt.x >= 21.5f && pt.x <= 23.2f && pt.y >= 12.5f && pt.y <= 25.5f)
                {
                    float stemAlpha = Mathf.Clamp01(Mathf.Min(pt.x - 21.5f, 23.2f - pt.x) * 2f);
                    noteAlpha = Mathf.Max(noteAlpha, stemAlpha);
                }

                // 顶端符梁：连接左杆顶 (13.5, 23.5) 到右杆顶 (22.5, 25.5)，厚度约 2.8px
                if (pt.x >= 12.5f && pt.x <= 23.2f)
                {
                    float progress = (pt.x - 12.5f) / (23.2f - 12.5f);
                    float beamTopY = Mathf.Lerp(23.5f, 25.5f, progress);
                    float beamBottomY = beamTopY - 2.8f;
                    if (pt.y >= beamBottomY && pt.y <= beamTopY)
                    {
                        float beamAlpha = Mathf.Clamp01(Mathf.Min(pt.y - beamBottomY, beamTopY - pt.y) * 2f);
                        noteAlpha = Mathf.Max(noteAlpha, beamAlpha);
                    }
                }

                // 混合音符颜色到背景像素
                if (noteAlpha > 0f)
                {
                    Color currentBg = pixels[index];
                    Color blended = Color.Lerp(currentBg, noteColor, noteAlpha);
                    blended.a = Mathf.Max(currentBg.a, noteAlpha);
                    pixels[index] = blended;
                }
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        _vocalBadgeSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f)
        );

        return _vocalBadgeSprite;
    }
}
