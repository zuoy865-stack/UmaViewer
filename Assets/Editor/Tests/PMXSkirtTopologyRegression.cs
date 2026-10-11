using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>独立验证裙面拓扑分析，不进入 PMX 骨骼或物理导出。</summary>
public static class PMXSkirtTopologyRegression
{
    private sealed class InputSnapshot
    {
        public Vector3[] Vertices;
        public int[] Triangles;
        public Vector3[][] Polylines;
        public string[][] BonePaths;
        public string[][] WeightIds;
        public float[][] WeightValues;
        public float[] NonSkirtWeights;
        public float TargetSegmentLength, LengthTolerance, MaximumVirtualLengthRatio;
        public int MaximumSegments;
        public Vector3[] Normals;
        public int[] Groups;
        public int[][] Seams;
        public string[] SkinSignatures;
    }

    public static void RunFixtures()
    {
        JArray cases = RunCases();
        SaveReport(cases);
        List<string> failures = cases.OfType<JObject>()
            .Where(item => (string)item["status"] != "passed")
            .Select(item => (string)item["name"] + ": " + (string)item["details"]).ToList();
        if (failures.Count > 0)
            throw new InvalidOperationException("裙面拓扑夹具失败：\n" + string.Join("\n", failures));
    }

    public static void RunBatch()
    {
        JArray cases = RunCases();
        bool failed = cases.OfType<JObject>().Any(item => (string)item["status"] != "passed");
        JObject report = SaveReport(cases);
        Debug.Log("PMX 裙面拓扑夹具：" + report);
        EditorApplication.Exit(failed ? 1 : 0);
    }

    private static JArray RunCases()
    {
        var results = new JArray();
        RunCase(results, "三角边邻接与开口裙面不闭环", TestTriangleAdjacencyAndOpenFork);
        RunCase(results, "重叠坐标的独立裙层不串接", TestIndependentLayers);
        RunCase(results, "不同裙长自适应分段与段数预算", TestAdaptiveSegmentsAndBudget);
        RunCase(results, "最长侧虚拟延长沿弯曲末端且输入不变", TestCurvedLongestSideExtension);
        RunCase(results, "曲率分段适配与弦误差", TestCurvedSegmentFit);
        RunCase(results, "重复面、孤立权重点与固定/混合权重审计", TestDuplicateAndWeightPreservation);
        RunCase(results, "无裙权重和无效索引明确 unsupported", TestUnsupportedInputs);
        RunCase(results, "拆点接缝按边证据连通且输入保持", TestProvenSeams);
        return results;
    }

    private static void TestProvenSeams()
    {
        PMXSkirtTopologyInput grid = MakeGrid("left", "middle", "right",
            Column(0, 0, 0, -0.15f, -0.3f), Column(0.1f, 0, 0, -0.15f, -0.3f), Column(0.2f, 0, 0, -0.15f, -0.3f));
        var split = new PMXSkirtTopologyInput { Columns = grid.Columns };
        foreach (int index in grid.TriangleIndices)
        {
            split.TriangleIndices.Add(split.Vertices.Count);
            split.Vertices.Add(grid.Vertices[index]);
            split.VertexColumnWeights.Add(grid.VertexColumnWeights[index]);
            split.VertexNormals.Add(Vector3.forward); split.VertexGroups.Add(0);
            split.VertexSkinSignatures.Add("bone:" + grid.VertexColumnWeights[index].ColumnWeights[0].ColumnId + ":1");
        }
        InputSnapshot before = Snapshot(split);
        var result = Analyze(split);
        Assert(result.Supported && !result.IsIncomplete && result.Regions.Count == 1,
            "同来源、同权重和法线的反向接缝边应建立分析连通，不能让每个三角成为独立裙层。");
        AssertUnchanged(split, before);
        // 法线硬边/双面片保持可见拆点，同源完整蒙皮允许物理分析连通。
        for (int i = 0; i < split.VertexNormals.Count; i++) split.VertexNormals[i] = i / 3 % 2 == 0 ? Vector3.forward : Vector3.back;
        before = Snapshot(split);
        var hardNormals = Analyze(split);
        Assert(hardNormals.Supported && !hardNormals.IsIncomplete && hardNormals.Regions.Count == 1,
            "同源完整蒙皮的法线拆点不应让四节物理回退。");
        AssertUnchanged(split, before);
        var skinBefore = split.VertexSkinSignatures.ToArray();
        for (int i = 0; i < split.VertexSkinSignatures.Count; i++) split.VertexSkinSignatures[i] = "different-body-bone:" + i;
        Assert(Analyze(split).Regions.Count > 1, "完整身体骨份额不同时不能凭合计裙权重相同接缝。");
        split.VertexSkinSignatures = skinBefore.ToList();
        // 源 Renderer 不同，即便坐标相同也不允许接边。
        for (int i = 0; i < split.VertexGroups.Count; i++) split.VertexGroups[i] = i / 3;
        var separated = Analyze(split);
        Assert(separated.Regions.Count > 1 && separated.IsIncomplete, "来源不同时不能按位置强行接缝。");
    }

