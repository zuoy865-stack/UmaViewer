using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>跨编译及 Play Mode 域重载恢复真实导出验证。</summary>
[InitializeOnLoad]
public static class PMXStage1TeioBatch
{
    private const string Key = "PMXStage1TeioBatch.phase";
    private static string ReportPath => Path.Combine(OutputDirectory, "teio_1003_00.pmx.probe.json");
    private static string OutputDirectory
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-pmxStage1Output") return Path.GetFullPath(args[i + 1]);
            return Path.GetFullPath("Logs/teio-export-stage1-20261005/teio-new");
        }
    }
    static PMXStage1TeioBatch()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        EditorApplication.update += Poll;
        if (SessionState.GetString(Key, "") == "compiling")
            EditorApplication.delayCall += ContinueAfterCompilation;
    }

    public static void RunBatch() => Begin(waitForSourceChange: false);
    public static void RunBatchWithRefresh() => Begin(waitForSourceChange: true);

    private static void Begin(bool waitForSourceChange)
    {
        if (File.Exists(ReportPath)) throw new IOException("探针报告已存在，请显式选择新的验证目录。");
        string config = Path.GetFullPath("Config.json");
        SessionState.SetBool(Key + ".config-existed", File.Exists(config));
        if (File.Exists(config)) SessionState.SetString(Key + ".config", Convert.ToBase64String(File.ReadAllBytes(config)));
        SessionState.SetString(Key, "compiling");
        SessionState.SetString(Key + ".started", DateTime.UtcNow.ToString("o"));
        // 新加载的来源 hook 必须实际参与本次完整编译。
        if (waitForSourceChange)
        {
            // 验证宿主加载后，由外部受控源码变更触发普通编译事件。
            SessionState.SetString(Key, "awaiting-refresh");
            File.WriteAllText("Logs/teio-export-stage1-20261005/refresh-ready.json", DateTime.UtcNow.ToString("o"));
        }
        else PMXExportBuildProvenance.RequestVerificationCompilation();
    }

    private static void ContinueAfterCompilation()
    {
        if (EditorApplication.isCompiling) { EditorApplication.delayCall += ContinueAfterCompilation; return; }
        try
        {
            SessionState.SetString(Key, "entering-play");
            EditorSceneManager.OpenScene("Assets/Scenes/Version2.unity");
            EditorApplication.isPlaying = true;
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || SessionState.GetString(Key, "") != "entering-play") return;
        try { SessionState.SetString(Key, "probe"); PMXStage1TeioProbe.StartProbe(); }
        catch (Exception exception) { Fail(exception); }
    }

    private static void Poll()
    {
        string phase = SessionState.GetString(Key, "");
        if (string.IsNullOrEmpty(phase)) return;
        const string trigger = "Logs/teio-export-stage1-20261005/refresh-request.json";
        if (phase == "awaiting-refresh" && File.Exists(trigger))
        {
            File.Delete(trigger);
            SessionState.SetString(Key, "compiling");
            AssetDatabase.Refresh();
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            return;
        }
        if (phase == "probe" && File.Exists(ReportPath))
        {
            JObject report = JObject.Parse(File.ReadAllText(ReportPath));
            if ((string)report["status"] == "running") return;
            RestoreConfig();
            SessionState.EraseString(Key);
            EditorApplication.Exit((string)report["status"] == "passed" ? 0 : 1);
            return;
        }
        if (DateTime.TryParse(SessionState.GetString(Key + ".started", ""), out DateTime start) &&
            DateTime.UtcNow - start.ToUniversalTime() > TimeSpan.FromMinutes(5))
            Fail(new TimeoutException("真实 Teio 批处理超时，阶段：" + phase));
    }

    private static void RestoreConfig()
    {
        // 启动器自动写配置；探针结束后还原原字节。
        if (SessionState.GetBool(Key + ".config-existed", false))
            File.WriteAllBytes("Config.json", Convert.FromBase64String(SessionState.GetString(Key + ".config", "")));
        else if (File.Exists("Config.json")) File.Delete("Config.json");
        SessionState.EraseString(Key + ".config");
    }

    private static void Fail(Exception exception)
    {
        RestoreConfig();
        SessionState.EraseString(Key);
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, new JObject { ["status"] = "failed", ["error"] = exception.ToString() }.ToString());
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }
}
