using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class UmaSceneController:MonoBehaviour
{
    private static UmaSceneController _instance;
    /// <summary>
    /// 场景控制器单例。支持在 Unity 域重载后静态数据丢失时自动从场景找回活跃实例自愈。
    /// </summary>
    public static UmaSceneController instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<UmaSceneController>();
            }
            return _instance;
        }
        set => _instance = value;
    }

    public GameObject CavansPrefab;
    public GameObject CavansInstance;

    public GameObject LoadingProgressPanel;
    public Slider LoadingProgressSlider;
    public TextMeshProUGUI LoadingProgressText;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            DestroyImmediate(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(this);
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    private void Start()
    {
        UmaAssetManager.OnLoadProgressChange += LoadingProgressChange;
    }

    public static void LoadScene(string name, Action OnSceneloaded = null, Action OnPrevSceneUnloaded = null)
    {
        instance.StartCoroutine(instance.LoadLiveSceneAsync(name, OnSceneloaded, OnPrevSceneUnloaded));
    }

    IEnumerator LoadLiveSceneAsync(string sceneName, Action OnSceneloaded, Action OnPrevSceneUnloaded)
    {

        if (CavansInstance)
        {
            //Destroy(CavansInstance);
        }
        CavansInstance = Instantiate(CavansPrefab, transform);
        var animation = CavansInstance.GetComponent<Animation>();
        animation.Play("SceneTransition_s");
        yield return new WaitUntil(() => !animation.isPlaying);

        // Set the current Scene to be able to unload it later
        Scene currentScene = SceneManager.GetActiveScene();

        // The Application loads the Scene in the background at the same time as the current Scene.
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);

        // Wait until the last operation fully loads to return anything
        yield return new WaitUntil(()=> asyncLoad.isDone);

        OnSceneloaded?.Invoke();

        // Unload the previous Scene
        AsyncOperation asyncUnLoad = SceneManager.UnloadSceneAsync(currentScene);
        yield return new WaitUntil(() => asyncUnLoad.isDone);

        OnPrevSceneUnloaded?.Invoke();

        animation.Play("SceneTransition_e");
        yield return new WaitUntil(() => !animation.isPlaying);
        Destroy(CavansInstance);
    }

    public void LoadingProgressChange(int curren, int target, string message = null)
    {
        if (curren == -1)
        {
            if (LoadingProgressPanel != null)
                LoadingProgressPanel.SetActive(false);
        }
        else if (target > 0)
        {
            if (LoadingProgressPanel != null)
                LoadingProgressPanel.SetActive(true);
            if (LoadingProgressSlider != null)
                LoadingProgressSlider.value = (float)curren / target;
            if (LoadingProgressText != null)
            {
                if (string.IsNullOrEmpty(message))
                {
                    LoadingProgressText.text = $"Loading...({curren}/{target})";
                }
                else
                {
                    // 若当前计数已达目标且提供了明确阶段描述（如 Loading Characters & Stage...），直接展示完整说明
                    LoadingProgressText.text = (curren >= target) ? message : $"{message}({curren}/{target})";
                }
            }
        }
    }
}

