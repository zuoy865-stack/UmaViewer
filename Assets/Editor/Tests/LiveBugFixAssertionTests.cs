using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Gallop;
using Gallop.Live;
using Gallop.Live.Cyalume;

/// <summary>
/// 针对本次三大修复（Cyalume宿主防御、全局UI报错机制、1175天空盒与舞台预载）的自动化断言测试套件
/// </summary>
public static class LiveBugFixAssertionTests
{
    private static readonly string LogFilePath = @"C:\Users\JuziD\.gemini\antigravity\brain\bfbceab6-21ba-4d33-b8ab-fa39182ba6dd\scratch\assertion_test_report.txt";

    [MenuItem("UmaViewer/Run BugFix Assertion Tests")]
    public static bool RunAllTests()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=================================================");
        sb.AppendLine("=== LiveBugFix Assertion Tests Execution Report ===");
        sb.AppendLine($"=== Time: {DateTime.Now} ===");
        sb.AppendLine("=================================================\n");

        int total = 0;
        int passed = 0;
        int failed = 0;

        // 1. Cyalume 宿主判定隔离与回退断言测试
        RunTestCase("Cyalume: 宿主判定角色隔离与安全回退", ref total, ref passed, ref failed, sb, TestCyalumeHostFilterAndFallback);

        // 2. Cyalume inactive 协程启动防御与精准销毁测试
        RunTestCase("Cyalume: inactive 协程防御与子物件防误删", ref total, ref passed, ref failed, sb, TestCyalumeInactiveCoroutineDefenseAndSafeCleanup);

        // 3. 全局 UI 报错与音频错误通道断言测试
        RunTestCase("GlobalErrorToast: 跨场景顶层错误捕获与呈现", ref total, ref passed, ref failed, sb, TestGlobalErrorToastNotification);

        // 4. 1175 Live (10147) 舞台资源收集与天空材质预载覆盖测试
        RunTestCase("Stage10147: 天空材质与公共部件预载完整性覆盖", ref total, ref passed, ref failed, sb, TestStage10147PreloadCoverage);

        // 5. 防回归：常规舞台收集与边界保护测试
        RunTestCase("Regression: 常规舞台收集防回归与边界保护", ref total, ref passed, ref failed, sb, TestNoRegressionNormalStage);

        sb.AppendLine("\n=================================================");
        sb.AppendLine($"=== SUMMARY: Total={total}, PASSED={passed}, FAILED={failed} ===");
        sb.AppendLine("=================================================");

        bool allPassed = (failed == 0 && total > 0);
        try
        {
            File.WriteAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[LiveBugFixAssertionTests] Completed. AllPassed={allPassed}. Log: {LogFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LiveBugFixAssertionTests] Failed to write report: {ex}");
        }