    private static void TestTriangleAdjacencyAndOpenFork()
    {
        PMXSkirtTopologyInput input = MakeGrid("front", "middle", "back",
            Column(0, 0, 0, 0.2f, 0.4f),
            Column(0.5f, 0, 0, 0.2f, 0.4f),
            Column(1, 0, 0, 0.2f, 0.4f));
        PMXSkirtTopologyResult result = Analyze(input);
        Assert(result.Supported, "有效开口裙面应受支持：" + result.Reason);
        Assert(result.Regions.Count == 1, "共享真实三角边的裙片必须属于同一连通区域。");
        PMXSkirtRegionResult region = result.Regions[0];
        Assert(!region.IsClosed && region.BoundaryEdges.Count > 0,
            "实际开口裙面必须保留边界，不能凭列角度补成闭环。");
        foreach (PMXSkirtBoundaryEdge edge in region.BoundaryEdges)
            Assert(!IsPair(edge.ColumnPairA, edge.ColumnPairB, "front", "back"),
                "开叉两侧不得生成虚构的首尾跨列连接。");
        AssertFinite(result);
    }

    private static void TestIndependentLayers()
    {
        Vector3[][] paths =
        {
            Column(0, 0, 0, 0.15f, 0.3f), Column(0.5f, 0, 0, 0.15f, 0.3f), Column(1, 0, 0, 0.15f, 0.3f)
        };
        PMXSkirtTopologyInput first = MakeGrid("outerA", "outerB", "outerC", paths);
        PMXSkirtTopologyInput second = MakeGrid("innerA", "innerB", "innerC", paths);
        PMXSkirtTopologyResult result = Analyze(Join(first, second));
        Assert(result.Supported, "两个有面且有权重的裙层应受支持：" + result.Reason);
        Assert(result.Regions.Count == 2, "空间重叠但没有共用网格边的两层裙面必须保持两个区域。");
        Assert(result.Regions[0].VertexIndices.Intersect(result.Regions[1].VertexIndices).Any() == false,
            "独立裙层不得共享输出顶点归属。");
        Assert(result.Columns.Select(column => column.ComponentId).Distinct().Count() == 2,
            "独立裙层的列必须各自归属于其真实网格连通分量。");
        AssertFinite(result);
    }

    private static void TestAdaptiveSegmentsAndBudget()
    {
        PMXSkirtTopologyInput input = MakeGrid("short", "long",
            Column(0, 0, 0, 0.2f, 0.4f),
            Column(1, 0, 0, 0.4f, 0.8f));
        input.TargetSegmentLength = 0.1f;
        input.LengthTolerance = 0.005f;
        input.MaximumSegments = 16;
        PMXSkirtTopologyResult result = Analyze(input);
        Assert(result.Supported, "长短裙列均应可拟合：" + result.Reason);
        PMXSkirtColumnResult shortColumn = FindColumn(result, "short");
        PMXSkirtColumnResult longColumn = FindColumn(result, "long");
        Assert(longColumn.Segments.Count == shortColumn.Segments.Count &&
            shortColumn.VirtualCoverageLength > input.LengthTolerance &&
            longColumn.VirtualCoverageLength <= input.LengthTolerance,
            "短裙列应以最长列为基准补虚拟段，不能只让长列增加段数。");
        Assert(longColumn.Segments.Count <= input.MaximumSegments && !longColumn.IsIncomplete,
            "未触及预算的长裙列应达到目标且不得超预算。");
        AssertNear(shortColumn.MeshGeodesicLength, 0.4f, 0.015f, "短列三角边测地长度");
        AssertNear(longColumn.MeshGeodesicLength, 0.8f, 0.015f, "长列三角边测地长度");

        input.MaximumSegments = 2;
        PMXSkirtTopologyResult limited = Analyze(input);
        PMXSkirtColumnResult limitedLong = FindColumn(limited, "long");
        Assert(limitedLong.IsIncomplete && !string.IsNullOrEmpty(limitedLong.BudgetReason),
            "段数预算不足时必须明确标记 incomplete 和原因。");
        Assert(limitedLong.Segments.Count <= input.MaximumSegments,
            "预算不足时不得超过段数上限。");
        AssertFinite(result);
        AssertFinite(limited);
    }

