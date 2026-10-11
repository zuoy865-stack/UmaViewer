using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live 选人音轨提示与模式隔离自动化断言测试套件
/// 覆盖：
/// 1. 音轨文件名解析逻辑 ParseVocalCharaId 与格式过滤匹配规则；
/// 2. 筛选模式切换流转状态机与彻底重置逻辑；
/// 3. 原始条目顺序记录与退出选人后的精准恢复逻辑。
/// </summary>
public static class LiveVocalSelectAssertionTests
{
    private static readonly string LogFilePath = @"C:\Users\JuziD\.gemini\antigravity\brain\b8e0478a-498c-482e-9002-7affb9c2a261\scratch\vocal_select_assertion_report.txt";

    [MenuItem("UmaViewer/Run LiveVocalSelect Assertion Tests")]
    public static bool RunAllTests()
    {
        var sb = new StringBuilder();
        sb.AppendLine("==================================================================");
        sb.AppendLine("=== LiveVocalSelect Assertion Tests Execution Report =============");
        sb.AppendLine($"=== Time: {DateTime.Now} ===");
        sb.AppendLine("==================================================================\n");

        int total = 0;
        int passed = 0;
        int failed = 0;

        // 1. 音轨解析逻辑 ParseVocalCharaId / 匹配规则测试
        RunTestCase("VocalParser: 音轨文件名解析与角色 ID 提取匹配规则", ref total, ref passed, ref failed, sb, TestVocalAudioParsingAndMatchingRules);

        // 2. 筛选模式切换流转与状态机重置测试
        RunTestCase("StateMachine: 筛选模式循环流转与退出彻底重置", ref total, ref passed, ref failed, sb, TestFilterModeStateFlowAndReset);

        // 3. 原始条目顺序记录与退出恢复逻辑测试
        RunTestCase("OrderPreservation: 原始层级记录、置顶筛选与退出精准恢复", ref total, ref passed, ref failed, sb, TestOriginalOrderRecordingAndRestoration);

        sb.AppendLine("\n==================================================================");
        sb.AppendLine($"=== SUMMARY: Total={total}, PASSED={passed}, FAILED={failed} ===");
        sb.AppendLine("==================================================================");

        bool allPassed = (failed == 0 && total > 0);
        try
        {
            string dir = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[LiveVocalSelectAssertionTests] Completed. AllPassed={allPassed}. Log: {LogFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LiveVocalSelectAssertionTests] Failed to write report: {ex}");
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

    private static void AssertEquals<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"[断言失败] 期望值: {expected}, 实际值: {actual}. {message}");
        }
    }

    /// <summary>
    /// 测试 1：验证音轨文件名解析 ParseVocalCharaId 及其边界过滤规则
    /// </summary>
    public static void TestVocalAudioParsingAndMatchingRules()
    {
        int targetMusicId = 1001;

        // 1. 标准完整命名格式匹配（带路径与不带路径）
        bool res1 = LiveVocalSelectManager.ParseVocalCharaId("sound/l/1001/snd_bgm_live_1001_chara_1001_01.awb", targetMusicId, out int chara1);
        AssertTrue(res1, "标准带路径音轨应匹配成功");
        AssertEquals(1001, chara1, "角色 ID 应正确解析为 1001");

        // 2. 多分轨角色音轨命名匹配
        bool res2 = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_chara_1052_02.awb", targetMusicId, out int chara2);
        AssertTrue(res2, "多分轨音轨应匹配成功");
        AssertEquals(1052, chara2, "角色 ID 应正确解析为 1052");

        // 3. 无分轨序号后缀变体命名
        bool res3 = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_chara_1024.awb", targetMusicId, out int chara3);
        AssertTrue(res3, "无分轨序号后缀变体应匹配成功");
        AssertEquals(1024, chara3, "角色 ID 应正确解析为 1024");

        // 4. 三位数与五位数角色 ID 变体匹配
        bool res4 = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_chara_105_01.awb", targetMusicId, out int chara4);
        AssertTrue(res4, "三位数角色 ID 应匹配成功");
        AssertEquals(105, chara4, "角色 ID 应正确解析为 105");

        // 5. 跨歌曲隔离过滤：歌曲 ID 不同的音轨必须被拒绝
        bool resOtherSong = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1002_chara_1001_01.awb", targetMusicId, out _);
        AssertTrue(!resOtherSong, "歌曲 ID 不符的音轨必须被严格拒绝");

        // 6. 伴奏与非角色音轨过滤
        bool resOke = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_oke_01.awb", targetMusicId, out _);
        AssertTrue(!resOke, "伴奏音轨 (oke) 不能被当作角色音轨匹配");

        bool resPreview = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_preview_01.awb", targetMusicId, out _);
        AssertTrue(!resPreview, "预听音频 (preview) 不能被当作角色音轨匹配");

        // 7. 非 awb 格式扩展名过滤
        bool resAcb = LiveVocalSelectManager.ParseVocalCharaId("snd_bgm_live_1001_chara_1001_01.acb", targetMusicId, out _);
        AssertTrue(!resAcb, "非 awb 扩展名文件不能被当作音轨匹配");

        // 8. 非法与空输入边界保护
        bool resNull = LiveVocalSelectManager.ParseVocalCharaId(null, targetMusicId, out _);
        AssertTrue(!resNull, "null 输入应安全返回 false");

        bool resEmpty = LiveVocalSelectManager.ParseVocalCharaId("", targetMusicId, out _);
        AssertTrue(!resEmpty, "空字符串应安全返回 false");

        bool resNoise = LiveVocalSelectManager.ParseVocalCharaId("sound/env/snd_se_footstep_01.awb", targetMusicId, out _);
        AssertTrue(!resNoise, "非 Live 音效文件应安全返回 false");
    }

    /// <summary>
    /// 测试 2：验证筛选模式循环切换状态机流转与退出彻底重置
    /// </summary>
    public static void TestFilterModeStateFlowAndReset()
    {
        var manager = LiveVocalSelectManager.Instance;
        AssertTrue(manager != null, "LiveVocalSelectManager 实例必须有效");

        // 先恢复到初始状态
        manager.ExitLiveSelectMode();
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.All, manager.CurrentFilterMode, "初始模式应为 All (0)");
        AssertTrue(!manager.IsLiveSelectMode, "初始状态下 IsLiveSelectMode 应为 false");

        // 状态机循环流转验证：All (0) -> VocalFirst (1) -> VocalOnly (2) -> All (0)
        manager.CycleFilterMode();
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.VocalFirst, manager.CurrentFilterMode, "第一次切换后模式应为 VocalFirst (1)");

        manager.CycleFilterMode();
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.VocalOnly, manager.CurrentFilterMode, "第二次切换后模式应为 VocalOnly (2)");

        manager.CycleFilterMode();
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.All, manager.CurrentFilterMode, "第三次切换后模式应重置回 All (0)");

        // 模拟进入 Live 选人模式
        int testMusicId = 2001;
        manager.EnterLiveSelectMode(testMusicId, null);
        AssertTrue(manager.IsLiveSelectMode, "调用 EnterLiveSelectMode 后 IsLiveSelectMode 应为 true");
        AssertEquals(testMusicId, manager.CurrentMusicId, "CurrentMusicId 应正确记录为 2001");

        // 切换至非默认筛选模式
        manager.CycleFilterMode(); // 切换为 VocalFirst
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.VocalFirst, manager.CurrentFilterMode, "当前模式应为 VocalFirst");

        // 调用 ExitLiveSelectMode 退出选人模式
        manager.ExitLiveSelectMode();

        // 严格断言重置契约
        AssertTrue(!manager.IsLiveSelectMode, "退出后 IsLiveSelectMode 必须重置为 false");
        AssertEquals(-1, manager.CurrentMusicId, "退出后 CurrentMusicId 必须重置为 -1");
        AssertTrue(manager.CurrentLiveSlot == null, "退出后 CurrentLiveSlot 必须重置为 null");
        AssertEquals(LiveVocalSelectManager.VocalFilterMode.All, manager.CurrentFilterMode, "退出后筛选模式必须彻底重置回 All");
    }

    /// <summary>
    /// 测试 3：验证原始层级顺序记录、置顶筛选重排以及退出时的精准恢复
    /// </summary>
    public static void TestOriginalOrderRecordingAndRestoration()
    {
        var manager = LiveVocalSelectManager.Instance;
        manager.ExitLiveSelectMode();
        manager.ClearRegisteredItems();
        manager.ClearVocalCache();

        // 创建虚拟父级面板与 4 个测试角色条目
        var rootPanel = new GameObject("TestRootPanel", typeof(RectTransform));
        var charaItems = new List<UmaUIContainer>();
        int[] charaIds = new int[] { 101, 102, 103, 104 };

        try
        {
            for (int i = 0; i < charaIds.Length; i++)
            {
                var itemGo = new GameObject($"CharaItem_{charaIds[i]}", typeof(RectTransform), typeof(UmaUIContainer));
                itemGo.transform.SetParent(rootPanel.transform, false);
                itemGo.transform.SetSiblingIndex(i); // 显式设定初始层级顺序 0, 1, 2, 3

                var container = itemGo.GetComponent<UmaUIContainer>();
                charaItems.Add(container);

                // 注册到管理器
                manager.RegisterCharacterItem(charaIds[i], container);
            }

            AssertEquals(4, manager.RegisteredItemCount, "应成功注册 4 个角色条目");

            // 验证条目初始 SiblingIndex 均为 0, 1, 2, 3
            for (int i = 0; i < charaItems.Count; i++)
            {
                AssertEquals(i, charaItems[i].transform.GetSiblingIndex(), $"条目 {charaIds[i]} 初始层级必须为 {i}");
            }

            // 为歌曲 3001 注入音轨数据：仅 103 和 104 拥有音轨
            int testMusicId = 3001;
            manager.SetVocalCacheForTest(testMusicId, new int[] { 103, 104 });

            // 1. 进入 Live 选人模式（默认模式为 All）
            manager.EnterLiveSelectMode(testMusicId, null);

            // 检查音轨小图标激活状态：103/104 激活，101/102 隐藏
            var badge101 = charaItems[0].transform.Find("VocalBadge");
            var badge102 = charaItems[1].transform.Find("VocalBadge");
            var badge103 = charaItems[2].transform.Find("VocalBadge");
            var badge104 = charaItems[3].transform.Find("VocalBadge");

            AssertTrue(badge101 != null && !badge101.gameObject.activeSelf, "角色 101 无音轨，徽标必须隐藏");
            AssertTrue(badge102 != null && !badge102.gameObject.activeSelf, "角色 102 无音轨，徽标必须隐藏");
            AssertTrue(badge103 != null && badge103.gameObject.activeSelf, "角色 103 有音轨，徽标必须显示");
            AssertTrue(badge104 != null && badge104.gameObject.activeSelf, "角色 104 有音轨，徽标必须显示");

            // 2. 切换至“音轨优先”模式 (VocalFirst)
            manager.CurrentFilterMode = LiveVocalSelectManager.VocalFilterMode.VocalFirst;

            // 断言：有音轨的 103 和 104 必须排在前面（Sibling 0 和 1），无音轨的 101 和 102 排在后面（Sibling 2 和 3）
            int idx101 = charaItems[0].transform.GetSiblingIndex();
            int idx102 = charaItems[1].transform.GetSiblingIndex();
            int idx103 = charaItems[2].transform.GetSiblingIndex();
            int idx104 = charaItems[3].transform.GetSiblingIndex();

            AssertTrue(idx103 < 2 && idx104 < 2, "有音轨的角色 103 和 104 在音轨优先模式下必须置顶在前两位");
            AssertTrue(idx101 >= 2 && idx102 >= 2, "无音轨的角色 101 和 102 在音轨优先模式下必须排在后两位");

            // 3. 切换至“仅限音轨”模式 (VocalOnly)
            manager.CurrentFilterMode = LiveVocalSelectManager.VocalFilterMode.VocalOnly;

            // 断言：有音轨的角色保持激活，无音轨的角色被隐藏
            AssertTrue(!charaItems[0].gameObject.activeSelf, "仅限音轨模式下，无音轨角色 101 必须被隐藏");
            AssertTrue(!charaItems[1].gameObject.activeSelf, "仅限音轨模式下，无音轨角色 102 必须被隐藏");
            AssertTrue(charaItems[2].gameObject.activeSelf, "仅限音轨模式下，有音轨角色 103 必须保持显示");
            AssertTrue(charaItems[3].gameObject.activeSelf, "仅限音轨模式下，有音轨角色 104 必须保持显示");

            // 4. 彻底退出 Live 选人模式（模拟恢复到普通角色预览模式）
            manager.ExitLiveSelectMode();

            // 核心断言 A：所有角色卡片的小图标全部隐藏，绝无残留
            AssertTrue(!badge101.gameObject.activeSelf, "退出 Live 模式后，角色 101 的音轨徽标必须关闭");
            AssertTrue(!badge102.gameObject.activeSelf, "退出 Live 模式后，角色 102 的音轨徽标必须关闭");
            AssertTrue(!badge103.gameObject.activeSelf, "退出 Live 模式后，角色 103 的音轨徽标必须彻底关闭，杜绝残留");
            AssertTrue(!badge104.gameObject.activeSelf, "退出 Live 模式后，角色 104 的音轨徽标必须彻底关闭，杜绝残留");

            // 核心断言 B：所有角色条目全部恢复激活可见
            AssertTrue(charaItems[0].gameObject.activeSelf, "退出 Live 模式后，角色 101 必须重新激活显示");
            AssertTrue(charaItems[1].gameObject.activeSelf, "退出 Live 模式后，角色 102 必须重新激活显示");
            AssertTrue(charaItems[2].gameObject.activeSelf, "退出 Live 模式后，角色 103 必须重新激活显示");
            AssertTrue(charaItems[3].gameObject.activeSelf, "退出 Live 模式后，角色 104 必须重新激活显示");

            // 核心断言 C：原始层级顺序必须 100% 精确恢复为 0, 1, 2, 3，绝不能打乱原始顺序
            AssertEquals(0, charaItems[0].transform.GetSiblingIndex(), "角色 101 的层级必须精准恢复为原始序号 0");
            AssertEquals(1, charaItems[1].transform.GetSiblingIndex(), "角色 102 的层级必须精准恢复为原始序号 1");
            AssertEquals(2, charaItems[2].transform.GetSiblingIndex(), "角色 103 的层级必须精准恢复为原始序号 2");
            AssertEquals(3, charaItems[3].transform.GetSiblingIndex(), "角色 104 的层级必须精准恢复为原始序号 3");
        }
        finally
        {
            // 清理测试临时创建的 GameObject 并重置管理器条目映射
            manager.ClearRegisteredItems();
            manager.ClearVocalCache();
            UnityEngine.Object.DestroyImmediate(rootPanel);
        }
    }
}