        return allPassed;
    }

    private static void RunTestCase(string testName, ref int total, ref int passed, ref int failed, StringBuilder sb, Action testAction)
    {
        total++;
        sb.AppendLine($"--- [TEST {total}] {testName} ---");
        try
        {
            testAction.Invoke();
            passed++;
            sb.AppendLine("    RESULT: [PASS] 断言全部通过！\n");
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"    RESULT: [FAIL] 断言失败或发生未捕获异常: {ex.Message}");
            sb.AppendLine($"    Stack: {ex.StackTrace}\n");
            Debug.LogError($"[Assertion Failed] {testName}: {ex}");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception($"[断言失败] {message}");
        }
    }

    private static void AssertNotNull(object obj, string message)
    {
        if (obj == null)
        {
            throw new Exception($"[断言失败] 期望非空对象，实际为 null: {message}");
        }
    }

    /// <summary>
    /// 测试 1：验证 CyalumeAutoBinder 绝不会将角色身体或非荧光棒 AssetHolder 作为宿主，并能安全回退到根节点
    /// </summary>
    private static void TestCyalumeHostFilterAndFallback()
    {
        // 构造角色模型节点树
        var charaRoot = new GameObject("Test_Chara_Root");
        charaRoot.AddComponent<UmaContainerCharacter>();

        var bodyObj = new GameObject("pfb_bdy1093_00(Clone)");
        bodyObj.transform.SetParent(charaRoot.transform);
        var bodyHolder = bodyObj.AddComponent<AssetHolder>();

        // 构造舞台/mainLive 节点
        var mainLive = new GameObject("Test_MainLive");
        var binder = mainLive.AddComponent<CyalumeAutoBinder>();

        try
        {
            // 通过反射调用 private 的 ResolveControllerHost 方法
            var method = typeof(CyalumeAutoBinder).GetMethod("ResolveControllerHost", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            AssertNotNull(method, "CyalumeAutoBinder 应包含 ResolveControllerHost 方法");

            var resolvedHost = method.Invoke(binder, null) as GameObject;
            AssertNotNull(resolvedHost, "ResolveControllerHost 应返回有效宿主 GameObject");

            // 核心断言：绝对不能是角色身体或角色层级！
            AssertTrue(resolvedHost != bodyObj, "宿主不能是角色身体节点 pfb_bdy1093_00(Clone)");
            AssertTrue(resolvedHost != charaRoot, "宿主不能是角色根节点 UmaContainerCharacter");
            AssertTrue(resolvedHost.GetComponent<UmaContainerCharacter>() == null, "宿主身上不能带有 UmaContainerCharacter");
            AssertTrue(!resolvedHost.name.Contains("pfb_bdy"), "宿主名称不能包含角色身体前缀");

            // 核心断言：未找到合法 stage 宿主时应安全回退到 mainLive 本身
            AssertTrue(resolvedHost == mainLive, "未找到合法荧光棒宿主时，应安全回退到 mainLive 自身");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(charaRoot);
            UnityEngine.Object.DestroyImmediate(mainLive);
        }
    }

    /// <summary>
    /// 测试 2：验证 CyalumeController3D 在 inactive 状态下启动协程不报错，且清理时精准保护舞台/角色其他物件
    /// </summary>
    private static void TestCyalumeInactiveCoroutineDefenseAndSafeCleanup()
    {
        var host = new GameObject("Test_Inactive_Host");
        host.SetActive(false); // 模拟未激活状态

        var controller = host.AddComponent<CyalumeController3D>();

        try
        {
            // 核心断言 1：在 inactive 状态下调用预热和初始化，不会抛出协程异常
            controller.StartOfficialLikeSetup();

            // 核心断言 2：精准清理不会误伤其他子节点
            var stageLightFixture = new GameObject("StageLaserFixture_DontDelete");
            stageLightFixture.transform.SetParent(host.transform);

            // 调用内部清理方法
            var destroyMethod = typeof(CyalumeController3D).GetMethod("DestroyExistingCyalumeChildren",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            AssertNotNull(destroyMethod, "CyalumeController3D 应包含 DestroyExistingCyalumeChildren 方法");

            destroyMethod.Invoke(controller, null);

            // 断言舞台无关子节点依然完好存活
            AssertTrue(stageLightFixture != null, "普通舞台子节点在荧光棒清理后必须完好存活，不能被模糊匹配误删");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    /// <summary>
    /// 测试 3：验证全局 UI 报错在 LiveScene（UmaViewerUI 为 null）时依然能通过 UmaGlobalErrorToast 顶层呈现
    /// </summary>
    private static void TestGlobalErrorToastNotification()
    {
        // 确保初始化
        UmaGlobalErrorToast.Initialize();

        string testMsg = "测试伴奏缺失报错: 音频文件未找到";
        UmaErrorManager.ShowUIMessage(testMsg, UIMessageType.Error);

        // 断言全局 Toast 的最后一条消息记录符合预期
        string last = UmaGlobalErrorToast.GetLastMessage();
        AssertTrue(!string.IsNullOrEmpty(last), "UmaGlobalErrorToast 应记录最新弹出的错误文本");
        AssertTrue(last.Contains("测试伴奏缺失报错"), $"Toast 记录中应包含传入的错误内容，实际为: '{last}'");

        // 测试音频专用报错接口
        UmaErrorManager.ReportAudioLoadError("sound/live/1174/oke.awb", "文件损坏");
        string lastAudioErr = UmaGlobalErrorToast.GetLastMessage();
        AssertTrue(lastAudioErr.Contains("1174") || lastAudioErr.Contains("文件损坏") || lastAudioErr.Contains("sound"), 
            $"音频加载报错应成功投递至顶层 Toast，实际为: '{lastAudioErr}'");
    }

    /// <summary>
    /// 测试 4：验证 1175 Live (背景编号 10147) 的舞台预载范围已完整覆盖 sourceresources 材质与 common 公共部件
    /// </summary>
    private static void TestStage10147PreloadCoverage()
    {
        if (Config.Instance == null) new Config();
        Config.Instance.MainPath = @"C:\Users\JuziD\Umamusume\umamusume_Data\Persistent";

        var db = UmaDatabaseController.Instance;
        AssertNotNull(db.MetaEntries, "数据库 MetaEntries 应成功加载");
        Debug.Log($"[Test4 Debug] db.MetaEntries.Count = {db.MetaEntries?.Count}");

        // 构造一个 1175 Live 的虚拟 Entry，BackGroundId = "10147"
        var liveEntry = new LiveEntry("header\n0,0,0\n0,0,10147\n")
        {
            MusicId = 1175,
            BackGroundId = "10147"
        };

        // 确保在 EditMode 下 UmaViewerMain.Instance 具备有效实例与 MetaEntries 字典
        GameObject dummyMainObj = null;
        UmaViewerMain prevInstance = UmaViewerMain.Instance;
        try
        {
            dummyMainObj = new GameObject("Dummy_UmaViewerMain");
            var dummyMain = dummyMainObj.AddComponent<UmaViewerMain>();
            dummyMain.AbList = db.MetaEntries;
            UmaViewerMain.Instance = dummyMain;

            // 调用 Director.CollectStageBundleEntries
            var entries = Gallop.Live.Director.CollectStageBundleEntries(liveEntry, requireStage: true);
            AssertNotNull(entries, "CollectStageBundleEntries 应返回非空的 bundle 列表");
            AssertTrue(entries.Count > 0, $"1175 舞台收集到的 bundle 数量应大于 0，实际为: {entries.Count}");

            // 检查收集的资源名称
            var names = entries.Select(e => e.Name).ToList();

            // 核心断言 1：必须包含 sourceresources 下的天空材质包
            bool hasSky000 = names.Any(n => n.IndexOf("sourceresources/3d/env/live/live10147/materials/mtl_env_live10147_sky000", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasSky001 = names.Any(n => n.IndexOf("sourceresources/3d/env/live/live10147/materials/mtl_env_live10147_sky001", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertTrue(hasSky000, "预载列表必须包含 10147 专属基础天空材质 mtl_env_live10147_sky000");
            AssertTrue(hasSky001, "预载列表必须包含 10147 渐变天空材质 mtl_env_live10147_sky001");

            // 核心断言 2：必须包含 common 下的公共天空球预制体与荧光棒
            bool hasCmnSky = names.Any(n => n.IndexOf("3d/env/live/common/sky/pfb_env_live_cmn_sky002", StringComparison.OrdinalIgnoreCase) >= 0);
            bool hasCmnCyalume = names.Any(n => n.IndexOf("3d/env/live/common/cyalume/pfb_env_live_cmn_cyalume_controller027", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertTrue(hasCmnSky, "预载列表必须包含公共底天空球预制体 pfb_env_live_cmn_sky002");
            AssertTrue(hasCmnCyalume, "预载列表必须包含公共荧光棒控制器 pfb_env_live_cmn_cyalume_controller027");

            // 核心断言 3：必须保留原有的 10147 专属预制体（如舞台主控制器）
            bool hasController = names.Any(n => n.IndexOf("3d/env/live/live10147/pfb_env_live10147_controller000", StringComparison.OrdinalIgnoreCase) >= 0);
            AssertTrue(hasController, "预载列表必须包含舞台主控制器 pfb_env_live10147_controller000");
        }
        finally
        {
            UmaViewerMain.Instance = prevInstance;
            if (dummyMainObj != null)
            {
                UnityEngine.Object.DestroyImmediate(dummyMainObj);
            }
        }
    }

    /// <summary>
    /// 测试 5：防回归测试，验证普通舞台（非10147）和空条件下的预载逻辑依然稳健
    /// </summary>
    private static void TestNoRegressionNormalStage()
    {
        // 测试空背景
        var emptyLive = new LiveEntry("header\n0,0,0\n0,0,\n") { BackGroundId = "" };
        var emptyEntries = Gallop.Live.Director.CollectStageBundleEntries(emptyLive, requireStage: false);
        AssertNotNull(emptyEntries, "空背景不应抛异常，应返回合法列表");

        // 测试 requireStage 为 false
        var normalLive = new LiveEntry("header\n0,0,0\n0,0,1001\n") { BackGroundId = "1001" };
        var stageDisabled = Gallop.Live.Director.CollectStageBundleEntries(normalLive, requireStage: false);
        AssertNotNull(stageDisabled, "requireStage=false 时不应抛异常");
        AssertTrue(stageDisabled.Count == 0, "requireStage 为 false 时应返回空列表");
    }
}