    private static void TestCurvedLongestSideExtension()
    {
        Vector3[][] paths =
        {
            new[]
            {
                new Vector3(0, 0, 0), new Vector3(0.05f, -0.05f, 0),
                new Vector3(0.15f, -0.12f, 0.02f), new Vector3(0.30f, -0.16f, 0.08f)
            },
            new[]
            {
                new Vector3(1, 0, 0), new Vector3(1, -0.2f, 0),
                new Vector3(1, -0.4f, 0), new Vector3(1, -0.6f, 0)
            }
        };
        PMXSkirtTopologyInput input = MakeGrid("short", "long", paths);
        input.TargetSegmentLength = 0.08f;
        input.LengthTolerance = 0.005f;
        input.MaximumSegments = 16;
        input.MaximumVirtualLengthRatio = 3f;
        InputSnapshot before = Snapshot(input);

        PMXSkirtTopologyResult result = Analyze(input);
        Assert(result.Supported, "弯曲不对称裙列应受支持：" + result.Reason);
        PMXSkirtColumnResult shortColumn = FindColumn(result, "short");
        PMXSkirtColumnResult longColumn = FindColumn(result, "long");
        Assert(shortColumn.VirtualCoverageLength > 0.05f && shortColumn.RealCoverageLength < shortColumn.TargetLength,
            "较短可见裙面应保留真实覆盖长度，并单独补出最长侧虚拟段。");
        AssertNear(shortColumn.AchievedLength, shortColumn.TargetLength, 0.02f,
            "短侧虚拟布局应接续到同层最长侧长度");
        Assert(longColumn.VirtualCoverageLength <= input.LengthTolerance,
            "最长侧本身不应被误记为虚拟延长。");
        PMXSkirtSegmentLayout virtualSegment = shortColumn.Segments.Last(segment => segment.IsVirtual);
        Vector3 pathEnd = shortColumn.MeshPathPoints.Last();
        Vector3 tangent = (pathEnd - shortColumn.MeshPathPoints[shortColumn.MeshPathPoints.Count - 2]).normalized;
        Vector3 virtualDelta = virtualSegment.End - pathEnd;
        Assert(Vector3.Dot(virtualDelta.normalized, tangent) > 0.9f,
            "虚拟段须从真实测地终点沿末端切线延长。");
        AssertNear(virtualDelta.magnitude, shortColumn.RequestedVirtualLength, 0.02f, "请求虚拟延长距离");
        AssertNear(shortColumn.VirtualCoverageLength, virtualDelta.magnitude, 0.02f, "实际虚拟覆盖长度");
        Assert(virtualDelta.magnitude > 0.05f &&
            new Vector2(virtualDelta.x, virtualDelta.z).magnitude / virtualDelta.magnitude > 0.5f,
            "虚拟段应沿倾斜/弯曲的裙列末端延续，不能固定沿世界 Y 拉长。");
        AssertUnchanged(input, before);
        AssertFinite(result);
    }

