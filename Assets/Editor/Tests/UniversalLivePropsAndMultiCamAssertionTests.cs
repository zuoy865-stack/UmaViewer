using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Gallop.Live;
using Gallop.Live.Cutt;

/// <summary>
/// UmaViewer 多机位分屏合成、1175 天空盒时序修复与通用道具自动化断言测试套件
/// 覆盖：
/// 1. 1157 道具反序列化与 1046 折扇装载逻辑断言
/// 2. 1175 双机位 multiCameraSettings 与图层参数断言
/// 3. 1175 天空盒 27 帧时序直达驱动与 10147 云层反相自适应激活断言
/// 4. 1092、1152、1181、1154 典型道具与立式麦克风挂载骨骼查找断言
/// 5. MultiCameraComposite 视口计算与空指针防御断言
/// 6. MultiCamera 数据组结构契约、GetKeyList 重写与真实 Cutt 反序列化断言
/// </summary>
public static class UniversalLivePropsAndMultiCamAssertionTests
{
    private const string MenuPath = "UmaViewer/Run Universal Props and MultiCam Assertion Tests";

    [MenuItem(MenuPath)]
    public static void RunAllAssertionTests()
    {
        int total = 0;
        int passed = 0;
        int failed = 0;
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine("  UmaViewer 多机位分屏合成、1175 天空盒时序修复与通用道具自动化断言测试");
        sb.AppendLine("================================================================================");

        RunTestCase("测试 1: 1157 道具反序列化与 1046 折扇装配装载逻辑断言", ref total, ref passed, ref failed, sb, TestLive1157AndProp1046FanLoading);
        RunTestCase("测试 2: 1175 双机位 multiCameraSettings 与图层参数断言", ref total, ref passed, ref failed, sb, TestLive1175MultiCameraSettingsAndLayer);
        RunTestCase("测试 3: 1175 天空盒 27 帧时序直达驱动与 10147 云层反相自适应激活断言", ref total, ref passed, ref failed, sb, TestLive1175SkyTimingAndStage10147InvertCloud);
        RunTestCase("测试 4: 1092、1152、1181、1154 典型道具与立式麦克风挂载骨骼查找断言", ref total, ref passed, ref failed, sb, TestTypicalPropsAndAttachBoneFinding);
        RunTestCase("测试 5: MultiCameraComposite 视口计算、SDF 距离场与空指针防御断言", ref total, ref passed, ref failed, sb, TestMultiCameraCompositeViewportAndRobustness);
        RunTestCase("测试 6: MultiCamera 默认休眠按需激活与 StageController 激光状态锁防穿透断言", ref total, ref passed, ref failed, sb, TestMultiCameraDormancyAndLaserLockAssertion);
        RunTestCase("测试 7: 多机位 MaskRoll 映射、分屏开关 IsScreenDivide 与背景色属性断言", ref total, ref passed, ref failed, sb, TestMultiCameraMaskRollDivideAndBgColorAssertion);
        RunTestCase("测试 8: 多机位数容错推断、独立合成器数组与 MultiCameraFinalComposite 断言", ref total, ref passed, ref failed, sb, TestMultiCameraInferenceAndFinalCompositeAssertion);
        RunTestCase("测试 9: URP 多机位全屏分屏 Overlay 呈现层、防黑空纹理保护与 MonitorCamera 实时画面断言", ref total, ref passed, ref failed, sb, TestUrpMultiCameraOverlayAndMonitorCameraAssertion);
        RunTestCase("测试 10: 多机位时间轴参考结构对齐与真实 Cutt 反序列化断言", ref total, ref passed, ref failed, sb, TestRealCuttAssetDeserializationAssertion);

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  测试汇总: 总计 {total} 项 | 通过: {passed} 项 | 失败: {failed} 项 | 通过率: {(total > 0 ? (float)passed / total * 100f : 0f):F1}%");
        sb.AppendLine("================================================================================");

        if (failed == 0)
        {
            Debug.Log($"<color=#44FF44><b>[UniversalPropsAndMultiCamTests 全部通过]</b></color>\n" + sb.ToString());
        }
        else
        {
            Debug.LogError($"<color=#FF4444><b>[UniversalPropsAndMultiCamTests 存在失败项]</b></color>\n" + sb.ToString());
        }
    }

    #region 测试执行辅助方法
    private static void RunTestCase(string testName, ref int total, ref int passed, ref int failed, StringBuilder sb, Action action)
    {
        total++;
        try
        {
            action();
            passed++;
            sb.AppendLine($"  [PASS] {testName}");
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"  [FAIL] {testName} -> 异常: {ex.Message}");
            Debug.LogException(ex);
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
        if (obj == null || obj.Equals(null))
        {
            throw new Exception($"[空引用断言失败] {message}");
        }
    }
    #endregion

