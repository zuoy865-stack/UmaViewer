using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>裙网格拓扑、测地长度和代理布局的独立分析输入。</summary>
public sealed class PMXSkirtTopologyInput
{
    public List<Vector3> Vertices = new List<Vector3>();
    public List<int> TriangleIndices = new List<int>();
    public List<PMXSkirtVertexWeight> VertexColumnWeights = new List<PMXSkirtVertexWeight>();
    public List<Vector3> VertexNormals = new List<Vector3>();
    public List<int> VertexGroups = new List<int>();
    public List<string> VertexSkinSignatures = new List<string>();
    public List<PMXSkirtColumnInput> Columns = new List<PMXSkirtColumnInput>();
    public List<PMXSkirtSeamPair> SeamPairs = new List<PMXSkirtSeamPair>();
    public float TargetSegmentLength = 0.08f;
    public float LengthTolerance = 0.002f;
    public int MaximumSegments = 64;
    public float MaximumVirtualLengthRatio = 1.5f;
}

public sealed class PMXSkirtSeamPair
{
    public int EdgeA0, EdgeA1, EdgeB0, EdgeB1;
}

public sealed class PMXSkirtVertexWeight
{
    public List<PMXSkirtColumnWeight> ColumnWeights = new List<PMXSkirtColumnWeight>();
    public float NonSkirtWeight;
}

public sealed class PMXSkirtColumnWeight
{
    public string ColumnId;
    public float Weight;
}

public sealed class PMXSkirtColumnInput
{
    public string ColumnId;
    public List<string> BonePath = new List<string>();
    public List<Vector3> RestPolyline = new List<Vector3>();
    public List<string> NeighborColumnIds = new List<string>();
}

public sealed class PMXSkirtTopologyResult
{
    public bool Supported;
    public bool IsIncomplete;
    public string Reason;
    public List<PMXSkirtRegionResult> Regions = new List<PMXSkirtRegionResult>();
    public List<PMXSkirtColumnResult> Columns = new List<PMXSkirtColumnResult>();
    public List<PMXSkirtVertexWeightAudit> WeightAudit = new List<PMXSkirtVertexWeightAudit>();
    public List<int> IsolatedSkirtVertexIndices = new List<int>();
}

public sealed class PMXSkirtRegionResult
{
    public int ComponentId;
    public List<int> TriangleIndices = new List<int>();
    public List<int> VertexIndices = new List<int>();
    public List<PMXSkirtBoundaryEdge> BoundaryEdges = new List<PMXSkirtBoundaryEdge>();
    public bool IsClosed;
    public bool CircumferentiallyClosed;
    public bool HasAmbiguousBridge;
    public List<PMXSkirtColumnPair> ColumnNeighbors = new List<PMXSkirtColumnPair>();
    public int DuplicateTriangleCount;
    public int DegenerateTriangleCount;
    public int IsolatedSkirtVertexCount;
    public string Reason;
}

public sealed class PMXSkirtColumnPair
{
    public string A, B;
}

public sealed class PMXSkirtBoundaryEdge
{
    public int VertexA, VertexB;
    public string ColumnPairA, ColumnPairB;
}

public sealed class PMXSkirtColumnResult
{
    public string ColumnId;
    public bool Supported;
    public string Reason;
    public int ComponentId = -1;
    public float ChainLength, MeshGeodesicLength, LengthDelta, TargetLength;
    public float RealCoverageLength, RequestedVirtualLength, VirtualCoverageLength, AchievedLength, MaximumChordError;
    public float CenterlineLength;
    public float TargetSegmentLength;
    public bool IsIncomplete;
    public string BudgetReason;
    public int RootEndpointVertex = -1, HemEndpointVertex = -1, CorridorVertexCount, RootReachableVertexCount;
    public List<int> MeshPathVertexIndices = new List<int>();
    public List<Vector3> MeshPathPoints = new List<Vector3>();
    public List<PMXSkirtSegmentLayout> Segments = new List<PMXSkirtSegmentLayout>();
}

public sealed class PMXSkirtSegmentLayout
{
    public int RowIndex;
    public Vector3 Start, End;
    public bool IsVirtual;
}

public sealed class PMXSkirtVertexWeightAudit
{
    public int VertexIndex;
    public float NonSkirtWeight, TotalWeight;
    public List<PMXSkirtColumnWeight> ColumnWeights = new List<PMXSkirtColumnWeight>();
}

/// <summary>只分析输入拓扑；不创建导出骨、权重或物理对象。</summary>
public static class PMXSkirtTopologyExporter
{
    private const float WeightEpsilon = 0.00001f;

    private sealed class Tri
    {
        internal int A, B, C, Source;
        internal int Component = -1;
        internal string Key;
        internal bool WeakBridge;
    }

    private struct Edge : IEquatable<Edge>
    {
        internal int A, B;
        internal Edge(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
        public bool Equals(Edge other) { return A == other.A && B == other.B; }
        public override bool Equals(object obj) { return obj is Edge && Equals((Edge)obj); }
        public override int GetHashCode() { unchecked { return A * 397 ^ B; } }
    }

    private sealed class EdgeUse
    {
        internal int Tri;
        internal Edge Edge;
    }