    private static void TestCurvedSegmentFit()
    {
        Vector3[][] paths =
        {
            new[] { new Vector3(0, 0, 0), new Vector3(0, -0.1f, 0), new Vector3(0.1f, -0.1f, 0), new Vector3(0.1f, -0.2f, 0) },
            new[] { new Vector3(1, 0, 0), new Vector3(1, -0.1f, 0), new Vector3(1, -0.2f, 0), new Vector3(1, -0.3f, 0) }
        };
        PMXSkirtTopologyInput input = MakeGrid("curved", "straight", paths);
        input.TargetSegmentLength = 0.15f;
        input.LengthTolerance = 0.02f;
        input.MaximumSegments = 32;
        PMXSkirtTopologyResult result = Analyze(input);
        Assert(result.Supported, "曲率夹具应受支持：" + result.Reason);
        PMXSkirtColumnResult curved = FindColumn(result, "curved");
        PMXSkirtColumnResult straight = FindColumn(result, "straight");
        Assert(curved.Segments.Count == straight.Segments.Count &&
            curved.Segments.Count > Mathf.CeilToInt(curved.TargetLength / input.TargetSegmentLength),
            "曲线应增加整层公共分段数，直列同步加密以保持横向对应。");
        Assert(curved.MaximumChordError <= input.LengthTolerance && straight.MaximumChordError <= input.LengthTolerance,
            "拟合完成后最大弦误差应在容差内。");
        AssertFinite(result);
    }

    private static void TestDuplicateAndWeightPreservation()
    {
        PMXSkirtTopologyInput input = MakeGrid("left", "right",
            Column(0, 0, 0, 0.2f, 0.4f), Column(1, 0, 0, 0.2f, 0.4f));
        int[] duplicate = input.TriangleIndices.Take(3).ToArray();
        input.TriangleIndices.Add(duplicate[1]);
        input.TriangleIndices.Add(duplicate[2]);
        input.TriangleIndices.Add(duplicate[0]);

        input.VertexColumnWeights[0] = new PMXSkirtVertexWeight
        { NonSkirtWeight = 1f, ColumnWeights = new List<PMXSkirtColumnWeight>() };
        input.VertexColumnWeights[1] = new PMXSkirtVertexWeight
        {
            NonSkirtWeight = 0.35f,
            ColumnWeights = new List<PMXSkirtColumnWeight> { new PMXSkirtColumnWeight { ColumnId = "left", Weight = 0.65f } }
        };
        input.Vertices.Add(new Vector3(4, 4, 4));
        input.VertexColumnWeights.Add(new PMXSkirtVertexWeight
        {
            NonSkirtWeight = 0f,
            ColumnWeights = new List<PMXSkirtColumnWeight> { new PMXSkirtColumnWeight { ColumnId = "left", Weight = 1f } }
        });
        int bodyTriangle = input.TriangleIndices.Count / 3;
        input.Vertices.AddRange(new[] { new Vector3(3, 0, 0), new Vector3(3, -0.2f, 0), new Vector3(3.3f, 0, 0) });
        input.VertexColumnWeights.Add(new PMXSkirtVertexWeight { NonSkirtWeight = 0.4f,
            ColumnWeights = new List<PMXSkirtColumnWeight> { new PMXSkirtColumnWeight { ColumnId = "left", Weight = 0.6f } } });
        input.VertexColumnWeights.AddRange(new[] { 0, 1 }.Select(_ => new PMXSkirtVertexWeight
            { NonSkirtWeight = 1f, ColumnWeights = new List<PMXSkirtColumnWeight>() }));
        input.TriangleIndices.AddRange(new[] { input.Vertices.Count - 3, input.Vertices.Count - 2, input.Vertices.Count - 1 });
        InputSnapshot before = Snapshot(input);

        PMXSkirtTopologyResult result = Analyze(input);
        Assert(result.Supported, "含重复面和独立权重审计的有效裙面应受支持：" + result.Reason);
        Assert(result.Regions.Sum(region => region.DuplicateTriangleCount) == 1,
            "重复三角形应计数并只按实际唯一拓扑参与拟合。");
        Assert(result.IsolatedSkirtVertexIndices.Count == 1 && result.IsolatedSkirtVertexIndices[0] == input.Vertices.Count - 4,
            "没有任何三角邻接的正裙权重顶点应单独记录为孤立点。");
        Assert(result.Regions.All(region => !region.TriangleIndices.Contains(bodyTriangle)),
            "仅有一个裙列正权重点的身体三角不得混入裙拓扑。");
        PMXSkirtVertexWeightAudit fixedWaist = result.WeightAudit.Single(item => item.VertexIndex == 0);
        AssertNear(fixedWaist.NonSkirtWeight, 1f, 0.0001f, "固定腰口非裙权重");
        Assert(fixedWaist.ColumnWeights.Count == 0, "固定腰口不得凭拓扑推导裙列权重。");
        PMXSkirtVertexWeightAudit mixed = result.WeightAudit.Single(item => item.VertexIndex == 1);
        AssertNear(mixed.NonSkirtWeight, 0.35f, 0.0001f, "混合点身体权重份额");
        Assert(mixed.ColumnWeights.Count == 1 && mixed.ColumnWeights[0].ColumnId == "left",
            "混合权重审计应保留原裙列关联。");
        AssertNear(mixed.ColumnWeights[0].Weight, 0.65f, 0.0001f, "混合点裙列权重份额");
        AssertUnchanged(input, before);
        AssertFinite(result);
    }