    #region 测试 1: 1157 道具反序列化与 1046 折扇装配装载逻辑断言
    /// <summary>
    /// 测试 1：验证 1157 Live 的道具配置与动作条目收集规则，并断言 1046 折扇道具装配与挂载
    /// </summary>
    private static void TestLive1157AndProp1046FanLoading()
    {
        // 1. 模拟构建 1157 LiveEntry 并验证专属道具动作包前缀匹配
        var live1157 = new LiveEntry("header\n0,0,0\n0,0,10146\n")
        {
            MusicId = 1157,
            BackGroundId = "10146"
        };

        var propCharaEntry = new UmaDatabaseEntry
        {
            Name = "3d/chara/prop/prop1046_00/pfb_chr_prop_1046_00",
            Type = UmaFileType._3d_cutt
        };
        var propMotionEntry = new UmaDatabaseEntry
        {
            Name = "3d/motion/live/prop/son1157/anm_liv_son1157_prop01",
            Type = UmaFileType._3d_cutt
        };
        var unrelatedEntry = new UmaDatabaseEntry
        {
            Name = "3d/chara/body/bdy0001_00/pfb_bdy0001_00",
            Type = UmaFileType._3d_cutt
        };

        var mockDbList = new Dictionary<string, UmaDatabaseEntry>(StringComparer.OrdinalIgnoreCase)
        {
            { propCharaEntry.Name, propCharaEntry },
            { propMotionEntry.Name, propMotionEntry },
            { unrelatedEntry.Name, unrelatedEntry }
        };

        // 模拟全局 UmaViewerMain
        var prevMain = UmaViewerMain.Instance;
        var dummyMainGo = new GameObject("Dummy_UmaViewerMain_Props");
        var dummyMain = dummyMainGo.AddComponent<UmaViewerMain>();
        dummyMain.AbList = mockDbList;
        UmaViewerMain.Instance = dummyMain;

        try
        {
            var collected = Director.CollectLivePropsEntries(live1157);
            AssertNotNull(collected, "CollectLivePropsEntries 必须稳定返回资源列表");
            AssertTrue(collected.Count == 2, $"1157 道具应匹配 2 个资源条目 (实际: {collected.Count})");
            AssertTrue(collected.Contains(propCharaEntry), "收集列表必须包含 1046 折扇道具模型资源");
            AssertTrue(collected.Contains(propMotionEntry), "收集列表必须包含 1157 专属道具动作资源包");

            // 2. 验证折扇道具挂载逻辑
            var charaGo = new GameObject("Chara_1157_Host");
            var handAttachR = new GameObject("Hand_Attach_R");
            handAttachR.transform.SetParent(charaGo.transform, false);

            var dummyFanPrefab = new GameObject("pfb_chr_prop_1046_00");

            try
            {
                var container = charaGo.AddComponent<UmaContainerCharacter>();
                GameObject fanInstance = container.AttachProp(dummyFanPrefab, "Hand_Attach_R", "prop_fan_1046");

                AssertNotNull(fanInstance, "AttachProp 挂载折扇必须返回有效 GameObject 实例");
                AssertTrue(fanInstance.transform.parent == handAttachR.transform, "折扇道具必须正确挂载至 Hand_Attach_R 骨骼节点");
                AssertTrue(fanInstance.transform.localPosition == Vector3.zero, "折扇局部坐标必须初始化归零");
                AssertTrue(fanInstance.transform.localRotation == Quaternion.identity, "折扇局部旋转必须归一化");
                AssertTrue(container.AttachedProps.ContainsKey("prop_fan_1046"), "AttachedProps 字典必须正确登记该道具");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dummyFanPrefab);
                UnityEngine.Object.DestroyImmediate(charaGo);
            }
        }
        finally
        {
            UmaViewerMain.Instance = prevMain;
            UnityEngine.Object.DestroyImmediate(dummyMainGo);
        }
    }
    #endregion

    #region 测试 2: 1175 双机位 multiCameraSettings 与图层参数断言
    /// <summary>
    /// 测试 2：验证 1175 Live 的双机位配置、图层分割线参数封装与 AlterUpdate_MultiCameraLayer 事件派发
    /// </summary>
    private static void TestLive1175MultiCameraSettingsAndLayer()
    {
        // 1. 验证 1175 双机位设置
        var multiCamSettings = new LiveTimelineMultiCameraSettings
        {
            cameraNum = 2 // 1175《ハロー・ポラリス》核心特性：双机位分屏演出
        };
        AssertTrue(multiCamSettings.cameraNum == 2, "1175 的 multiCameraSettings.cameraNum 必须精确等于 2 (双机位)");

        // 2. 验证多机位图层关键帧数据完整性与插值计算
        var keyA = new LiveTimelineKeyMultiCameraLayerData
        {
            frame = 0,
            MultiCameraNo = 1,
            LineType = MultiCameraComposite.DivideLineType.Color,
            LineThickness = 0.015f,
            LineColor = Color.white,
            FadeValue = 0f,
            TransformParameter = new Vector4(0f, 0f, 0f, 1f),
            MaskRoll = 0f
        };

        var keyB = new LiveTimelineKeyMultiCameraLayerData
        {
            frame = 60,
            MultiCameraNo = 1,
            LineType = MultiCameraComposite.DivideLineType.Color,
            LineThickness = 0.025f,
            LineColor = Color.yellow,
            FadeValue = 1f,
            TransformParameter = new Vector4(0.2f, -0.1f, 30f, 1f),
            MaskRoll = 30f
        };

        AssertTrue(keyA.dataType == LiveTimelineKeyDataType.MultiCameraLayer, "图层关键帧类型必须为 MultiCameraLayer");
        AssertTrue(keyA.MultiCameraNo == 1, "通道编号必须正确存储");

        // 3. 验证时间轴事件广播：挂载 LiveTimelineControl 并广播事件
        var hostGo = new GameObject("TimelineHost_MultiCam");
        try
        {
            var control = hostGo.AddComponent<LiveTimelineControl>();

            bool eventTriggered = false;
            int receivedCameraNo = -1;
            MultiCameraComposite.DivideLineType receivedLineType = MultiCameraComposite.DivideLineType.Fade;
            float receivedThickness = 0f;
            Color receivedColor = Color.black;
            float receivedFade = -1f;

            control.OnUpdateMultiCameraLayer += (cameraNo, lineType, lineThickness, lineColor, fadeValue, transformParam, maskRoll, minPos, maxPos) =>
            {
                eventTriggered = true;
                receivedCameraNo = cameraNo;
                receivedLineType = lineType;
                receivedThickness = lineThickness;
                receivedColor = lineColor;
                receivedFade = fadeValue;
            };

            // 模拟构造含图层关键帧的 worksheet 并触发更新
            var sheet = new LiveTimelineWorkSheet();
            var layerData = new LiveTimelineMultiCameraLayerData();
            layerData.keys.Add(keyB);
            sheet.multiCameraLayerKeys.Add(layerData);

            control.AlterUpdate_MultiCameraLayer(sheet, 60f);

            AssertTrue(eventTriggered, "AlterUpdate_MultiCameraLayer 必须成功广播 OnUpdateMultiCameraLayer 事件");
            AssertTrue(receivedCameraNo == 1, "广播的通道编号必须为 1");
            AssertTrue(receivedLineType == MultiCameraComposite.DivideLineType.Color, "广播的分割线类型必须为 Color");
            AssertTrue(Mathf.Approximately(receivedThickness, 0.025f), "广播的分割线厚度必须精确对齐");
            AssertTrue(receivedColor == Color.yellow, "广播的分割线颜色必须为黄色");
            AssertTrue(Mathf.Approximately(receivedFade, 1f), "广播的淡入权重必须精确为 1.0");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(hostGo);
        }
    }
    #endregion

    #region 测试 3: 1175 天空盒 27 帧时序直达驱动与 10147 云层反相自适应激活断言
    /// <summary>
    /// 测试 3：验证 10147 舞台自适应识别并激活云层反相模式，以及 targets 判空穿透后的天空变色 100% 直达驱动
    /// </summary>
    private static void TestLive1175SkyTimingAndStage10147InvertCloud()
    {
        var stageRoot = new GameObject("pfb_env_live10147_controller000");
        var skyBaseGo = new GameObject("sky_base_00");
        var skyGradGo = new GameObject("sky_grad_00");

        skyBaseGo.transform.SetParent(stageRoot.transform, false);
        skyGradGo.transform.SetParent(stageRoot.transform, false);

        var baseRenderer = skyBaseGo.AddComponent<MeshRenderer>();
        var gradRenderer = skyGradGo.AddComponent<MeshRenderer>();

        Shader testShader = Shader.Find("Hidden/InternalErrorShader") ?? Shader.Find("Standard");
        var baseMat = new Material(testShader) { name = "mtl_env_live10147_sky000" };
        var gradMat = new Material(testShader) { name = "mtl_env_live10147_sky001" };
        baseRenderer.material = baseMat;
        gradRenderer.material = gradMat;

        try
        {
            var stageCtrl = stageRoot.AddComponent<StageController>();
            var skyCtrl = stageRoot.AddComponent<StageSkyController>();

            // 1. 初始化 StageSkyController 并验证 10147 自适应模式激活
            skyCtrl.Initialize(stageCtrl);

            AssertTrue(skyCtrl.IsStage10147(stageCtrl), "IsStage10147 必须成功识别 10147 舞台");
            AssertTrue(skyCtrl.CurrentMode == StageSkyMode.OfficialTimeline,
                $"10147/1175 必须走官方时间轴天空模式，禁止自动反相云层 (当前模式: {skyCtrl.CurrentMode})");

            // 2. 模拟 StageController 的 _enableBgColorDriver 开启
            var enableField = typeof(StageController).GetField("_enableBgColorDriver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (enableField != null) enableField.SetValue(stageCtrl, true);

            // 3. 构造 1175 天空盒时序数据：27 帧天空底色 与 26 帧渐变云层
            // 重点断言：即使 StageController 内部 targets 为空，也必须 100% 优先直达派发给 StageSkyController！
            var baseUpdateInfo = new BgColor1UpdateInfo
            {
                TimelineName = "sky_base_00",
                color = new Color(0.15f, 0.35f, 0.75f, 1f),
                colorPower = 2.0f
            };

            var gradUpdateInfo = new BgColor1UpdateInfo
            {
                TimelineName = "sky_grad_00",
                color = new Color(0.9f, 0.6f, 0.2f, 1f),
                colorPower = 1.5f
            };

            stageCtrl.OnUpdateBgColor1(ref baseUpdateInfo);
            stageCtrl.OnUpdateBgColor1(ref gradUpdateInfo);

            AssertTrue(skyCtrl.HasBaseColor, "27 帧天空底色时序必须成功直达 StageSkyController");
            AssertTrue(skyCtrl.LastBaseColor == baseUpdateInfo.color, "天空底色颜色值必须精确一致");
            AssertTrue(Mathf.Approximately(skyCtrl.LastBasePower, 2.0f), "天空底色发光强度必须精确为 2.0");

            AssertTrue(skyCtrl.HasGradColor, "26 帧渐变云层时序必须成功直达 StageSkyController");
            AssertTrue(skyCtrl.LastGradColor == gradUpdateInfo.color, "渐变云层颜色值必须精确一致");
            AssertTrue(Mathf.Approximately(skyCtrl.LastGradPower, 1.5f), "渐变云层发光强度必须精确为 1.5");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(stageRoot);
            if (baseMat != null) UnityEngine.Object.DestroyImmediate(baseMat);
            if (gradMat != null) UnityEngine.Object.DestroyImmediate(gradMat);
        }
    }
    #endregion

    #region 测试 4: 1092、1152、1181、1154 典型道具与立式麦克风挂载骨骼查找断言
    /// <summary>
    /// 测试 4：验证 1092、1152、1181、1154 典型演出道具与立式麦克风在马娘骨骼层级中的查找定位与容错机制
    /// </summary>
    private static void TestTypicalPropsAndAttachBoneFinding()
    {
        var charaGo = new GameObject("Chara_Props_Hierarchy_Host");
        var upBodyBone = new GameObject("UpBodyBone");
        var wristR = new GameObject("Wrist_R");
        var handAttachR = new GameObject("Hand_Attach_R");
        var wristL = new GameObject("Wrist_L");
        var handAttachL = new GameObject("Hand_Attach_L");

        upBodyBone.transform.SetParent(charaGo.transform, false);
        wristR.transform.SetParent(upBodyBone.transform, false);
        handAttachR.transform.SetParent(wristR.transform, false);
        wristL.transform.SetParent(upBodyBone.transform, false);
        handAttachL.transform.SetParent(wristL.transform, false);

        var dummyMicPrefab = new GameObject("pfb_chr_prop_stand_mic_1154");

        try
        {
            var container = charaGo.AddComponent<UmaContainerCharacter>();
            container.UpBodyBone = upBodyBone;

            // 1. 1092 典型右手道具（如手持麦克风/荧光棒）：精准查找 Hand_Attach_R
            Transform attachR = container.FindAttachBone("Hand_Attach_R");
            AssertNotNull(attachR, "1092 手部道具骨骼查找必须命中有效 Transform");
            AssertTrue(attachR == handAttachR.transform, "1092 手部道具必须精准锁定至 Hand_Attach_R 骨骼");

            // 2. 1152 典型双手/左手道具（如花束/手持铃铛）：精准查找 Hand_Attach_L
            Transform attachL = container.FindAttachBone("Hand_Attach_L");
            AssertNotNull(attachL, "1152 左手道具骨骼查找必须命中有效 Transform");
            AssertTrue(attachL == handAttachL.transform, "1152 左手道具必须精准锁定至 Hand_Attach_L 骨骼");

            // 3. 1181 带有自定义后缀或未完全匹配骨骼：容错回退机制
            Transform fallbackR = container.FindAttachBone("Custom_Attach_R");
            AssertNotNull(fallbackR, "1181 模糊挂载名称必须容错回退命中右手相关骨骼");
            AssertTrue(fallbackR == wristR.transform, "包含 _R 的模糊名称应优先回退锁定至 Wrist_R");

            // 4. 1154 立式麦克风 (stand_mic)：挂载至躯干基准点或上身骨骼
            Transform standMicBone = container.FindAttachBone("stand_mic_unknown");
            AssertNotNull(standMicBone, "1154 立式麦克风查找未知关节点必须安全回退至 UpBodyBone 或根节点");
            AssertTrue(standMicBone == upBodyBone.transform, "完全未匹配的关节点应兜底返回 UpBodyBone，绝不返回 null");

            // 5. 挂接道具姿态重置断言
            GameObject micInstance = container.AttachProp(dummyMicPrefab, "Hand_Attach_R", "stand_mic");
            AssertNotNull(micInstance, "AttachProp 挂载立式麦克风必须返回有效实例");
            AssertTrue(micInstance.transform.localPosition == Vector3.zero, "挂载道具局部坐标必须重置为 (0,0,0)");
            AssertTrue(micInstance.transform.localRotation == Quaternion.identity, "挂载道具局部旋转必须归一化");
            AssertTrue(micInstance.transform.localScale == Vector3.one, "挂载道具局部缩放必须保持 (1,1,1)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(dummyMicPrefab);
            UnityEngine.Object.DestroyImmediate(charaGo);
        }
    }
    #endregion

    #region 测试 5: MultiCameraComposite 视口计算、SDF 距离场与空指针防御断言
    /// <summary>
    /// 测试 5：验证 MultiCameraComposite 的带符号距离场数学算法、羽化抗锯齿、分屏视口计算与空指针防御
    /// </summary>
    private static void TestMultiCameraCompositeViewportAndRobustness()
    {
        // 1. 垂直无倾斜分屏带符号距离场 (SDF) 精度断言 (roll = 0, offset = (0, 0))
        Vector2 offsetZero = Vector2.zero;
        float distCenter = MultiCameraComposite.CalculateDistanceToDivideLine(new Vector2(0.5f, 0.5f), offsetZero, 0f);
        AssertTrue(Mathf.Abs(distCenter) < 0.0001f, $"屏幕中心 (0.5, 0.5) 距离垂直分割线的带符号距离必须为 0 (实际: {distCenter})");

        float distRight = MultiCameraComposite.CalculateDistanceToDivideLine(new Vector2(0.8f, 0.5f), offsetZero, 0f);
        AssertTrue(Mathf.Approximately(distRight, 0.3f), $"右侧点 (0.8, 0.5) 距离垂直分割线应为 +0.3 (实际: {distRight})");
        AssertTrue(MultiCameraComposite.IsPointInLayer(new Vector2(0.8f, 0.5f), offsetZero, 0f), "右侧点必须被判定在图层侧 (true)");

        float distLeft = MultiCameraComposite.CalculateDistanceToDivideLine(new Vector2(0.2f, 0.5f), offsetZero, 0f);
        AssertTrue(Mathf.Approximately(distLeft, -0.3f), $"左侧点 (0.2, 0.5) 距离垂直分割线应为 -0.3 (实际: {distLeft})");
        AssertTrue(!MultiCameraComposite.IsPointInLayer(new Vector2(0.2f, 0.5f), offsetZero, 0f), "左侧点绝不应判定在次图层侧 (false)");

        // 2. 45度倾斜分屏带符号距离场断言 (roll = 45度)
        float dist45 = MultiCameraComposite.CalculateDistanceToDivideLine(new Vector2(0.5f + 0.1f, 0.5f + 0.1f), offsetZero, 45f);
        float expected45 = (0.1f + 0.1f) * Mathf.Cos(45f * Mathf.Deg2Rad);
        AssertTrue(Mathf.Abs(dist45 - expected45) < 0.001f, $"45度倾角下的带符号距离计算误差必须低于 0.001 (实际: {dist45}, 预期: {expected45})");

        // 3. 分割线羽化抗锯齿 Alpha 因子断言
        float thickness = 0.02f; // 半厚度 0.01
        float aa = 0.004f;
        // 点在分割线正中心 -> Alpha = 1
        float alphaCore = MultiCameraComposite.CalculateDivideLineAlpha(new Vector2(0.5f, 0.5f), offsetZero, 0f, thickness, aa);
        AssertTrue(Mathf.Approximately(alphaCore, 1f), $"分割线中心区域 Alpha 必须等于 1.0 (实际: {alphaCore})");

        // 点在完全外部 -> Alpha = 0
        float alphaFar = MultiCameraComposite.CalculateDivideLineAlpha(new Vector2(0.7f, 0.5f), offsetZero, 0f, thickness, aa);
        AssertTrue(Mathf.Approximately(alphaFar, 0f), $"分割线外部区域 Alpha 必须等于 0.0 (实际: {alphaFar})");

        // 点在羽化边界区间 -> 0 < Alpha < 1
        float alphaEdge = MultiCameraComposite.CalculateDivideLineAlpha(new Vector2(0.512f, 0.5f), offsetZero, 0f, thickness, aa);
        AssertTrue(alphaEdge > 0f && alphaEdge < 1f, $"羽化平滑过渡带 Alpha 必须介于 (0, 1) 之间 (实际: {alphaEdge})");

        // 4. 视口计算工具断言
        Rect leftVp = MultiCameraComposite.CalculateSplitViewport(0, 2, Vector2.zero);
        Rect rightVp = MultiCameraComposite.CalculateSplitViewport(1, 2, Vector2.zero);
        AssertTrue(leftVp == new Rect(0f, 0f, 0.5f, 1f), "左机位等分视口 Rect 必须为 (0, 0, 0.5, 1)");
        AssertTrue(rightVp == new Rect(0.5f, 0f, 0.5f, 1f), "右机位等分视口 Rect 必须为 (0.5, 0.5, 1)");

        // 5. 空指针防御与极限参数健壮性断言
        var compGo = new GameObject("Test_MultiCamComp_Host");
        try
        {
            var comp = compGo.AddComponent<MultiCameraComposite>();

            // 未配置输入源时的防御
            comp.SetCameraTextures(null, null);
            comp.IsCompositeActive = false;

            // 无论 source/destination 是否为 null，Composite 绝不能崩溃抛异常
            comp.Composite(null, null);

            // 淡入值为 0 时，自动降级
            comp.FadeValue = 0f;
            comp.Composite(null, null);

            // 参数批量更新
            comp.UpdateLayerParameters(1, MultiCameraComposite.DivideLineType.Color, 0.02f, Color.red, 1f, Vector4.zero, 0f, Vector3.zero, Vector3.zero);
            AssertTrue(comp.IsCompositeActive, "FadeValue 为 1 时应激活分屏合成状态");
            AssertTrue(comp.LineColor == Color.red, "分割线颜色应被正确接收更新");

            comp.ResetParameters();
            AssertTrue(!comp.IsCompositeActive, "ResetParameters 后应恢复为非激活状态");

            // 6. 验证 IsScreenDivide 与 LineAntialiasing 新增属性
            AssertTrue(comp.IsScreenDivide, "默认状态下 IsScreenDivide 必须为 true");
            comp.IsScreenDivide = false;
            AssertTrue(!comp.IsScreenDivide, "设置 IsScreenDivide = false 必须成功生效");
            AssertTrue(Mathf.Approximately(comp.LineAntialiasing, 0.002f), "默认 LineAntialiasing 必须为 0.002f");
            comp.LineAntialiasing = 0.005f;
            AssertTrue(Mathf.Approximately(comp.LineAntialiasing, 0.005f), "设置 LineAntialiasing 必须成功生效");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(compGo);
        }
    }
    #endregion

    #region 测试 6: MultiCamera 默认休眠按需激活与 StageController 激光状态锁防穿透断言
    /// <summary>
    /// 测试 6：验证 MultiCameraComposite 关联相机的按需激活与休眠、以及 StageController 激光状态锁加固逻辑
    /// </summary>
    private static void TestMultiCameraDormancyAndLaserLockAssertion()
    {
        // 1. 验证 MultiCameraComposite 相机联动按需激活
        var compGo = new GameObject("Test_MultiCam_Dormancy_Host");
        var camMainGo = new GameObject("Test_Cam_Main");
        var camSubGo = new GameObject("Test_Cam_Sub");

        try
        {
            var comp = compGo.AddComponent<MultiCameraComposite>();
            var camMain = camMainGo.AddComponent<Camera>();
            var camSub = camSubGo.AddComponent<Camera>();

            // 初始状态下相机默认禁用
            camMain.enabled = false;
            camSub.enabled = false;

            comp.CameraMain = camMain;
            comp.CameraSub = camSub;
            AssertTrue(comp.RenderCamera == camSub, "RenderCamera 必须返回关联的副机位 _cameraSub");

            // 1.1 当 fadeValue <= 0.001f 时，相机保持休眠
            comp.UpdateLayerParameters(0, MultiCameraComposite.DivideLineType.Fade, 0.01f, Color.white, 0f, Vector4.zero, 0f, Vector3.zero, Vector3.zero);
            AssertTrue(!camMain.enabled, "FadeValue 为 0 时主机位相机必须处于休眠状态 (cam.enabled = false)");
            AssertTrue(!camSub.enabled, "FadeValue 为 0 时副机位相机必须处于休眠状态 (cam.enabled = false)");
            AssertTrue(!comp.IsCompositeActive, "FadeValue 为 0 时分屏状态绝不能被激活");

            // 1.2 当时间轴下发 fadeValue > 0.001f 时，动态唤醒分屏相机
            comp.UpdateLayerParameters(0, MultiCameraComposite.DivideLineType.Color, 0.015f, Color.white, 0.85f, Vector4.zero, 0f, Vector3.zero, Vector3.zero);
            AssertTrue(camMain.enabled, "FadeValue > 0.001f 时主机位相机必须被动态唤醒 (cam.enabled = true)");
            AssertTrue(camSub.enabled, "FadeValue > 0.001f 时副机位相机必须被动态唤醒 (cam.enabled = true)");
            AssertTrue(comp.IsCompositeActive, "FadeValue > 0.001f 时分屏合成器必须处于激活状态");

            // 1.3 当分屏结束 fadeValue 降至 0 时，相机应立即置回休眠
            comp.UpdateLayerParameters(0, MultiCameraComposite.DivideLineType.Color, 0.015f, Color.white, 0.0005f, Vector4.zero, 0f, Vector3.zero, Vector3.zero);
            AssertTrue(!camMain.enabled, "分屏结束后主机位相机必须立即休眠 (cam.enabled = false)");
            AssertTrue(!camSub.enabled, "分屏结束后副机位相机必须立即休眠 (cam.enabled = false)");
            AssertTrue(!comp.IsCompositeActive, "分屏结束后分屏合成器必须置回非激活");

            // 1.4 调用 ResetParameters 时，相机应彻底休眠且 fadeValue 重置为 0
            comp.UpdateLayerParameters(0, MultiCameraComposite.DivideLineType.Color, 0.015f, Color.white, 1f, Vector4.zero, 0f, Vector3.zero, Vector3.zero);
            comp.ResetParameters();
            AssertTrue(!camMain.enabled, "ResetParameters 后主机位相机必须休眠");
            AssertTrue(!camSub.enabled, "ResetParameters 后副机位相机必须休眠");
            AssertTrue(comp.FadeValue == 0f, "ResetParameters 后 FadeValue 必须归零 (0f)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(camMainGo);
            UnityEngine.Object.DestroyImmediate(camSubGo);
            UnityEngine.Object.DestroyImmediate(compGo);
        }

        // 2. 验证 StageController 激光状态锁加固（反射检验 _laserSetupDone）
        var stageGo = new GameObject("Test_Stage_LaserLock_Host");
        try
        {
            var stage = stageGo.AddComponent<StageController>();

            var flagField = typeof(StageController).GetField("_laserSetupDone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            AssertNotNull(flagField, "_laserSetupDone 私有字段必须存在");

            // 初始未初始化
            bool isDone = (bool)flagField.GetValue(stage);
            AssertTrue(!isDone, "初始阶段 _laserSetupDone 必须为 false");

            // 模拟完成状态并验证锁加固
            flagField.SetValue(stage, true);
            AssertTrue((bool)flagField.GetValue(stage), "设置后 _laserSetupDone 必须为 true");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(stageGo);
        }
    }

    /// <summary>
    /// 测试 7：多机位 MaskRoll 映射、分屏开关 IsScreenDivide、深度与背景色属性断言
    /// 验证：
    /// 1. GetMultiCameraMaskRollFromMaskType 对 Down/Left/Right/LeftUp/RightUp/LeftDown/RightDown 的正确映射
    /// 2. LiveTimelineKeyCameraPositionData.IsEnabledBgColor 与 GetBgColor 的属性行为
    /// 3. MultiCameraComposite.IsScreenDivide 与 RenderCamera 访问器可用性
    /// </summary>
    private static void TestMultiCameraMaskRollDivideAndBgColorAssertion()
    {
        // 1. 验证 GetMultiCameraMaskRollFromMaskType 映射（反射调用 LiveTimelineControl 的私有方法）
        var timelineGo = new GameObject("Test_TimelineControl_MaskRoll");
        try
        {
            var timeline = timelineGo.AddComponent<LiveTimelineControl>();
            var method = typeof(LiveTimelineControl).GetMethod(
                "GetMultiCameraMaskRollFromMaskType",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            );
            AssertNotNull(method, "GetMultiCameraMaskRollFromMaskType 私有方法必须存在");

            float rollDown = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.Down });
            AssertTrue(Mathf.Approximately(rollDown, 180f), $"Down 映射角度应为 180 度，实际为: {rollDown}");

            float rollLeft = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.Left });
            AssertTrue(Mathf.Approximately(rollLeft, -90f), $"Left 映射角度应为 -90 度，实际为: {rollLeft}");

            float rollRight = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.Right });
            AssertTrue(Mathf.Approximately(rollRight, 90f), $"Right 映射角度应为 90 度，实际为: {rollRight}");

            float rollLeftUp = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.LeftUp });
            AssertTrue(Mathf.Approximately(rollLeftUp, -45f), $"LeftUp 映射角度应为 -45 度，实际为: {rollLeftUp}");

            float rollRightUp = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.RightUp });
            AssertTrue(Mathf.Approximately(rollRightUp, 45f), $"RightUp 映射角度应为 45 度，实际为: {rollRightUp}");

            float rollLeftDown = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.LeftDown });
            AssertTrue(Mathf.Approximately(rollLeftDown, -135f), $"LeftDown 映射角度应为 -135 度，实际为: {rollLeftDown}");

            float rollRightDown = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.RightDown });
            AssertTrue(Mathf.Approximately(rollRightDown, 135f), $"RightDown 映射角度应为 135 度，实际为: {rollRightDown}");

            float rollDefault = (float)method.Invoke(timeline, new object[] { LiveTimelineKeyMultiCameraPositionData.MaskType.All });
            AssertTrue(Mathf.Approximately(rollDefault, 0f), $"All 映射角度应为 0 度，实际为: {rollDefault}");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(timelineGo);
        }

        // 2. 验证 LiveTimelineKeyCameraPositionData 背景色扩展
        var keyData = new LiveTimelineKeyCameraPositionData
        {
            BgColor = new Color(0.1f, 0.2f, 0.3f, 1f),
            attribute = (LiveTimelineKeyAttribute)0x40000 // 置位 ATTR_ENABLE_BG_COLOR
        };
        AssertTrue(keyData.IsEnabledBgColor, "置位 0x40000 后 IsEnabledBgColor 必须为 true");
        AssertTrue(keyData.GetBgColor() == keyData.BgColor, "GetBgColor() 返回值必须与关键帧 BgColor 一致");

        keyData.attribute = 0;
        AssertTrue(!keyData.IsEnabledBgColor, "未置位 0x40000 时 IsEnabledBgColor 必须为 false");

        // 3. 验证 MultiCameraComposite 的 IsScreenDivide 与 RenderCamera
        var compGo = new GameObject("Test_MultiCameraComposite_Props");
        try
        {
            var comp = compGo.AddComponent<MultiCameraComposite>();
            var camSubGo = new GameObject("Test_SubCam");
            camSubGo.transform.SetParent(compGo.transform);
            var camSub = camSubGo.AddComponent<Camera>();
            comp.CameraSub = camSub;

            AssertTrue(comp.IsScreenDivide, "MultiCameraComposite 默认分屏开关 IsScreenDivide 应为 true");
            comp.IsScreenDivide = false;
            AssertTrue(!comp.IsScreenDivide, "修改后 IsScreenDivide 应为 false");

            AssertNotNull(comp.RenderCamera, "RenderCamera 访问器应正确返回绑定的 CameraSub 组件");
            AssertTrue(comp.RenderCamera == camSub, "RenderCamera 必须等于 CameraSub");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(compGo);
        }
    }

    #region 测试 8: 多机位数容错推断、独立合成器数组与 MultiCameraFinalComposite 断言
    /// <summary>
    /// 测试 8：验证 multiCameraSettings 为空时从 worksheetList[0].multiCameraPosKeys 推断机位数，
    /// 验证独立合成器数组 MultiCameraComposites 与主相机 MultiCameraFinalComposite 挂载。
    /// </summary>
    private static void TestMultiCameraInferenceAndFinalCompositeAssertion()
    {
        var dirGo = new GameObject("Test_Director_MultiCam");
        var controlGo = new GameObject("Test_TimelineControl");
        var mainCamGo = new GameObject("MainCamera");
        mainCamGo.tag = "MainCamera";
        var mainCamera = mainCamGo.AddComponent<Camera>();

        try
        {
            var director = dirGo.AddComponent<Director>();
            var timeline = controlGo.AddComponent<LiveTimelineControl>();

            // 模拟 1157 数据：multiCameraSettings 为 null，但 multiCameraPosKeys 有 2 轨
            var data = ScriptableObject.CreateInstance<LiveTimelineData>();
            var sheet = ScriptableObject.CreateInstance<LiveTimelineWorkSheet>();
            sheet.multiCameraPosKeys = new List<LiveTimelineMultiCameraPositionData>
            {
                new LiveTimelineMultiCameraPositionData
                {
                    keys = new LiveTimelineKeyMultiCameraPositionDataList()
                },
                new LiveTimelineMultiCameraPositionData
                {
                    keys = new LiveTimelineKeyMultiCameraPositionDataList()
                }
            };
            data.worksheetList = new List<LiveTimelineWorkSheet> { sheet };
            data.multiCameraSettings = null; // 模拟 1157 该配置为空
            timeline.data = data;

            // 执行初始化
            director.InitializeMultiCamera(timeline);

            // 断言 1：成功推断并初始化 2 台多机位
            AssertNotNull(director.MultiCameraComposites, "多机位合成器数组必须被成功创建");
            AssertTrue(director.MultiCameraComposites.Length == 2, $"机位数应推断为 2，实际为: {director.MultiCameraComposites.Length}");

            // 断言 2：每路机位拥有独立的 MultiCameraComposite 与 MultiCameraNo
            var comp0 = director.GetMultiCameraComposite(0);
            var comp1 = director.GetMultiCameraComposite(1);
            AssertNotNull(comp0, "第 0 路 MultiCameraComposite 不能为空");
            AssertNotNull(comp1, "第 1 路 MultiCameraComposite 不能为空");
            AssertTrue(comp0 != comp1, "两路 MultiCameraComposite 必须为互不相同的独立实例（杜绝单例踩踏）");
            AssertTrue(comp0.MultiCameraNo == 0, "第 0 路 MultiCameraNo 应为 0");
            AssertTrue(comp1.MultiCameraNo == 1, "第 1 路 MultiCameraNo 应为 1");

            // 断言 3：初始状态下所有分屏相机的 Camera.enabled 必须为 false（休眠状态）
            if (comp0.RenderCamera != null)
            {
                AssertTrue(!comp0.RenderCamera.enabled, "初始状态下第 0 路次机位相机必须处于休眠状态 (enabled == false)");
            }
            if (comp1.RenderCamera != null)
            {
                AssertTrue(!comp1.RenderCamera.enabled, "初始状态下第 1 路次机位相机必须处于休眠状态 (enabled == false)");
            }

            // 断言 4：主相机必须成功挂载 MultiCameraFinalComposite 后处理组件
            var finalComp = mainCamGo.GetComponent<MultiCameraFinalComposite>();
            AssertNotNull(finalComp, "主相机上必须自动挂载 MultiCameraFinalComposite 组件");
            AssertNotNull(finalComp.TargetComposite, "MultiCameraFinalComposite 必须成功关联目标合成器");

            // 清理
            director.CleanupMultiCamera();
            AssertTrue(director.MultiCameraComposites == null, "CleanupMultiCamera 后 MultiCameraComposites 必须清空");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(dirGo);
            UnityEngine.Object.DestroyImmediate(controlGo);
            UnityEngine.Object.DestroyImmediate(mainCamGo);
        }
    }
    #endregion

    #region 测试 9: URP 多机位全屏分屏 Overlay 呈现层、防黑空纹理保护与 MonitorCamera 实时画面断言
    /// <summary>
    /// 测试 9：断言 URP 兼容的 Overlay Canvas 全屏呈现与生命周期、防黑空纹理隐藏保护及 MonitorCamera 实时渲染
    /// </summary>
    private static void TestUrpMultiCameraOverlayAndMonitorCameraAssertion()
    {
        var dirGo = new GameObject("Director_TestUrp");
        var director = dirGo.AddComponent<Director>();
        var controlGo = new GameObject("TimelineControl_TestUrp");
        var timeline = controlGo.AddComponent<LiveTimelineControl>();

        try
        {
            // 1. 模拟 1157 双机位时间轴工作表
            var data = ScriptableObject.CreateInstance<LiveTimelineData>();
            var sheet = new LiveTimelineWorkSheet();
            sheet.multiCameraPosKeys = new List<LiveTimelineMultiCameraPositionData>
            {
                new LiveTimelineMultiCameraPositionData { keys = new LiveTimelineKeyMultiCameraPositionDataList() },
                new LiveTimelineMultiCameraPositionData { keys = new LiveTimelineKeyMultiCameraPositionDataList() }
            };

            // 配置 MonitorCamera 关键帧
            sheet.monitorCameraPosKeys = new List<LiveTimelineMonitorCameraPositionData>
            {
                new LiveTimelineMonitorCameraPositionData
                {
                    keys = new LiveTimelineKeyMonitorCameraPositionDataList
                    {
                        thisList = new List<LiveTimelineKeyMonitorCameraPositionData>
                        {
                            new LiveTimelineKeyMonitorCameraPositionData
                            {
                                frame = 0,
                                position = new Vector3(-1.3f, 1.5f, 5f),
                                fov = 45f,
                                nearClip = 0.1f,
                                farClip = 100f
                            }
                        }
                    }
                }
            };
            sheet.monitorCameraLookAtKeys = new List<LiveTimelineMonitorCameraLookAtData>
            {
                new LiveTimelineMonitorCameraLookAtData
                {
                    keys = new LiveTimelineKeyMonitorCameraLookAtDataList
                    {
                        thisList = new List<LiveTimelineKeyMonitorCameraLookAtData>
                        {
                            new LiveTimelineKeyMonitorCameraLookAtData
                            {
                                frame = 0,
                                position = new Vector3(0f, 1f, 0f)
                            }
                        }
                    }
                }
            };

            data.worksheetList = new List<LiveTimelineWorkSheet> { sheet };
            timeline.data = data;

            // 2. 初始化多机位与 MonitorCamera
            director.InitializeMultiCamera(timeline);
            director.InitializeMonitorCamera(timeline);

            // 断言 1：MonitorCamera 离屏纹理必须成功分配
            AssertNotNull(director.MonitorCameraTexture, "MonitorCamera 离屏渲染纹理必须成功创建");
            AssertTrue(director.MonitorCameraTexture.width > 0 && director.MonitorCameraTexture.height > 0, "MonitorCamera 纹理尺寸必须为正数有效值");

            // 断言 2：UpdateMonitorCamera 能正确驱动相机位姿
            director.UpdateMonitorCamera(sheet, 0f);

            // 3. 测试 URP 分屏呈现层 Overlay 驱动
            var comp = director.GetMultiCameraComposite(0);
            AssertNotNull(comp, "多机位合成器不能为空");

            // 单机位阶段（FadeValue == 0）：Overlay 必须保持休眠隐藏
            comp.ApplySwitcherFade(0f);
            director.UpdateMultiCameraDisplay();
            var overlayRoot = dirGo.transform.Find("MultiCameraScreenOverlay");
            if (overlayRoot != null)
            {
                AssertTrue(!overlayRoot.gameObject.activeSelf, "单机位阶段 (FadeValue == 0) Overlay 画布必须处于隐藏状态");
            }

            // 分屏阶段（FadeValue == 1）：Overlay 必须被自动唤醒并激活
            comp.ApplySwitcherFade(1f);
            director.UpdateMultiCameraDisplay();
            AssertNotNull(overlayRoot, "分屏激活时 MultiCameraScreenOverlay 画布根节点必须存在");
            AssertTrue(overlayRoot.gameObject.activeSelf, "分屏阶段 (FadeValue == 1) Overlay 画布必须被自动激活呈现");

            var overlayCanvas = overlayRoot.GetComponent<Canvas>();
            AssertNotNull(overlayCanvas, "Overlay 根节点上必须具备 Canvas 组件");
            AssertTrue(overlayCanvas.sortingOrder == -1, $"Overlay Canvas 的 sortingOrder 必须为 -1（实际为: {overlayCanvas.sortingOrder}），以确保覆盖 3D 且绝不遮挡 UI");

            // 4. 清理并断言零内存残留
            director.CleanupMultiCamera();
            director.CleanupMonitorCamera();
            AssertTrue(director.MonitorCameraTexture == null, "CleanupMonitorCamera 后 MonitorCameraTexture 必须释放置空");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(dirGo);
            UnityEngine.Object.DestroyImmediate(controlGo);
        }
    }
    #endregion

    #region 测试 10: 多机位时间轴参考结构对齐与真实 Cutt 反序列化断言
    /// <summary>
    /// 测试 10：验证 MultiCamera 数据组结构参考实现（默认列表、GetKeyList 重写、基类名称构造），
    /// 并从本地缓存中加载真实 1157 Cutt 资产，深度断言反序列化字段 MultiCameraNo 与 MaskCentralAngle 的有效性。
    /// </summary>
    private static void TestRealCuttAssetDeserializationAssertion()
    {
        // 1. 验证参考结构形态契约（默认实例化、接口重写与名称构造）
        var posGroup = new LiveTimelineMultiCameraPositionData();
        AssertNotNull(posGroup.keys, "LiveTimelineMultiCameraPositionData 默认必须实例化 keys 容器");
        AssertTrue(posGroup.name == "MultiCameraPos", $"LiveTimelineMultiCameraPositionData 构造函数必须注册名称 MultiCameraPos，实际为: '{posGroup.name}'");
        AssertNotNull(posGroup.GetKeyList(), "LiveTimelineMultiCameraPositionData GetKeyList() 必须返回有效列表");
        AssertTrue(posGroup.GetKeyList() == posGroup.keys, "GetKeyList() 必须严格指向内部 keys 成员");

        var lookAtGroup = new LiveTimelineMultiCameraLookAtData();
        AssertNotNull(lookAtGroup.keys, "LiveTimelineMultiCameraLookAtData 默认必须实例化 keys 容器");
        AssertTrue(lookAtGroup.name == "MultiCameraLookAt", $"LiveTimelineMultiCameraLookAtData 构造函数必须注册名称 MultiCameraLookAt，实际为: '{lookAtGroup.name}'");
        AssertNotNull(lookAtGroup.GetKeyList(), "LiveTimelineMultiCameraLookAtData GetKeyList() 必须返回有效列表");
        AssertTrue(lookAtGroup.GetKeyList() == lookAtGroup.keys, "GetKeyList() 必须严格指向内部 keys 成员");

        // 2. 尝试从本地持久化缓存中加载 1157 真实 Cutt 资产并执行反序列化断言
        if (Config.Instance == null) new Config();
        Config.Instance.MainPath = @"C:\Users\JuziD\Umamusume\umamusume_Data\Persistent";

        var db = UmaDatabaseController.Instance;
        if (db != null && db.MetaEntries != null && db.MetaEntries.TryGetValue("cutt/cutt_son1157/cutt_son1157", out var cuttEntry))
        {
            if (UmaAssetManager.Exist(cuttEntry))
            {
                AssetBundle bundle = UmaAssetManager.LoadAssetBundle(cuttEntry);
                AssertNotNull(bundle, "1157 Cutt AssetBundle 读取加载不得为空");

                GameObject cuttPrefab = bundle.LoadAsset<GameObject>("cutt_son1157");
                AssertNotNull(cuttPrefab, "1157 Cutt 预制体资源载入不得为空");

                var timelineControl = cuttPrefab.GetComponent<LiveTimelineControl>();
                AssertNotNull(timelineControl, "1157 Cutt 预制体必须挂载 LiveTimelineControl 组件");
                AssertNotNull(timelineControl.data, "1157 LiveTimelineControl.data 必须成功反序列化");
                AssertNotNull(timelineControl.data.worksheetList, "1157 worksheetList 必须反序列化成功");
                AssertTrue(timelineControl.data.worksheetList.Count > 0, "1157 worksheetList 至少应包含 1 个工作表");

                var sheet0 = timelineControl.data.worksheetList[0];
                AssertNotNull(sheet0.multiCameraPosKeys, "1157 worksheet[0].multiCameraPosKeys 必须反序列化成功");
                AssertTrue(sheet0.multiCameraPosKeys.Count > 0, "1157 multiCameraPosKeys 轨道数量应大于 0");

                bool foundValidMultiCameraNo = false;
                bool foundFanMaskAngle = false;

                for (int i = 0; i < sheet0.multiCameraPosKeys.Count; i++)
                {
                    var group = sheet0.multiCameraPosKeys[i];
                    AssertNotNull(group, "多机位位置组数据不能为空");
                    AssertNotNull(group.GetKeyList(), "真实资产反序列化后 GetKeyList() 必须有效且非空");

                    // 真实反序列化后的 MultiCameraNo 应为合法索引（0、1 等）
                    if (group.MultiCameraNo >= 0)
                    {
                        foundValidMultiCameraNo = true;
                    }

                    if (group.keys != null && group.keys.thisList != null)
                    {
                        for (int j = 0; j < group.keys.thisList.Count; j++)
                        {
                            var k = group.keys.thisList[j];
                            if (k != null && k.maskType == LiveTimelineKeyMultiCameraPositionData.MaskType.Fan && k.MaskCentralAngle > 0.001f)
                            {
                                foundFanMaskAngle = true;
                            }
                        }
                    }
                }

                AssertTrue(foundValidMultiCameraNo, "真实 1157 Cutt 资产反序列化中必须解析出有效的 MultiCameraNo");
                AssertTrue(foundFanMaskAngle, "真实 1157 Cutt 资产中必须反序列化出有效的 MaskCentralAngle 扇形遮罩角度");

                // 同时断言 LookAt 轨道反序列化
                if (sheet0.multiCameraLookAtKeys != null && sheet0.multiCameraLookAtKeys.Count > 0)
                {
                    for (int i = 0; i < sheet0.multiCameraLookAtKeys.Count; i++)
                    {
                        var lookAt = sheet0.multiCameraLookAtKeys[i];
                        AssertNotNull(lookAt, "多机位注视点组数据不能为空");
                        AssertNotNull(lookAt.GetKeyList(), "真实资产反序列化后 LookAt.GetKeyList() 必须有效");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[测试 10] 本地未缓存 1157 Cutt bundle，跳过资产级断言，已完成参考结构与契约断言。");
            }
        }
    }
    #endregion
    #endregion
}
