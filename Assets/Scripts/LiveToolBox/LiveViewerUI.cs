using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UI;

public class LiveViewerUI : MonoBehaviour
{
    private static LiveViewerUI _instance;
    /// <summary>
    /// Live 界面单例。支持在域重载后自动从场景找回活跃实例自愈。
    /// </summary>
    public static LiveViewerUI Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<LiveViewerUI>();
            }
            return _instance;
        }
        set => _instance = value;
    }

    public UnityEngine.UI.Slider ProgressBar;

    public RectTransform BottonUITransform;

    public Dropdown FrameRateDropDown;

    public GameObject RecordingUI;

    public Text RecordingText;

    public Text LyricsText;

    public List<UmaLyricsData> CurrentLyrics = new List<UmaLyricsData>();

    float targetHeight = 0;
    float height;
    private void Awake()
    {
        if (Config.Instance == null)
        {
            new Config();
        }
        ApplyFrameRateOptions();
        UmaViewerMain.ApplyFrameRateLimit();

        height = BottonUITransform.rect.height;
        targetHeight = 0;
        Invoke(nameof(HideSlider), 1.5f);
        _instance = this;
        //TrueProgressBar = (UnityEngine.UIElements.Slider)ProgressBar;
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public void OnMouse(bool isEnter)
    {
        if (isEnter)
        {
            targetHeight = 0;
            CancelInvoke();
        }
        else
        {
            Invoke(nameof(HideSlider), 2.5f);
        }
    }

    private void FixedUpdate()
    {
        BottonUITransform.anchoredPosition = Vector2.Lerp(BottonUITransform.anchoredPosition, new Vector2(0, targetHeight), Time.fixedDeltaTime * 5);
    }

    private void HideSlider()
    {
        targetHeight = -height;
    }

    public void SetFrameRate(int fps)
    {
        Config.Instance.TargetFrameRate = fps == 1 ? 30 : 60;
        Config.Instance.UpdateConfig(false);
        UmaViewerMain.ApplyFrameRateLimit();
    }

    private void ApplyFrameRateOptions()
    {
        if (FrameRateDropDown == null) return;

        FrameRateDropDown.ClearOptions();
        FrameRateDropDown.AddOptions(new List<string> { "60", "30" });
        FrameRateDropDown.SetValueWithoutNotify(Config.Instance.GetTargetFrameRate() == 30 ? 1 : 0);
        FrameRateDropDown.RefreshShownValue();
    }

    public void UpdateLyrics(float time)
    {
        var text = UmaUtility.GetCurrentLyrics(time, CurrentLyrics);
        LyricsText.text = text;
    }
}