    private static void TestUnsupportedInputs()
    {
        PMXSkirtTopologyInput noWeights = MakeGrid("a", "b",
            Column(0, 0, 0, 0.2f, 0.4f), Column(1, 0, 0, 0.2f, 0.4f));
        foreach (PMXSkirtVertexWeight weight in noWeights.VertexColumnWeights)
        {
            weight.ColumnWeights.Clear();
            weight.NonSkirtWeight = 1f;
        }
        PMXSkirtTopologyResult noWeightResult = Analyze(noWeights);
        Assert(!noWeightResult.Supported && !string.IsNullOrWhiteSpace(noWeightResult.Reason),
            "没有裙权重的面片必须返回明确 unsupported 原因。");

        PMXSkirtTopologyInput badIndex = MakeGrid("a", "b",
            Column(0, 0, 0, 0.2f, 0.4f), Column(1, 0, 0, 0.2f, 0.4f));
        badIndex.TriangleIndices[0] = int.MaxValue;
        PMXSkirtTopologyResult badIndexResult = Analyze(badIndex);
        Assert(!badIndexResult.Supported && !string.IsNullOrWhiteSpace(badIndexResult.Reason),
            "超界三角索引必须返回明确 unsupported 原因。");

        PMXSkirtTopologyInput nonFinite = MakeGrid("a", "b",
            Column(0, 0, 0, 0.2f, 0.4f), Column(1, 0, 0, 0.2f, 0.4f));
        nonFinite.Vertices[0] = new Vector3(float.NaN, 0, 0);
        PMXSkirtTopologyResult nonFiniteResult = Analyze(nonFinite);
        Assert(!nonFiniteResult.Supported && !string.IsNullOrWhiteSpace(nonFiniteResult.Reason),
            "非有限网格位置必须安全返回 unsupported。");

        PMXSkirtTopologyInput badRest = MakeGrid("a", "b",
            Column(0, 0, 0, 0.2f, 0.4f), Column(1, 0, 0, 0.2f, 0.4f));
        badRest.Columns[0].RestPolyline[1] = new Vector3(float.PositiveInfinity, 0, 0);
        PMXSkirtTopologyResult badRestResult = Analyze(badRest);
        Assert(!badRestResult.Supported && badRestResult.IsIncomplete && !string.IsNullOrWhiteSpace(badRestResult.Reason),
            "非有限静止折线必须报告 unsupported/incomplete 原因。");
    }

    private static PMXSkirtTopologyResult Analyze(PMXSkirtTopologyInput input)
    {
        PMXSkirtTopologyResult result = PMXSkirtTopologyExporter.Analyze(input);
        Assert(result != null, "拓扑分析不得返回 null。");
        return result;
    }

    private static PMXSkirtColumnResult FindColumn(PMXSkirtTopologyResult result, string id)
    {
        PMXSkirtColumnResult column = result.Columns.SingleOrDefault(item => item.ColumnId == id);
        Assert(column != null && column.Supported, "缺少受支持的裙列: " + id);
        return column;
    }

    private static PMXSkirtTopologyInput MakeGrid(string id0, string id1, params Vector3[][] paths)
        => MakeGrid(new[] { id0, id1 }, paths);

    private static PMXSkirtTopologyInput MakeGrid(string id0, string id1, string id2, params Vector3[][] paths)
        => MakeGrid(new[] { id0, id1, id2 }, paths);