    private sealed class MinHeap
    {
        private readonly List<KeyValuePair<float, int>> _items = new List<KeyValuePair<float, int>>();
        internal int Count { get { return _items.Count; } }
        internal void Push(float priority, int value)
        {
            int i = _items.Count; _items.Add(new KeyValuePair<float, int>(priority, value));
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_items[p].Key <= priority) break;
                _items[i] = _items[p]; i = p;
            }
            _items[i] = new KeyValuePair<float, int>(priority, value);
        }
        internal KeyValuePair<float, int> Pop()
        {
            var root = _items[0]; var last = _items[_items.Count - 1]; _items.RemoveAt(_items.Count - 1);
            if (_items.Count == 0) return root;
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1;
                if (l >= _items.Count) break;
                int c = r < _items.Count && _items[r].Key < _items[l].Key ? r : l;
                if (_items[c].Key >= last.Key) break;
                _items[i] = _items[c]; i = c;
            }
            _items[i] = last; return root;
        }
    }

    public static PMXSkirtTopologyResult Analyze(PMXSkirtTopologyInput input)
    {
        var result = new PMXSkirtTopologyResult();
        if (!Validate(input, result)) { result.Supported = false; result.IsIncomplete = true; return result; }
        PMXSkirtTopologyInput analysisInput = CopyForAnalysis(input);
        BuildWeightAudit(analysisInput, result);
        var unique = BuildTriangles(analysisInput, result);
        PopulateProvenSeamPairs(analysisInput, unique);
        var edgeUses = BuildComponents(analysisInput, unique, result);
        BuildBoundaryDiagnostics(analysisInput, unique, edgeUses, result);
        BuildColumns(analysisInput, unique, edgeUses, result);
        result.Supported = result.Columns.Any(c => c.Supported);
        result.IsIncomplete = !result.Supported || result.Columns.Any(c => !c.Supported || c.IsIncomplete) ||
            result.Regions.Any(r => r.HasAmbiguousBridge) || result.IsolatedSkirtVertexIndices.Count > 0;
        if (!result.Supported) result.Reason = "没有列能在裙网格组件中建立有效的加权测地路径。";
        else if (result.Columns.Any(c => !c.Supported)) result.Reason = "部分输入裙列无法建立有效路径；结果覆盖不完整。";
        if (result.Regions.Any(r => r.HasAmbiguousBridge))
            result.Reason = AppendReason(result.Reason, "跨列弱桥的层归属未证明，布局仅供诊断。");
        if (result.IsolatedSkirtVertexIndices.Count > 0)
            result.Reason = AppendReason(result.Reason, "存在不属于有效裙三角的正权重顶点。");
        return result;
    }

    private static PMXSkirtTopologyInput CopyForAnalysis(PMXSkirtTopologyInput input)
    {
        return new PMXSkirtTopologyInput
        {
            Vertices = input.Vertices,
            TriangleIndices = input.TriangleIndices,
            VertexColumnWeights = input.VertexColumnWeights,
            VertexNormals = input.VertexNormals,
            VertexGroups = input.VertexGroups,
            VertexSkinSignatures = input.VertexSkinSignatures,
            Columns = input.Columns,
            SeamPairs = input.SeamPairs == null ? new List<PMXSkirtSeamPair>() : new List<PMXSkirtSeamPair>(input.SeamPairs),
            TargetSegmentLength = input.TargetSegmentLength,
            LengthTolerance = input.LengthTolerance,
            MaximumSegments = input.MaximumSegments,
            MaximumVirtualLengthRatio = input.MaximumVirtualLengthRatio
        };
    }

    public static int PopulateProvenSeamPairs(PMXSkirtTopologyInput input)
    {
        if (input == null || input.VertexNormals == null || input.VertexNormals.Count != input.Vertices.Count ||
            input.VertexGroups == null || input.VertexGroups.Count != input.Vertices.Count) return 0;
        var diagnostics = new PMXSkirtTopologyResult();
        List<Tri> triangles = BuildTriangles(input, diagnostics);
        return PopulateProvenSeamPairs(input, triangles);
    }

    private sealed class SeamEdgeUse
    {
        internal int A, B, Group;
        internal Vector3 FaceNormal;
    }

    private static int PopulateProvenSeamPairs(PMXSkirtTopologyInput input, List<Tri> triangles)
    {
        if (input.SeamPairs == null) input.SeamPairs = new List<PMXSkirtSeamPair>();
        input.SeamPairs.Clear();
        if (input.VertexNormals == null || input.VertexNormals.Count != input.Vertices.Count ||
            input.VertexGroups == null || input.VertexGroups.Count != input.Vertices.Count) return 0;
        var uses = new Dictionary<string, List<SeamEdgeUse>>(StringComparer.Ordinal);
        foreach (Tri tri in triangles)
        {
            int group = input.VertexGroups[tri.A];
            if (input.VertexGroups[tri.B] != group || input.VertexGroups[tri.C] != group) continue;
            Vector3 face = Vector3.Cross(input.Vertices[tri.B] - input.Vertices[tri.A], input.Vertices[tri.C] - input.Vertices[tri.A]);
            if (face.sqrMagnitude < 1e-12f) continue;
            face.Normalize();
            AddSeamEdge(uses, input, tri.A, tri.B, group, face);
            AddSeamEdge(uses, input, tri.B, tri.C, group, face);
            AddSeamEdge(uses, input, tri.C, tri.A, group, face);
        }
        foreach (List<SeamEdgeUse> pair in uses.Values)
        {
            if (pair.Count != 2) continue;
            SeamEdgeUse a = pair[0], b = pair[1];
            if (a.Group != b.Group || a.A == b.B || a.B == b.A ||
                !SamePosition(input.Vertices[a.A], input.Vertices[b.B]) ||
                !SamePosition(input.Vertices[a.B], input.Vertices[b.A])) continue;
            // 裙面接缝可能跨越折面；顶点法线与蒙皮族更直接证明可焊接性。
            if (!SameSourceSkin(input, a.A, b.B) || !SameSourceSkin(input, a.B, b.A)) continue;
            input.SeamPairs.Add(new PMXSkirtSeamPair
            { EdgeA0 = a.A, EdgeA1 = a.B, EdgeB0 = b.A, EdgeB1 = b.B });
        }
        return input.SeamPairs.Count;
    }

    private static bool SameSourceSkin(PMXSkirtTopologyInput input, int a, int b)
    {
        if (input.VertexSkinSignatures != null && input.VertexSkinSignatures.Count == input.Vertices.Count)
            return !string.IsNullOrEmpty(input.VertexSkinSignatures[a]) &&
                string.Equals(input.VertexSkinSignatures[a], input.VertexSkinSignatures[b], StringComparison.Ordinal);
        return Vector3.Dot(input.VertexNormals[a], input.VertexNormals[b]) >= 0.995f &&
            SameSkirtInfluence(input.VertexColumnWeights[a], input.VertexColumnWeights[b]);
    }

    private static void AddSeamEdge(Dictionary<string, List<SeamEdgeUse>> uses,
        PMXSkirtTopologyInput input, int a, int b, int group, Vector3 face)
    {
        string pa = PositionKey(input.Vertices[a]), pb = PositionKey(input.Vertices[b]);
        string key = group + "|" + (string.CompareOrdinal(pa, pb) <= 0 ? pa + "|" + pb : pb + "|" + pa);
        if (!uses.TryGetValue(key, out var list)) uses[key] = list = new List<SeamEdgeUse>();
        list.Add(new SeamEdgeUse { A = a, B = b, Group = group, FaceNormal = face });
    }

    private static string PositionKey(Vector3 point)
    { return Mathf.RoundToInt(point.x * 1000000f) + ":" + Mathf.RoundToInt(point.y * 1000000f) + ":" + Mathf.RoundToInt(point.z * 1000000f); }

    private static bool SamePosition(Vector3 a, Vector3 b)
    { return (a - b).sqrMagnitude <= 1e-12f; }

    private static bool SameSkirtInfluence(PMXSkirtVertexWeight a, PMXSkirtVertexWeight b)
    {
        if (Mathf.Abs(a.NonSkirtWeight - b.NonSkirtWeight) > 0.12f) return false;
        float sumA = a.ColumnWeights.Sum(x => x.Weight), sumB = b.ColumnWeights.Sum(x => x.Weight);
        if (Mathf.Abs(sumA - sumB) > 0.12f) return false;
        var ids = a.ColumnWeights.Select(x => x.ColumnId).Union(b.ColumnWeights.Select(x => x.ColumnId), StringComparer.Ordinal);
        return ids.Sum(id => Mathf.Abs(
            a.ColumnWeights.Where(x => x.ColumnId == id).Sum(x => x.Weight) -
            b.ColumnWeights.Where(x => x.ColumnId == id).Sum(x => x.Weight))) <= 0.18f;
    }

    private static bool Validate(PMXSkirtTopologyInput input, PMXSkirtTopologyResult result)
    {
        if (input == null) { result.Reason = "缺少输入。"; return false; }
        if (input.Vertices == null || input.Vertices.Count == 0 || input.TriangleIndices == null ||
            input.TriangleIndices.Count < 3 || input.TriangleIndices.Count % 3 != 0)
        { result.Reason = "缺少有效顶点或三角索引。"; return false; }
        if (input.VertexColumnWeights == null || input.VertexColumnWeights.Count != input.Vertices.Count)
        { result.Reason = "裙列权重数量必须与顶点数量相同。"; return false; }
        if ((input.VertexNormals != null && input.VertexNormals.Count != 0 && input.VertexNormals.Count != input.Vertices.Count) ||
            (input.VertexGroups != null && input.VertexGroups.Count != 0 && input.VertexGroups.Count != input.Vertices.Count) ||
            (input.VertexSkinSignatures != null && input.VertexSkinSignatures.Count != 0 && input.VertexSkinSignatures.Count != input.Vertices.Count))
        { result.Reason = "接缝法线或网格来源映射数量不匹配。"; return false; }
        if (input.Columns == null || input.Columns.Count == 0)
        { result.Reason = "缺少已证明的裙列输入。"; return false; }
        if (!Finite(input.TargetSegmentLength) || input.TargetSegmentLength <= 0 ||
            !Finite(input.LengthTolerance) || input.LengthTolerance < 0 || input.MaximumSegments < 1 ||
            !Finite(input.MaximumVirtualLengthRatio) || input.MaximumVirtualLengthRatio < 1)
        { result.Reason = "分段长度、容差或预算无效。"; return false; }
        if (input.Vertices.Any(v => !Finite(v))) { result.Reason = "顶点包含非有限坐标。"; return false; }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in input.Columns)
            if (c == null || string.IsNullOrWhiteSpace(c.ColumnId) || !ids.Add(c.ColumnId) || c.RestPolyline == null ||
                c.RestPolyline.Any(p => !Finite(p)))
            { result.Reason = "裙列 ID 重复，或骨链折线缺失。"; return false; }
        foreach (int index in input.TriangleIndices)
            if (index < 0 || index >= input.Vertices.Count) { result.Reason = "三角索引越界。"; return false; }
        foreach (var w in input.VertexColumnWeights)
        {
            if (w == null || w.ColumnWeights == null || !Finite(w.NonSkirtWeight) || w.NonSkirtWeight < 0)
            { result.Reason = "裙权重数据无效。"; return false; }
            float sum = w.NonSkirtWeight;
            foreach (var cw in w.ColumnWeights)
            {
                if (cw == null || !ids.Contains(cw.ColumnId) || !Finite(cw.Weight) || cw.Weight < 0)
                { result.Reason = "裙列权重含未知列或非法数值。"; return false; }
                sum += cw.Weight;
            }
            if (!Finite(sum)) { result.Reason = "顶点权重总和溢出。"; return false; }
        }
        return true;
    }

    private static void BuildWeightAudit(PMXSkirtTopologyInput input, PMXSkirtTopologyResult result)
    {
        for (int i = 0; i < input.VertexColumnWeights.Count; i++)
        {
            var w = input.VertexColumnWeights[i];
            var copy = w.ColumnWeights.Select(x => new PMXSkirtColumnWeight { ColumnId = x.ColumnId, Weight = x.Weight }).ToList();
            result.WeightAudit.Add(new PMXSkirtVertexWeightAudit
            {
                VertexIndex = i, NonSkirtWeight = w.NonSkirtWeight, ColumnWeights = copy,
                TotalWeight = w.NonSkirtWeight + copy.Sum(x => x.Weight)
            });
        }
    }

    private static List<Tri> BuildTriangles(PMXSkirtTopologyInput input, PMXSkirtTopologyResult result)
    {
        var output = new List<Tri>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < input.TriangleIndices.Count; i += 3)
        {
            int a = input.TriangleIndices[i], b = input.TriangleIndices[i + 1], c = input.TriangleIndices[i + 2];
            int skirtVertices = new[] { a, b, c }.Distinct().Count(v => HasSkirtWeight(input.VertexColumnWeights[v]));
            if (skirtVertices < 2) continue;
            if (a == b || b == c || c == a) continue;
            string key = TriangleKey(a, b, c);
            if (!seen.Add(key)) continue;
            int columns = new[] { a, b, c }.SelectMany(v => input.VertexColumnWeights[v].ColumnWeights)
                .Where(w => w.Weight > WeightEpsilon).Select(w => w.ColumnId).Distinct().Count();
            float maxSkirtWeight = new[] { a, b, c }.Max(v => input.VertexColumnWeights[v].ColumnWeights.Sum(w => w.Weight));
            output.Add(new Tri
            {
                A = a, B = b, C = c, Source = i / 3, Key = key,
                // 固定腰口里少量跨列权重不构成弱桥；只标记裙权重整体很弱的连接。
                WeakBridge = columns > 1 && maxSkirtWeight < 0.25f
            });
        }
        return output;
    }

    private static Dictionary<Edge, List<int>> BuildComponents(PMXSkirtTopologyInput input,
        List<Tri> triangles, PMXSkirtTopologyResult result)
    {
        var uses = new Dictionary<Edge, List<int>>();
        for (int i = 0; i < triangles.Count; i++)
        {
            Tri t = triangles[i]; AddUse(uses, new Edge(t.A, t.B), i); AddUse(uses, new Edge(t.B, t.C), i); AddUse(uses, new Edge(t.C, t.A), i);
        }
        var adjacency = new List<int>[triangles.Count];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();
        foreach (var pair in uses.Values)
            for (int a = 0; a < pair.Count; a++) for (int b = a + 1; b < pair.Count; b++)
            { adjacency[pair[a]].Add(pair[b]); adjacency[pair[b]].Add(pair[a]); }
        foreach (PMXSkirtSeamPair seam in input.SeamPairs ?? new List<PMXSkirtSeamPair>())
        {
            if (!uses.TryGetValue(new Edge(seam.EdgeA0, seam.EdgeA1), out var left) ||
                !uses.TryGetValue(new Edge(seam.EdgeB0, seam.EdgeB1), out var right)) continue;
            foreach (int a in left) foreach (int b in right)
            { adjacency[a].Add(b); adjacency[b].Add(a); }
        }
        int component = 0;
        for (int i = 0; i < triangles.Count; i++)
        {
            if (triangles[i].Component >= 0) continue;
            var queue = new Queue<int>(); queue.Enqueue(i); triangles[i].Component = component;
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                foreach (int next in adjacency[at]) if (triangles[next].Component < 0)
                { triangles[next].Component = component; queue.Enqueue(next); }
            }
            component++;
        }
        var byComponent = triangles.GroupBy(t => t.Component).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var entry in byComponent)
        {
            var region = new PMXSkirtRegionResult { ComponentId = entry.Key, TriangleIndices = entry.Value.Select(t => t.Source).ToList() };
            region.VertexIndices = entry.Value.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().OrderBy(v => v).ToList();
            result.Regions.Add(region);
        }
        return uses;
    }

    private static void BuildBoundaryDiagnostics(PMXSkirtTopologyInput input, List<Tri> triangles,
        Dictionary<Edge, List<int>> uses, PMXSkirtTopologyResult result)
    {
        var compByTri = triangles.ToDictionary(t => t.Source, t => t.Component);
        foreach (var region in result.Regions)
        {
            var edges = uses.Where(p => p.Value.Any(t => compByTri[triangles[t].Source] == region.ComponentId));
            foreach (var pair in edges)
            {
                int count = pair.Value.Count(t => triangles[t].Component == region.ComponentId);
                if (count != 1 || IsSeamEdge(input, pair.Key)) continue;
                region.BoundaryEdges.Add(new PMXSkirtBoundaryEdge
                {
                    VertexA = pair.Key.A, VertexB = pair.Key.B,
                    ColumnPairA = DominantColumn(input.VertexColumnWeights[pair.Key.A]),
                    ColumnPairB = DominantColumn(input.VertexColumnWeights[pair.Key.B])
                });
            }
            region.IsClosed = region.BoundaryEdges.Count == 0;
            region.CircumferentiallyClosed = HasCircumferentialCycle(input, region, triangles);
            region.HasAmbiguousBridge = entryHasWeakBridge(input, triangles, uses, region.ComponentId);
            region.DuplicateTriangleCount = CountDuplicateTriangles(input, region.TriangleIndices);
            region.DegenerateTriangleCount = CountDegenerateTriangles(input, region.TriangleIndices);
            region.IsolatedSkirtVertexCount = 0;
            region.Reason = region.HasAmbiguousBridge ? "组件含裙权重较弱的跨列连接，保留为歧义桥，不拆层。" :
                region.CircumferentiallyClosed ? "裙列邻接证据构成周向环。" : "未证明周向闭环；保留裙面边界。";
        }
        // 身体三角中的单个裙权重点仍有真实三角邻接，不应误报为孤立裙点。
        var usedVertices = new HashSet<int>();
        for (int i = 0; i < input.TriangleIndices.Count; i += 3)
        {
            int a = input.TriangleIndices[i], b = input.TriangleIndices[i + 1], c = input.TriangleIndices[i + 2];
            if (a == b || b == c || c == a) continue;
            if (HasSkirtWeight(input.VertexColumnWeights[a]) ||
                HasSkirtWeight(input.VertexColumnWeights[b]) ||
                HasSkirtWeight(input.VertexColumnWeights[c]))
            {
                usedVertices.Add(a); usedVertices.Add(b); usedVertices.Add(c);
            }
        }
        result.IsolatedSkirtVertexIndices = Enumerable.Range(0, input.VertexColumnWeights.Count)
            .Where(v => HasSkirtWeight(input.VertexColumnWeights[v]) && !usedVertices.Contains(v)).ToList();
        if (result.Regions.Count == 1) result.Regions[0].IsolatedSkirtVertexCount = result.IsolatedSkirtVertexIndices.Count;
    }

    private static bool entryHasWeakBridge(PMXSkirtTopologyInput input, List<Tri> triangles,
        Dictionary<Edge, List<int>> edgeUses, int componentId)
    {
        var adjacency = triangles.Select(_ => new HashSet<int>()).ToArray();
        foreach (var edge in edgeUses.Values)
            for (int i = 0; i < edge.Count; i++) for (int j = i + 1; j < edge.Count; j++)
            { adjacency[edge[i]].Add(edge[j]); adjacency[edge[j]].Add(edge[i]); }
        foreach (PMXSkirtSeamPair seam in input.SeamPairs ?? new List<PMXSkirtSeamPair>())
            if (edgeUses.TryGetValue(new Edge(seam.EdgeA0, seam.EdgeA1), out var a) &&
                edgeUses.TryGetValue(new Edge(seam.EdgeB0, seam.EdgeB1), out var b))
                foreach (int left in a) foreach (int right in b) { adjacency[left].Add(right); adjacency[right].Add(left); }
        foreach (int candidate in triangles.Select((tri, index) => new { tri, index })
            .Where(item => item.tri.Component == componentId && item.tri.WeakBridge).Select(item => item.index))
        {
            int[] neighbors = adjacency[candidate].Where(index => triangles[index].Component == componentId).ToArray();
            if (neighbors.Length < 2) continue;
            var reached = new HashSet<int>(); var queue = new Queue<int>(); queue.Enqueue(neighbors[0]);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                if (at == candidate || !reached.Add(at)) continue;
                foreach (int next in adjacency[at]) if (next != candidate && triangles[next].Component == componentId) queue.Enqueue(next);
            }
            if (neighbors.Skip(1).Any(neighbor => !reached.Contains(neighbor))) return true;
        }
        return false;
    }

    private static bool HasCircumferentialCycle(PMXSkirtTopologyInput input,
        PMXSkirtRegionResult region, List<Tri> triangles)
    {
        var neighbors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Tri tri in triangles.Where(t => t.Component == region.ComponentId))
        {
            var present = new HashSet<string>(new[] { tri.A, tri.B, tri.C }
                .SelectMany(v => input.VertexColumnWeights[v].ColumnWeights)
                .Where(weight => weight.Weight > WeightEpsilon).Select(weight => weight.ColumnId), StringComparer.Ordinal);
            foreach (PMXSkirtColumnInput column in input.Columns)
            {
                if (!present.Contains(column.ColumnId)) continue;
                foreach (string neighborId in column.NeighborColumnIds ?? new List<string>())
                {
                    if (!present.Contains(neighborId)) continue;
                    if (!neighbors.TryGetValue(column.ColumnId, out var aa)) neighbors[column.ColumnId] = aa = new HashSet<string>(StringComparer.Ordinal);
                    aa.Add(neighborId);
                    if (!region.ColumnNeighbors.Any(pair =>
                        (pair.A == column.ColumnId && pair.B == neighborId) ||
                        (pair.A == neighborId && pair.B == column.ColumnId)))
                        region.ColumnNeighbors.Add(new PMXSkirtColumnPair { A = column.ColumnId, B = neighborId });
                }
            }
        }
        if (neighbors.Count < 3 || neighbors.Values.Any(set => set.Count != 2)) return false;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(); queue.Enqueue(neighbors.Keys.First());
        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (string next in neighbors[id]) queue.Enqueue(next);
        }
        return visited.Count == neighbors.Count;
    }

    private static bool IsSeamEdge(PMXSkirtTopologyInput input, Edge edge)
    {
        return (input.SeamPairs ?? new List<PMXSkirtSeamPair>()).Any(seam =>
            (new Edge(seam.EdgeA0, seam.EdgeA1).Equals(edge) || new Edge(seam.EdgeB0, seam.EdgeB1).Equals(edge)));
    }

    private static void BuildColumns(PMXSkirtTopologyInput input, List<Tri> triangles,
        Dictionary<Edge, List<int>> edgeUses, PMXSkirtTopologyResult result)
    {
        var vertexComponents = new Dictionary<int, HashSet<int>>();
        foreach (var t in triangles) foreach (int v in new[] { t.A, t.B, t.C })
        {
            if (!vertexComponents.TryGetValue(v, out var set)) vertexComponents[v] = set = new HashSet<int>();
            set.Add(t.Component);
        }
        var preliminary = new List<Tuple<PMXSkirtColumnResult, List<Vector3>, float>>();
        foreach (var column in input.Columns)
        {
            var r = new PMXSkirtColumnResult { ColumnId = column.ColumnId, ChainLength = PolylineLength(column.RestPolyline) };
            result.Columns.Add(r);
            if (r.ChainLength <= input.LengthTolerance)
            { r.Reason = "原骨链折线长度不足，无法用于根端/裙摆边界定位。"; continue; }
            var support = new Dictionary<int, float>();
            for (int v = 0; v < input.VertexColumnWeights.Count; v++)
            {
                float w = GetWeight(input.VertexColumnWeights[v], column.ColumnId);
                if (w <= WeightEpsilon || !vertexComponents.TryGetValue(v, out var comps)) continue;
                foreach (int comp in comps) support[comp] = support.TryGetValue(comp, out float old) ? old + w : w;
            }
            if (support.Count == 0) { r.Reason = "该列没有落在有效裙三角中的正权重顶点。"; continue; }
            var ordered = support.OrderByDescending(x => x.Value).ToList();
            if (ordered.Count > 1 && ordered[1].Value >= ordered[0].Value * 0.25f)
            { r.Reason = "该列权重显著跨越多个断开的拓扑组件，归属不明确。"; continue; }
            r.ComponentId = ordered[0].Key;
            var compTris = triangles.Where(t => t.Component == r.ComponentId).ToList();
            var allowed = BuildColumnGraph(input, column.ColumnId, compTris);
            r.CorridorVertexCount = allowed.Count;
            if (allowed.Count < 2) { r.Reason = "列权重支持的网格区域不足以建立路径。"; continue; }
            var boundaryVertices = GetBoundaryVertices(input, compTris, triangles, edgeUses);
            var endpoints = FindEndpoints(input, column, allowed, boundaryVertices);
            r.RootEndpointVertex = endpoints.Item1;
            r.HemEndpointVertex = endpoints.Item2;
            if (endpoints.Item1 < 0 || endpoints.Item2 < 0 || endpoints.Item1 == endpoints.Item2)
            { r.Reason = "无法从骨链折线投影确定根端与裙摆边界端。"; continue; }
            r.RootReachableVertexCount = ReachableCount(allowed, endpoints.Item1);
            var path = ShortestPath(input.Vertices, allowed, endpoints.Item1, endpoints.Item2);
            if (path.Count < 2) { r.Reason = "裙区三角边图中不存在根端到边界端路径。"; continue; }
            float meshLength = PathLength(input.Vertices, path);
            r.MeshPathVertexIndices = path;
            r.MeshPathPoints = path.Select(v => input.Vertices[v]).ToList();
            r.MeshGeodesicLength = meshLength;
            r.LengthDelta = meshLength - r.ChainLength;
            List<Vector3> centerline = BuildCenterline(input, column, compTris, endpoints, meshLength);
            float centerlineLength = PolylineLength(centerline);
            r.CenterlineLength = centerlineLength;
            r.RealCoverageLength = centerlineLength;
            r.Supported = meshLength > input.LengthTolerance && centerlineLength > input.LengthTolerance;
            if (!r.Supported) { r.Reason = "测地路径短于设定容差。"; continue; }
            preliminary.Add(Tuple.Create(r, centerline, centerlineLength));
        }
        foreach (var group in preliminary.GroupBy(x => x.Item1.ComponentId))
        {
            float target = group.Max(x => x.Item3);
            // Teio 短裙至少四段，长裙按 8cm 基准增加段数。
            int baseSegments = Mathf.Max(4, Mathf.CeilToInt(target / input.TargetSegmentLength));
            float spacing = target / baseSegments;
            int shared = baseSegments;
            while (shared < input.MaximumSegments)
            {
                bool fits = group.All(item =>
                {
                    float layout = LayoutLength(input, item.Item3, target);
                    if (layout > item.Item3 + input.LengthTolerance && !HasTailTangent(item.Item2)) layout = item.Item3;
                    return LayoutFitError(input, item.Item2, layout, shared, spacing) <= input.LengthTolerance;
                });
                if (fits) break;
                shared++;
            }
            foreach (var item in group)
            {
                var r = item.Item1;
                r.TargetLength = target;
                BuildLayout(input, r, item.Item2, item.Item3, target, shared, spacing);
            }
        }
    }

    private static Dictionary<int, HashSet<int>> BuildColumnGraph(PMXSkirtTopologyInput input, string id, List<Tri> tris)
    {
        var graph = new Dictionary<int, HashSet<int>>();
        var inComponent = new HashSet<int>(tris.SelectMany(tri => new[] { tri.A, tri.B, tri.C }));
        foreach (var t in tris)
        {
            // 列权重只选根/裙摆端点；中间测地走完整个已证明组件，涵盖稀疏渐变权重。
            AddGraphEdge(graph, t.A, t.B); AddGraphEdge(graph, t.B, t.C); AddGraphEdge(graph, t.C, t.A);
        }
        foreach (PMXSkirtSeamPair seam in input.SeamPairs ?? new List<PMXSkirtSeamPair>())
            if (inComponent.Contains(seam.EdgeA0) && inComponent.Contains(seam.EdgeA1) &&
                inComponent.Contains(seam.EdgeB0) && inComponent.Contains(seam.EdgeB1))
            { AddGraphEdge(graph, seam.EdgeA0, seam.EdgeB1); AddGraphEdge(graph, seam.EdgeA1, seam.EdgeB0); }
        return graph;
    }

    private static List<Vector3> BuildCenterline(PMXSkirtTopologyInput input, PMXSkirtColumnInput column,
        List<Tri> componentTriangles, Tuple<int, int> endpoints, float meshLength)
    {
        // 按骨链进度求裙面加权截面中心，避免三角边锯齿直接决定骨段密度。
        int bins = Mathf.Clamp(Mathf.Max(8, Mathf.CeilToInt(meshLength / 0.05f)), 8, 16);
        var sums = new Vector3[bins]; var totals = new float[bins];
        var vertices = componentTriangles.SelectMany(tri => new[] { tri.A, tri.B, tri.C }).Distinct();
        float progressStart = ProjectProgress(input.Vertices[endpoints.Item1], column.RestPolyline);
        float progressEnd = ProjectProgress(input.Vertices[endpoints.Item2], column.RestPolyline);
        float progressSpan = progressEnd - progressStart;
        if (Mathf.Abs(progressSpan) < 1e-5f) progressSpan = 1f;
        foreach (int vertex in vertices)
        {
            float weight = GetWeight(input.VertexColumnWeights[vertex], column.ColumnId);
            if (weight <= WeightEpsilon) continue;
            // 以正权重根/裙摆端点归一进度，容许短原骨链端外的裙面继续展开。
            float progress = Mathf.Clamp01((ProjectProgress(input.Vertices[vertex], column.RestPolyline) - progressStart) / progressSpan);
            if (!Finite(progress)) continue;
            int bin = Mathf.Clamp(Mathf.RoundToInt(progress * (bins - 1)), 0, bins - 1);
            sums[bin] += input.Vertices[vertex] * weight;
            totals[bin] += weight;
        }
        Vector3[] points = new Vector3[bins];
        var occupied = Enumerable.Range(0, bins).Where(i => totals[i] > WeightEpsilon).ToList();
        if (occupied.Count == 0) return new List<Vector3> { input.Vertices[endpoints.Item1], input.Vertices[endpoints.Item2] };
        foreach (int i in occupied) points[i] = sums[i] / totals[i];
        for (int i = 0; i < bins; i++)
        {
            if (totals[i] > WeightEpsilon) continue;
            int left = occupied.Where(index => index < i).DefaultIfEmpty(-1).Last();
            int right = occupied.Where(index => index > i).DefaultIfEmpty(-1).First();
            points[i] = left < 0 ? points[right] : right < 0 ? points[left] :
                Vector3.Lerp(points[left], points[right], (i - left) / (float)(right - left));
        }
        // 两遍轻平滑保留端点，中心线仍来自真实加权表面截面。
        for (int pass = 0; pass < 2; pass++)
        {
            Vector3[] next = (Vector3[])points.Clone();
            for (int i = 1; i + 1 < bins; i++) next[i] = Vector3.Lerp(points[i], (points[i - 1] + points[i + 1]) * 0.5f, 0.5f);
            points = next;
        }
        Vector3 rootAxis = SamplePolylineExtended(column.RestPolyline, progressStart);
        Vector3 hemAxis = SamplePolylineExtended(column.RestPolyline, progressEnd);
        Vector3 rootTangent = (column.RestPolyline[1] - column.RestPolyline[0]).normalized;
        Vector3 hemTangent = (column.RestPolyline[column.RestPolyline.Count - 1] -
            column.RestPolyline[column.RestPolyline.Count - 2]).normalized;
        if (rootTangent.sqrMagnitude > 1e-8f) points[0] -= rootTangent * Vector3.Dot(points[0] - rootAxis, rootTangent);
        if (hemTangent.sqrMagnitude > 1e-8f) points[bins - 1] -= hemTangent * Vector3.Dot(points[bins - 1] - hemAxis, hemTangent);
        return points.ToList();
    }

    private static Vector3 SamplePolylineExtended(List<Vector3> path, float progress)
    {
        float length = PolylineLength(path);
        if (progress < 0) return path[0] + (path[0] - path[1]).normalized * (length * -progress);
        if (progress > 1) return path[path.Count - 1] +
            (path[path.Count - 1] - path[path.Count - 2]).normalized * (length * (progress - 1));
        return PointAlongPath(path, length * progress);
    }

    private static int ReachableCount(Dictionary<int, HashSet<int>> graph, int start)
    {
        if (!graph.ContainsKey(start)) return 0;
        var reached = new HashSet<int>(); var queue = new Queue<int>(); queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            if (!reached.Add(at)) continue;
            foreach (int next in graph[at]) queue.Enqueue(next);
        }
        return reached.Count;
    }

    private static HashSet<int> GetBoundaryVertices(PMXSkirtTopologyInput input, List<Tri> componentTriangles, List<Tri> allTriangles,
        Dictionary<Edge, List<int>> uses)
    {
        int componentId = componentTriangles[0].Component;
        var boundary = new HashSet<int>();
        foreach (var pair in uses)
            if (pair.Value.Count(t => allTriangles[t].Component == componentId) == 1 && !IsSeamEdge(input, pair.Key))
            { boundary.Add(pair.Key.A); boundary.Add(pair.Key.B); }
        return boundary;
    }

    private static Tuple<int, int> FindEndpoints(PMXSkirtTopologyInput input, PMXSkirtColumnInput column,
        Dictionary<int, HashSet<int>> graph, HashSet<int> boundaryVertices)
    {
        if (column.RestPolyline == null || column.RestPolyline.Count < 2) return Tuple.Create(-1, -1);
        var candidates = graph.Keys.Where(v => GetWeight(input.VertexColumnWeights[v], column.ColumnId) > WeightEpsilon).ToList();
        if (candidates.Count < 2) return Tuple.Create(-1, -1);
        var progress = candidates.ToDictionary(v => v, v => ProjectProgress(input.Vertices[v], column.RestPolyline));
        var boundary = candidates.Where(boundaryVertices.Contains).ToList();
        if (boundary.Count == 0) boundary = candidates;
        var visited = new HashSet<int>();
        int bestRoot = -1, bestHem = -1; float bestSpan = float.NegativeInfinity;
        foreach (int seed in candidates)
        {
            if (!visited.Add(seed)) continue;
            var component = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue(); component.Add(at);
                foreach (int next in graph[at]) if (visited.Add(next)) queue.Enqueue(next);
            }
            int root = component.Where(candidates.Contains).OrderBy(v => progress[v])
                .ThenBy(v => Vector3.Distance(input.Vertices[v], column.RestPolyline[0])).ThenBy(v => v).First();
            int hem = component.Where(boundary.Contains).OrderByDescending(v => progress[v])
                .ThenBy(v => Vector3.Distance(input.Vertices[v], column.RestPolyline[column.RestPolyline.Count - 1])).ThenBy(v => v)
                .DefaultIfEmpty(-1).First();
            float span = hem < 0 ? float.NegativeInfinity : progress[hem] - progress[root];
            if (span > bestSpan) { bestSpan = span; bestRoot = root; bestHem = hem; }
        }
        return Tuple.Create(bestRoot, bestHem);
    }

    private static List<int> ShortestPath(List<Vector3> positions, Dictionary<int, HashSet<int>> graph, int start, int end)
    {
        var dist = graph.Keys.ToDictionary(v => v, v => float.PositiveInfinity);
        var prev = new Dictionary<int, int>(); var heap = new MinHeap(); dist[start] = 0; heap.Push(0, start);
        while (heap.Count > 0)
        {
            var item = heap.Pop(); int v = item.Value;
            if (item.Key > dist[v]) continue;
            if (v == end) break;
            foreach (int n in graph[v])
            {
                float d = dist[v] + Vector3.Distance(positions[v], positions[n]);
                if (d >= dist[n]) continue;
                dist[n] = d; prev[n] = v; heap.Push(d, n);
            }
        }
        if (!Finite(dist[end])) return new List<int>();
        var path = new List<int> { end };
        while (path[path.Count - 1] != start)
        { if (!prev.TryGetValue(path[path.Count - 1], out int p)) return new List<int>(); path.Add(p); }
        path.Reverse();
        // 同源拆点接缝可在图中零距离跨接，布局曲线合并重合点避免零长骨段。
        return path.Where((vertex, index) => index == 0 ||
            (positions[vertex] - positions[path[index - 1]]).sqrMagnitude > 1e-12f).ToList();
    }

    private static float LayoutLength(PMXSkirtTopologyInput input, float realLength, float target)
    {
        float requested = Mathf.Max(0, target - realLength);
        return realLength + Mathf.Min(requested, realLength * input.MaximumVirtualLengthRatio);
    }

    private static int EstimateSegmentCount(PMXSkirtTopologyInput input, List<Vector3> path, float layoutLength, float spacing)
    {
        int count = Mathf.Max(1, Mathf.CeilToInt(layoutLength / Mathf.Max(spacing, 0.0001f)));
        while (count < input.MaximumSegments && LayoutFitError(input, path, layoutLength, count, spacing) > input.LengthTolerance)
            count++;
        return Mathf.Clamp(count, 1, input.MaximumSegments);
    }

    private static bool HasTailTangent(List<Vector3> path)
    {
        for (int i = path.Count - 1; i > 0; i--)
            if ((path[i] - path[i - 1]).sqrMagnitude > 1e-12f) return true;
        return false;
    }

    private static float LayoutFitError(PMXSkirtTopologyInput input, List<Vector3> path, float layoutLength, int count, float spacing)
    {
        float chord = MaximumChordError(path, layoutLength, Mathf.Max(1, count));
        float segment = layoutLength / Mathf.Max(1, count);
        return Mathf.Max(chord, Mathf.Max(0f, segment - spacing));
    }

    private static void BuildLayout(PMXSkirtTopologyInput input, PMXSkirtColumnResult result,
        List<Vector3> path, float realLength, float target, int forcedCount, float spacing)
    {
        float requestedVirtual = Mathf.Max(0, target - realLength);
        float virtualLength = Mathf.Min(requestedVirtual, realLength * input.MaximumVirtualLengthRatio);
        result.RequestedVirtualLength = requestedVirtual;
        Vector3 tangent = Vector3.zero;
        for (int i = path.Count - 1; i > 0 && tangent.sqrMagnitude < 1e-12f; i--)
            tangent = path[i] - path[i - 1];
        tangent = tangent.sqrMagnitude > 1e-12f ? tangent.normalized : Vector3.zero;
        if (requestedVirtual > virtualLength + input.LengthTolerance)
        {
            result.IsIncomplete = true; result.BudgetReason = "虚拟延长比例超过预算，仅生成预算内长度。";
        }
        if (virtualLength > input.LengthTolerance && tangent == Vector3.zero)
        {
            virtualLength = 0;
            result.IsIncomplete = true;
            result.BudgetReason = AppendReason(result.BudgetReason, "末段方向无效，不能虚拟延长。");
        }
        result.VirtualCoverageLength = virtualLength;
        float layoutLength = realLength + virtualLength;
        int count = forcedCount > 0 ? forcedCount : EstimateSegmentCount(input, path, layoutLength, spacing);
        count = Mathf.Clamp(count, 1, input.MaximumSegments);
        float fitError = LayoutFitError(input, path, layoutLength, count, spacing);
        if (fitError > input.LengthTolerance)
        {
            result.IsIncomplete = true;
            result.BudgetReason = AppendReason(result.BudgetReason, "最大弦偏差或所需分段数超过预算。");
        }
        result.MaximumChordError = fitError;
        result.TargetSegmentLength = spacing;
        result.AchievedLength = layoutLength;
        for (int i = 0; i < count; i++)
        {
            float a = layoutLength * i / count, b = layoutLength * (i + 1) / count;
            result.Segments.Add(new PMXSkirtSegmentLayout
            {
                RowIndex = i, Start = PointAlongPath(path, a), End = PointAlongPath(path, b),
                IsVirtual = b > realLength + input.LengthTolerance
            });
        }
        if (result.AchievedLength + input.LengthTolerance < target) result.IsIncomplete = true;
    }

    private static float MaximumChordError(List<Vector3> path, float length, int segments)
    {
        if (path.Count < 2 || segments < 1) return 0;
        var cumulative = new float[path.Count];
        for (int i = 1; i < path.Count; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(path[i - 1], path[i]);
        float maximum = 0;
        for (int row = 0; row < segments; row++)
        {
            float a = length * row / segments, b = length * (row + 1) / segments;
            Vector3 start = PointAlongPath(path, a), end = PointAlongPath(path, b), chord = end - start;
            float denom = chord.sqrMagnitude;
            for (int i = 1; i + 1 < path.Count; i++)
            {
                float d = cumulative[i];
                if (d <= a || d >= b) continue;
                float t = denom > 1e-12f ? Mathf.Clamp01(Vector3.Dot(path[i] - start, chord) / denom) : 0;
                maximum = Mathf.Max(maximum, Vector3.Distance(path[i], start + chord * t));
            }
        }
        return maximum;
    }

    private static Vector3 PointAlongPath(List<Vector3> path, float distance)
    {
        float remain = Mathf.Max(0, distance);
        for (int i = 1; i < path.Count; i++)
        {
            float len = Vector3.Distance(path[i - 1], path[i]);
            if (remain <= len) return len > 1e-8f ? Vector3.Lerp(path[i - 1], path[i], Mathf.Clamp01(remain / len)) : path[i];
            remain -= len;
        }
        Vector3 tangent = path.Count > 1 ? (path[path.Count - 1] - path[path.Count - 2]).normalized : Vector3.zero;
        return path[path.Count - 1] + tangent * remain;
    }

    private static float ProjectProgress(Vector3 point, List<Vector3> polyline)
    {
        float total = PolylineLength(polyline), walked = 0, bestD = float.PositiveInfinity, best = 0;
        for (int i = 1; i < polyline.Count; i++)
        {
            Vector3 a = polyline[i - 1], d = polyline[i] - a; float len = d.magnitude;
            if (len <= 1e-8f) continue;
            float t = Vector3.Dot(point - a, d) / (len * len);
            if (i > 1) t = Mathf.Max(0, t);
            if (i < polyline.Count - 1) t = Mathf.Min(1, t);
            float dist = (point - (a + d * t)).sqrMagnitude;
            if (dist < bestD) { bestD = dist; best = walked + len * t; }
            walked += len;
        }
        return total > 1e-8f ? best / total : float.NaN;
    }

    private static float PolylineLength(List<Vector3> points)
    {
        float sum = 0;
        if (points == null) return 0;
        for (int i = 1; i < points.Count; i++) sum += Vector3.Distance(points[i - 1], points[i]);
        return sum;
    }

    private static float PathLength(List<Vector3> points, List<int> path)
    { float sum = 0; for (int i = 1; i < path.Count; i++) sum += Vector3.Distance(points[path[i - 1]], points[path[i]]); return sum; }
    private static float GetWeight(PMXSkirtVertexWeight w, string id)
    { return w.ColumnWeights.Where(x => string.Equals(x.ColumnId, id, StringComparison.Ordinal)).Sum(x => x.Weight); }
    private static bool HasSkirtWeight(PMXSkirtVertexWeight w) { return w.ColumnWeights.Any(x => x.Weight > WeightEpsilon); }
    private static string DominantColumn(PMXSkirtVertexWeight w)
    { return w.ColumnWeights.OrderByDescending(x => x.Weight).FirstOrDefault(x => x.Weight > WeightEpsilon)?.ColumnId; }
    private static void AddUse(Dictionary<Edge, List<int>> uses, Edge edge, int tri)
    { if (!uses.TryGetValue(edge, out var list)) uses[edge] = list = new List<int>(); list.Add(tri); }
    private static void AddGraphEdge(Dictionary<int, HashSet<int>> graph, int a, int b)
    {
        if (!graph.TryGetValue(a, out var aa)) graph[a] = aa = new HashSet<int>();
        if (!graph.TryGetValue(b, out var bb)) graph[b] = bb = new HashSet<int>();
        aa.Add(b); bb.Add(a);
    }
    private static string TriangleKey(int a, int b, int c)
    { int[] v = { a, b, c }; Array.Sort(v); return v[0] + ":" + v[1] + ":" + v[2]; }
    private static int CountDuplicateTriangles(PMXSkirtTopologyInput input, List<int> sourceTriangles)
    {
        var keys = new HashSet<string>();
        foreach (int t in sourceTriangles)
        { int i = t * 3; keys.Add(TriangleKey(input.TriangleIndices[i], input.TriangleIndices[i + 1], input.TriangleIndices[i + 2])); }
        var seen = new HashSet<string>(); int count = 0;
        for (int i = 0; i < input.TriangleIndices.Count; i += 3)
        { string key = TriangleKey(input.TriangleIndices[i], input.TriangleIndices[i + 1], input.TriangleIndices[i + 2]); if (keys.Contains(key) && !seen.Add(key)) count++; }
        return count;
    }
    private static int CountDegenerateTriangles(PMXSkirtTopologyInput input, List<int> sourceTriangles)
    {
        var usedVertices = new HashSet<int>();
        foreach (int t in sourceTriangles)
        { int i = t * 3; usedVertices.Add(input.TriangleIndices[i]); usedVertices.Add(input.TriangleIndices[i + 1]); usedVertices.Add(input.TriangleIndices[i + 2]); }
        int count = 0;
        for (int i = 0; i < input.TriangleIndices.Count; i += 3)
        {
            int a = input.TriangleIndices[i], b = input.TriangleIndices[i + 1], c = input.TriangleIndices[i + 2];
            int[] unique = { a, b, c };
            if ((a == b || b == c || c == a) && unique.Distinct().Count(v => HasSkirtWeight(input.VertexColumnWeights[v])) >= 2 &&
                unique.Any(usedVertices.Contains)) count++;
        }
        return count;
    }
    private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    private static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
    private static string AppendReason(string a, string b) { return string.IsNullOrEmpty(a) ? b : a + " " + b; }
}