    private static PMXSkirtTopologyInput MakeGrid(string[] ids, params Vector3[][] paths)
    {
        Assert(ids.Length == paths.Length && ids.Length >= 2, "网格夹具的列名与折线数量不一致。");
        int rows = paths[0].Length;
        Assert(rows >= 2 && paths.All(path => path.Length == rows), "网格夹具要求每列有相同的至少两个采样点。");
        var input = new PMXSkirtTopologyInput
        {
            Vertices = new List<Vector3>(), TriangleIndices = new List<int>(),
            VertexColumnWeights = new List<PMXSkirtVertexWeight>(), Columns = new List<PMXSkirtColumnInput>()
        };
        for (int column = 0; column < ids.Length; column++)
        {
            input.Vertices.AddRange(paths[column]);
            foreach (Vector3 _ in paths[column])
                input.VertexColumnWeights.Add(new PMXSkirtVertexWeight
                {
                    NonSkirtWeight = 0,
                    ColumnWeights = new List<PMXSkirtColumnWeight>
                    { new PMXSkirtColumnWeight { ColumnId = ids[column], Weight = 1f } }
                });
            input.Columns.Add(new PMXSkirtColumnInput
            {
                ColumnId = ids[column],
                BonePath = new List<string> { "Hip", "Skirt_" + ids[column] },
                RestPolyline = paths[column].ToList()
            });
        }
        for (int column = 0; column < ids.Length - 1; column++)
        {
            for (int row = 0; row < rows - 1; row++)
            {
                int a = column * rows + row;
                int b = (column + 1) * rows + row;
                input.TriangleIndices.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
            }
        }
        return input;
    }

    private static PMXSkirtTopologyInput Join(params PMXSkirtTopologyInput[] inputs)
    {
        var joined = new PMXSkirtTopologyInput
        {
            Vertices = new List<Vector3>(), TriangleIndices = new List<int>(),
            VertexColumnWeights = new List<PMXSkirtVertexWeight>(), Columns = new List<PMXSkirtColumnInput>()
        };
        foreach (PMXSkirtTopologyInput input in inputs)
        {
            int offset = joined.Vertices.Count;
            joined.Vertices.AddRange(input.Vertices);
            joined.TriangleIndices.AddRange(input.TriangleIndices.Select(index => index + offset));
            joined.VertexColumnWeights.AddRange(input.VertexColumnWeights);
            joined.Columns.AddRange(input.Columns);
        }
        return joined;
    }

    private static Vector3[] Column(float x, float y0, float z, float y1, float y2)
        => new[] { new Vector3(x, y0, z), new Vector3(x, y1, z), new Vector3(x, y2, z) };

    private static void RunCase(JArray results, string name, Action test)
    {
        try { test(); results.Add(Result(name, "passed", "行为断言完成")); }
        catch (Exception exception) { results.Add(Result(name, "failed", exception.ToString())); }
    }

    private static JObject Result(string name, string status, string details) => new JObject
    { ["name"] = name, ["status"] = status, ["details"] = details };

    private static JObject SaveReport(JArray cases)
    {
        string[] args = Environment.GetCommandLineArgs();
        string root = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-pmxStage2aRoot") { root = args[i + 1]; break; }
        string outputDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(root)
            ? "Logs/teio-export-stage2a-20261005" : root);
        Directory.CreateDirectory(outputDirectory);
        bool failed = cases.OfType<JObject>().Any(item => (string)item["status"] != "passed");
        var report = new JObject
        {
            ["completedUtc"] = DateTime.UtcNow.ToString("o"),
            ["status"] = failed ? "failed" : "passed",
            ["cases"] = cases
        };
        File.WriteAllText(Path.Combine(outputDirectory, "fixtures.json"), report.ToString());
        return report;
    }

    private static bool IsPair(string a, string b, string x, string y)
        => (a == x && b == y) || (a == y && b == x);

    private static void AssertFinite(PMXSkirtTopologyResult result)
    {
        foreach (PMXSkirtRegionResult region in result.Regions)
            Assert(region.VertexIndices != null && region.TriangleIndices != null && region.BoundaryEdges != null,
                "区域必须返回完整且非空的集合。");
        foreach (PMXSkirtColumnResult column in result.Columns)
        {
            Assert(IsFinite(column.ChainLength) && IsFinite(column.MeshGeodesicLength) &&
                IsFinite(column.TargetLength) && IsFinite(column.RealCoverageLength) &&
                IsFinite(column.RequestedVirtualLength) && IsFinite(column.VirtualCoverageLength) &&
                IsFinite(column.AchievedLength) && IsFinite(column.MaximumChordError),
                "列长度诊断必须为有限值。");
            foreach (Vector3 point in column.MeshPathPoints) Assert(IsFinite(point), "测地路径必须为有限坐标。");
            foreach (PMXSkirtSegmentLayout segment in column.Segments)
                Assert(IsFinite(segment.Start) && IsFinite(segment.End), "输出分段端点必须为有限坐标。");
        }
    }

    private static InputSnapshot Snapshot(PMXSkirtTopologyInput input)
        => new InputSnapshot
        {
            Vertices = input.Vertices.ToArray(), Triangles = input.TriangleIndices.ToArray(),
            Polylines = input.Columns.Select(column => column.RestPolyline.ToArray()).ToArray(),
            BonePaths = input.Columns.Select(column => column.BonePath.ToArray()).ToArray(),
            WeightIds = input.VertexColumnWeights.Select(weight => weight.ColumnWeights.Select(item => item.ColumnId).ToArray()).ToArray(),
            WeightValues = input.VertexColumnWeights.Select(weight => weight.ColumnWeights.Select(item => item.Weight).ToArray()).ToArray(),
            NonSkirtWeights = input.VertexColumnWeights.Select(weight => weight.NonSkirtWeight).ToArray(),
            TargetSegmentLength = input.TargetSegmentLength, LengthTolerance = input.LengthTolerance,
            MaximumSegments = input.MaximumSegments, MaximumVirtualLengthRatio = input.MaximumVirtualLengthRatio,
            Normals = input.VertexNormals.ToArray(), Groups = input.VertexGroups.ToArray(), SkinSignatures = input.VertexSkinSignatures.ToArray(),
            Seams = input.SeamPairs.Select(s => new[] { s.EdgeA0, s.EdgeA1, s.EdgeB0, s.EdgeB1 }).ToArray()
        };

    private static void AssertUnchanged(PMXSkirtTopologyInput input, InputSnapshot before)
    {
        Assert(input.Vertices.SequenceEqual(before.Vertices) && input.TriangleIndices.SequenceEqual(before.Triangles),
            "拓扑分析不得修改源网格顶点或三角索引。");
        Assert(input.VertexNormals.SequenceEqual(before.Normals) && input.VertexGroups.SequenceEqual(before.Groups) &&
            input.VertexSkinSignatures.SequenceEqual(before.SkinSignatures) &&
            input.SeamPairs.Count == before.Seams.Length && input.SeamPairs.Select((s, i) =>
                new[] { s.EdgeA0, s.EdgeA1, s.EdgeB0, s.EdgeB1 }.SequenceEqual(before.Seams[i])).All(value => value),
            "分析不得修改法线、来源和输入接缝集合。");
        Assert(input.Columns.Count == before.Polylines.Length, "拓扑分析不得增删输入裙列。");
        for (int i = 0; i < input.Columns.Count; i++)
            Assert(input.Columns[i].RestPolyline.SequenceEqual(before.Polylines[i]) &&
                input.Columns[i].BonePath.SequenceEqual(before.BonePaths[i]), "拓扑分析不得修改输入骨路径或静止折线。");
        Assert(input.VertexColumnWeights.Count == before.NonSkirtWeights.Length,
            "拓扑分析不得增删源顶点权重。");
        for (int i = 0; i < input.VertexColumnWeights.Count; i++)
        {
            PMXSkirtVertexWeight weight = input.VertexColumnWeights[i];
            Assert(weight.NonSkirtWeight == before.NonSkirtWeights[i] &&
                weight.ColumnWeights.Select(item => item.ColumnId).SequenceEqual(before.WeightIds[i]) &&
                weight.ColumnWeights.Select(item => item.Weight).SequenceEqual(before.WeightValues[i]),
                "拓扑分析不得更改腰口或混合裙权重。");
        }
        Assert(input.TargetSegmentLength == before.TargetSegmentLength && input.LengthTolerance == before.LengthTolerance &&
            input.MaximumSegments == before.MaximumSegments &&
            input.MaximumVirtualLengthRatio == before.MaximumVirtualLengthRatio,
            "拓扑分析不得更改输入分段配置。");
    }

    private static void AssertNear(float actual, float expected, float tolerance, string label)
        => Assert(Mathf.Abs(actual - expected) <= tolerance,
            label + "预期约 " + expected + "，实际 " + actual + "。");

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
