using LibMMD.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static LibMMD.Model.SkinningOperator;

/// <summary>原裙骨直线二分两次；替换原骨，不移动端点或增加裙列。</summary>
internal static class PMXSkirtLayoutExporter
{
    internal sealed class Segment
    {
        internal int Row, BoneIndex, SourceBoneIndex;
        internal Vector3 Start, End, OriginalStart, OriginalEnd;
        internal float StartDistance;
        internal bool IsVirtual = false;
        internal string Name, SourceBoneName;
    }

    internal sealed class Column
    {
        internal PMXPhysicsExporter.SkirtColumn Source;
        internal string ColumnId;
        internal bool IsCircumferential = false;
        internal Vector3 AnchorPosition;
        internal int AnchorBoneIndex;
        internal float AnchorRadius;
        internal readonly List<string> NeighborColumnIds = new List<string>();
        internal int ComponentId, RootBoneIndex;
        internal bool RegionClosed = false;
        internal float RealLength, AchievedLength;
        internal float TargetSegmentLength;
        internal float MaximumFitError = 0;
        internal readonly List<Segment> Segments = new List<Segment>();
        internal readonly HashSet<int> SourceBoneIndexes = new HashSet<int>();
    }

    internal sealed class Result
    {
        internal readonly List<Column> Columns = new List<Column>();
        internal readonly List<string> ReplacedBoneNames = new List<string>();
        internal int CompressedVertexCount;
        internal float MaximumMergedSkirtWeight, TotalMergedSkirtWeight;
    }

    private sealed class Source
    {
        internal string Id;
        internal PMXPhysicsExporter.SkirtColumn Column;
        internal int RootBoneIndex, AnchorBoneIndex;
        internal Vector3 AnchorPosition;
        internal float AnchorRadius;
        internal readonly List<Vector3> RestPolyline = new List<Vector3>();
        internal readonly List<int> BoneIndexes = new List<int>();
        internal readonly List<Transform> Bones = new List<Transform>();
    }

    private sealed class CircumferentialEvidence
    {
        internal readonly HashSet<long> Edges = new HashSet<long>();
        internal readonly List<float> Progress = new List<float>();
    }

    internal static Result TryBuild(PMXPhysicsExporter.Context context, Transform coordinateRoot,
        PMXBoneExporter.Result boneResult, RawMMDModel model)
    {
        if (context?.Mesh == null || coordinateRoot == null || boneResult == null ||
            model?.Vertices == null || context.SkirtColumns.Count < 3) return null;
        var input = BuildInput(context, coordinateRoot, boneResult, model, out var sources, out string error);
        if (input == null)
        { Debug.LogWarning("PMX 裙摆四分回退：" + error); return null; }
        var original = boneResult.Bones;
        var removed = new HashSet<int>();
        foreach (Source source in sources)
            foreach (int index in source.BoneIndexes.Take(source.BoneIndexes.Count - 1))
                if (!removed.Add(index))
                { Debug.LogWarning("PMX 裙摆四分回退：不同裙列共享原骨。"); return null; }
        var all = new List<Bone>(original);
        var result = new Result();
        var splitBySource = new Dictionary<int, List<Segment>>();
        var names = new HashSet<string>(original.Select(b => b.Name), StringComparer.Ordinal);
        foreach (Source source in sources)
        {
            var column = new Column { Source = source.Column, ColumnId = source.Id,
                AnchorPosition = source.AnchorPosition, AnchorBoneIndex = source.AnchorBoneIndex,
                AnchorRadius = source.AnchorRadius, RootBoneIndex = source.RootBoneIndex };
            float walked = 0;
            for (int edge = 0; edge + 1 < source.BoneIndexes.Count; edge++)
            {
                int oldIndex = source.BoneIndexes[edge];
                Bone oldBone = original[oldIndex];
                Vector3 start = oldBone.Position, end = source.RestPolyline[edge + 1];
                float length = Vector3.Distance(start, end);
                var pieces = new List<Segment>();
                column.SourceBoneIndexes.Add(oldIndex);
                result.ReplacedBoneNames.Add(oldBone.Name);
                for (int quarter = 0; quarter < 4; quarter++)
                {
                    Vector3 a = Vector3.Lerp(start, end, quarter / 4f);
                    Vector3 b = Vector3.Lerp(start, end, (quarter + 1) / 4f);
                    string name = oldBone.Name + "_seg" + quarter.ToString("00");
                    if (!names.Add(name)) throw new InvalidOperationException("裙分段骨重名：" + name);
                    int temporary = all.Count;
                    Bone bone = CopySegmentBone(oldBone, name, a);
                    // 首段承接原父骨，其余三段串联；最后尾部仍指原端点。
                    bone.ParentIndex = quarter == 0 ? oldBone.ParentIndex : temporary - 1;
                    bone.ChildBoneVal = new Bone.ChildBone { ChildUseId = true,
                        Index = quarter < 3 ? temporary + 1 : source.BoneIndexes[edge + 1] };
                    if (quarter > 0) { bone.AppendRotate = false; bone.AppendTranslate = false; }
                    all.Add(bone);
                    var piece = new Segment { Row = column.Segments.Count, BoneIndex = temporary,
                        SourceBoneIndex = oldIndex, SourceBoneName = oldBone.Name,
                        OriginalStart = start, OriginalEnd = end, Start = a, End = b,
                        StartDistance = walked + length * quarter / 4f, Name = name };
                    column.Segments.Add(piece); pieces.Add(piece);
                }
                splitBySource.Add(oldIndex, pieces);
                walked += length;
            }
            column.RealLength = column.AchievedLength = walked;
            column.TargetSegmentLength = column.Segments.Max(s => Vector3.Distance(s.Start, s.End));
            result.Columns.Add(column);
        }
        BuildNeighbors(input, sources, result.Columns);
        RewriteWeights(model.Vertices, splitBySource, result);
        // 同一映射覆盖被替换骨、保留骨及新增骨的临时索引。
        var first = Enumerable.Repeat(-1, all.Count).ToArray();
        var last = Enumerable.Repeat(-1, all.Count).ToArray();
        var output = new List<Bone>();
        // 原位替换，保证同层级的父骨先于末端求值。
        for (int i = 0; i < original.Length; i++)
        {
            if (!splitBySource.TryGetValue(i, out var pieces))
            { first[i] = last[i] = output.Count; output.Add(original[i]); continue; }
            first[i] = output.Count;
            foreach (Segment piece in pieces)
            {
                first[piece.BoneIndex] = last[piece.BoneIndex] = output.Count;
                output.Add(all[piece.BoneIndex]);
            }
            last[i] = output.Count - 1;
        }
        foreach (var column in result.Columns)
        {
            column.RootBoneIndex = first[column.RootBoneIndex];
            column.AnchorBoneIndex = first[column.AnchorBoneIndex];
            foreach (var segment in column.Segments)
            {
                Bone bone = all[segment.BoneIndex];
                bone.ParentIndex = bone.ParentIndex < 0 ? -1 : last[bone.ParentIndex];
                bone.ChildBoneVal.Index = first[bone.ChildBoneVal.Index];
                if (bone.AppendRotate || bone.AppendTranslate) bone.AppendBoneVal.Index = first[bone.AppendBoneVal.Index];
                segment.BoneIndex = first[segment.BoneIndex];
            }
        }
        PMXBoneIndexRemapper.Remap(model, boneResult, original, output, first, last);
        return result;
    }

    private static Bone CopySegmentBone(Bone source, string name, Vector3 position) => new Bone
    {
        Name = name, NameEn = name, Position = position, ParentIndex = source.ParentIndex,
        TransformLevel = source.TransformLevel, Rotatable = source.Rotatable, Movable = source.Movable,
        Visible = source.Visible, Controllable = source.Controllable,
        AppendRotate = source.AppendRotate, AppendTranslate = source.AppendTranslate,
        AppendBoneVal = Bone.AppendBone.CopyOf(source.AppendBoneVal), RotAxisFixed = source.RotAxisFixed,
        RotAxis = source.RotAxis, UseLocalAxis = source.UseLocalAxis,
        LocalAxisVal = Bone.LocalAxis.CopyOf(source.LocalAxisVal), PostPhysics = source.PostPhysics,
        ReceiveTransform = source.ReceiveTransform, ExportKey = source.ExportKey
    };

    private static void BuildNeighbors(PMXSkirtTopologyInput input, List<Source> sources, List<Column> columns)
    {
        // 只据原网格证据连接既有邻列，不插新列、不改变骨坐标。
        var byId = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var ranges = BuildColumnProgressRanges(input, sources);
        var edges = new Dictionary<string, CircumferentialEvidence>();
        for (int i = 0; i + 2 < input.TriangleIndices.Count; i += 3)
            for (int j = 0; j < 3; j++)
            {
                int a = input.TriangleIndices[i + j], b = input.TriangleIndices[i + (j + 1) % 3];
                if (!TryGetDominantSource(input.VertexColumnWeights[a], byId, out string left) ||
                    !TryGetDominantSource(input.VertexColumnWeights[b], byId, out string right) || left == right) continue;
                string key = string.CompareOrdinal(left, right) < 0 ? left + "|" + right : right + "|" + left;
                if (!edges.TryGetValue(key, out var evidence)) edges[key] = evidence = new CircumferentialEvidence();
                evidence.Edges.Add(EdgeKey(a, b));
                evidence.Progress.Add((NormalizeSupportProgress(input.Vertices[a], left, byId, ranges) +
                    NormalizeSupportProgress(input.Vertices[b], right, byId, ranges)) * 0.5f);
            }
        var columnById = columns.ToDictionary(c => c.ColumnId);
        foreach (var pair in edges.Where(e => HasContinuousEdgeBand(e.Value)))
        {
            string[] ids = pair.Key.Split('|');
            columnById[ids[0]].NeighborColumnIds.Add(ids[1]); columnById[ids[1]].NeighborColumnIds.Add(ids[0]);
        }
        int component = 0;
        var seen = new HashSet<string>();
        foreach (Column start in columns)
        {
            if (!seen.Add(start.ColumnId)) continue;
            var pending = new Queue<Column>(); pending.Enqueue(start);
            while (pending.Count > 0)
            {
                Column current = pending.Dequeue(); current.ComponentId = component;
                foreach (string id in current.NeighborColumnIds) if (seen.Add(id)) pending.Enqueue(columnById[id]);
            }
            component++;
        }
    }

    private static void RewriteWeights(Vertex[] vertices, Dictionary<int, List<Segment>> splitBySource, Result diagnostics)
    {
        var generated = new HashSet<int>(splitBySource.Values.SelectMany(s => s).Select(s => s.BoneIndex));
        foreach (Vertex vertex in vertices)
        {
            List<Influence> original = Read(vertex.SkinningOperator);
            if (!original.Any(w => w.Weight > 0 && splitBySource.ContainsKey(w.Bone))) continue;
            var next = new List<Influence>();
            foreach (Influence influence in original)
            {
                if (!splitBySource.TryGetValue(influence.Bone, out var segments)) { next.Add(influence); continue; }
                Vector3 axis = segments[3].End - segments[0].Start;
                // 投影只分配原裙权重，不参与骨段坐标或朝向计算。
                float quarter = Mathf.Clamp01(Vector3.Dot(vertex.Coordinate - segments[0].Start, axis) / axis.sqrMagnitude) * 4;
                int index = Mathf.Min(3, Mathf.FloorToInt(quarter));
                float blend = index < 3 ? quarter - index : 0;
                next.Add(new Influence(segments[index].BoneIndex, influence.Weight * (1 - blend)));
                if (blend > 0) next.Add(new Influence(segments[index + 1].BoneIndex, influence.Weight * blend));
            }
            vertex.SkinningOperator = Write(next, original[0].Bone, generated, diagnostics);
        }
    }

    private static PMXSkirtTopologyInput BuildInput(PMXPhysicsExporter.Context context, Transform coordinateRoot,
        PMXBoneExporter.Result boneResult, RawMMDModel model, out List<Source> sources, out string error)
    {
        sources = new List<Source>();
        error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PMXPhysicsExporter.SkirtColumn column in context.SkirtColumns)
        {
            if (column == null || column.Chain == null || column.Chain.Root == null)
            { error = "裙列缺少根骨。"; return null; }
            string id = column.Chain.Root.name;
            if (!seen.Add(id) || !boneResult.BoneIndexes.TryGetValue(column.Chain.Root, out int rootIndex))
            { error = "裙列根骨重名或未导出：" + id; return null; }
            Transform anchor = PMXPhysicsExporter.FindNearestExportedParentTransform(column.Chain.Root.parent, boneResult.BoneIndexes);
            var source = new Source { Id = id, Column = column, RootBoneIndex = rootIndex,
                AnchorBoneIndex = anchor != null ? boneResult.BoneIndexes[anchor] : 0,
                AnchorPosition = coordinateRoot.InverseTransformPoint(column.Chain.Root.position),
                AnchorRadius = column.Chain.Radii.TryGetValue(column.Chain.Root, out float rootRadius) ? rootRadius : 0.01f };
            Transform current = column.Chain.Root;
            while (current != null && column.Chain.Bones.Contains(current))
            {
                if (!boneResult.BoneIndexes.TryGetValue(current, out int index))
                { error = "裙列骨未导出：" + current.name; return null; }
                source.Bones.Add(current);
                source.BoneIndexes.Add(index);
                current = PMXPhysicsExporter.GetSingleChainChild(current, column.Chain.Bones);
            }
            if (source.Bones.Count < 2) { error = "裙列骨链不足：" + id; return null; }
            source.RestPolyline.AddRange(source.Bones.Select(bone => coordinateRoot.InverseTransformPoint(bone.position)));
            for (int i = 0; i + 1 < source.BoneIndexes.Count; i++)
            {
                Bone original = boneResult.Bones[source.BoneIndexes[i]];
                Vector3 end = source.RestPolyline[i + 1];
                Vector3 tail = original.ChildBoneVal.ChildUseId
                    ? boneResult.Bones[original.ChildBoneVal.Index].Position
                    : original.Position + original.ChildBoneVal.Offset;
                float length = Vector3.Distance(original.Position, tail);
                if (original.HasIk || !PMXPhysicsExporter.IsFinite(length) ||
                    length <= PMXPhysicsExporter.MinimumSegmentLength * 4 ||
                    Vector3.Distance(original.Position, source.RestPolyline[i]) > 1e-6f ||
                    Vector3.Distance(tail, end) > 1e-6f)
                { error = "原裙骨端点与配置链不一致或不能四分：" + original.Name; return null; }
            }
            sources.Add(source);
        }

        int vertexCount = context.Mesh.Items.Sum(item => item.Positions == null ? 0 : item.Positions.Length);
        if (vertexCount != model.Vertices.Length) { error = "导出顶点与网格快照数量不一致。"; return null; }
        var input = new PMXSkirtTopologyInput
        {
            Vertices = model.Vertices.Select(vertex => vertex.Coordinate).ToList(),
            VertexNormals = model.Vertices.Select(vertex => vertex.Normal).ToList(),
            TargetSegmentLength = 0.08f
        };
        var columnOfBone = new Dictionary<int, string>();
        foreach (Source source in sources)
        {
            input.Columns.Add(new PMXSkirtColumnInput
            {
                ColumnId = source.Id,
                BonePath = source.Bones.Select(bone => bone.name).ToList(),
                RestPolyline = source.RestPolyline
            });
            foreach (int index in source.BoneIndexes) columnOfBone[index] = source.Id;
        }

        for (int itemIndex = 0; itemIndex < context.Mesh.Items.Count; itemIndex++)
        {
            PMXMeshExportContext.Item item = context.Mesh.Items[itemIndex];
            int offset = item.VertexOffset;
            for (int vertex = 0; vertex < item.Positions.Length; vertex++)
            {
                var weights = new PMXSkirtVertexWeight();
                float skirt = 0;
                if (item.Weights != null && vertex < item.Weights.Length && item.Bones != null)
                {
                    BoneWeight weight = item.Weights[vertex];
                    Add(weights, columnOfBone, boneResult.BoneIndexes, item.Bones, weight.boneIndex0, weight.weight0, ref skirt);
                    Add(weights, columnOfBone, boneResult.BoneIndexes, item.Bones, weight.boneIndex1, weight.weight1, ref skirt);
                    Add(weights, columnOfBone, boneResult.BoneIndexes, item.Bones, weight.boneIndex2, weight.weight2, ref skirt);
                    Add(weights, columnOfBone, boneResult.BoneIndexes, item.Bones, weight.boneIndex3, weight.weight3, ref skirt);
                }
                weights.NonSkirtWeight = Mathf.Max(0, 1f - skirt);
                input.VertexColumnWeights.Add(weights);
                input.VertexGroups.Add(itemIndex);

            }
            foreach (PMXMeshExportContext.Submesh submesh in item.Submeshes)
            {
                for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                    input.TriangleIndices.AddRange(new[]
                    {
                        submesh.Indices[i] + offset, submesh.Indices[i + 1] + offset, submesh.Indices[i + 2] + offset
                    });
            }
        }

        return input;
    }

    private static Dictionary<string, Vector2> BuildColumnProgressRanges(PMXSkirtTopologyInput input, List<Source> sources)
    {
        var ranges = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        foreach (Source source in sources)
        {
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int vertex = 0; vertex < input.VertexColumnWeights.Count; vertex++)
            {
                if (input.VertexColumnWeights[vertex].ColumnWeights
                    .Where(weight => weight.ColumnId == source.Id).Sum(weight => weight.Weight) <= 0.05f) continue;
                float progress = ProjectProgress(input.Vertices[vertex], source.RestPolyline);
                if (float.IsNaN(progress) || float.IsInfinity(progress)) continue;
                min = Mathf.Min(min, progress); max = Mathf.Max(max, progress);
            }
            if (min <= max) ranges[source.Id] = new Vector2(min, max);
        }
        return ranges;
    }

    private static bool TryGetDominantSource(PMXSkirtVertexWeight weights, Dictionary<string, Source> sources, out string id)
    {
        id = null;
        if (weights?.ColumnWeights == null) return false;
        float total = 0, strongest = 0;
        foreach (PMXSkirtColumnWeight weight in weights.ColumnWeights)
        {
            if (!sources.ContainsKey(weight.ColumnId) || weight.Weight <= 0) continue;
            total += weight.Weight;
            if (weight.Weight > strongest) { strongest = weight.Weight; id = weight.ColumnId; }
        }
        return id != null && total >= 0.2f && strongest >= 0.1f && strongest / total >= 0.35f;
    }

    private static float NormalizeSupportProgress(Vector3 vertex, string id, Dictionary<string, Source> sources,
        Dictionary<string, Vector2> support)
    {
        if (!support.TryGetValue(id, out Vector2 range)) return 0.5f;
        float span = range.y - range.x;
        if (span <= 1e-5f) return 0.5f;
        return Mathf.Clamp01((ProjectProgress(vertex, sources[id].RestPolyline) - range.x) / span);
    }

    private static bool HasContinuousEdgeBand(CircumferentialEvidence evidence)
    {
        if (evidence.Edges.Count < 3 || evidence.Progress.Count < 3) return false;
        float min = evidence.Progress.Min(), max = evidence.Progress.Max();
        if (max - min < 0.2f) return false;
        int longest = 0, run = 0, previous = -2;
        foreach (int bin in evidence.Progress.Select(progress => Mathf.Clamp(Mathf.FloorToInt(progress * 6), 0, 5))
            .Distinct().OrderBy(value => value))
        {
            run = bin == previous + 1 ? run + 1 : 1;
            longest = Mathf.Max(longest, run);
            previous = bin;
        }
        return longest >= 3;
    }

    private static long EdgeKey(int a, int b)
    {
        uint low = (uint)Mathf.Min(a, b), high = (uint)Mathf.Max(a, b);
        return ((long)low << 32) | high;
    }

    private static float ProjectProgress(Vector3 point, List<Vector3> polyline)
    {
        float total = 0, walked = 0, bestDistance = float.PositiveInfinity, best = 0;
        for (int i = 1; i < polyline.Count; i++) total += Vector3.Distance(polyline[i - 1], polyline[i]);
        for (int i = 1; i < polyline.Count; i++)
        {
            Vector3 start = polyline[i - 1], delta = polyline[i] - start;
            float length = delta.magnitude;
            if (length <= 1e-6f) continue;
            float t = Vector3.Dot(point - start, delta) / (length * length);
            if (i > 1) t = Mathf.Max(0, t);
            if (i < polyline.Count - 1) t = Mathf.Min(1, t);
            float distance = (point - (start + delta * t)).sqrMagnitude;
            if (distance < bestDistance) { bestDistance = distance; best = walked + length * t; }
            walked += length;
        }
        return total > 1e-6f ? best / total : float.NaN;
    }

    private static void Add(PMXSkirtVertexWeight target, Dictionary<int, string> columns,
        Dictionary<Transform, int> exported, Transform[] bones, int rendererIndex, float weight, ref float skirt)
    {
        if (weight <= 0 || rendererIndex < 0 || rendererIndex >= bones.Length || bones[rendererIndex] == null ||
            !exported.TryGetValue(bones[rendererIndex], out int exportedIndex) ||
            !columns.TryGetValue(exportedIndex, out string column))
            return;
        PMXSkirtColumnWeight existing = target.ColumnWeights.FirstOrDefault(item => item.ColumnId == column);
        if (existing == null) target.ColumnWeights.Add(new PMXSkirtColumnWeight { ColumnId = column, Weight = weight });
        else existing.Weight += weight;
        skirt += weight;
    }

    private struct Influence
    {
        internal int Bone;
        internal float Weight;
        internal Influence(int bone, float weight) { Bone = bone; Weight = weight; }
    }

    private static List<Influence> Read(SkinningOperator skinning)
    {
        var result = new List<Influence>();
        if (skinning.Param is Bdef1 one) result.Add(new Influence(one.BoneId, 1));
        else if (skinning.Param is Bdef2 two)
        {
            result.Add(new Influence(two.BoneId[0], two.BoneWeight));
            result.Add(new Influence(two.BoneId[1], 1f - two.BoneWeight));
        }
        else if (skinning.Param is Bdef4 four)
            for (int i = 0; i < 4; i++) if (four.BoneWeight[i] > 0)
                result.Add(new Influence(four.BoneId[i], four.BoneWeight[i]));
        if (skinning.Param is Sdef sdef)
        {
            result.Add(new Influence(sdef.BoneId[0], sdef.BoneWeight));
            result.Add(new Influence(sdef.BoneId[1], 1f - sdef.BoneWeight));
        }
        return result;
    }

    private static SkinningOperator Write(List<Influence> influences, int fallback, HashSet<int> generated, Result diagnostics)
    {
        var merged = new Dictionary<int, float>();
        foreach (Influence influence in influences)
            if (influence.Weight > 0f)
                merged[influence.Bone] = merged.TryGetValue(influence.Bone, out float old) ? old + influence.Weight : influence.Weight;
        // 先保留身体份额，再合并超出上限的裙段份额。
        List<KeyValuePair<int, float>> fixedInfluences = merged.Where(item => !generated.Contains(item.Key))
            .OrderByDescending(item => item.Value).ToList();
        List<KeyValuePair<int, float>> skirtInfluences = merged.Where(item => generated.Contains(item.Key))
            .OrderByDescending(item => item.Value).ToList();
        int slots = Math.Max(0, 4 - fixedInfluences.Count);
        var active = new List<KeyValuePair<int, float>>(fixedInfluences.Take(4));
        var keptSkirt = skirtInfluences.Take(slots).ToList();
        float mergedSkirt = skirtInfluences.Skip(keptSkirt.Count).Sum(item => item.Value);
        if (mergedSkirt > 0)
        {
            diagnostics.CompressedVertexCount++;
            diagnostics.TotalMergedSkirtWeight += mergedSkirt;
            diagnostics.MaximumMergedSkirtWeight = Mathf.Max(diagnostics.MaximumMergedSkirtWeight, mergedSkirt);
        }
        if (keptSkirt.Count > 0 && mergedSkirt > 0)
            keptSkirt[0] = new KeyValuePair<int, float>(keptSkirt[0].Key,
                keptSkirt[0].Value + mergedSkirt);
        active.AddRange(keptSkirt);
        if (active.Count == 0)
            active.Add(new KeyValuePair<int, float>(fallback, 1f));
        float sum = active.Sum(item => item.Value);
        if (active.Count == 0 || sum <= 0)
            return new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = fallback } };
        if (active.Count == 1)
            return new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = active[0].Key } };
        if (active.Count == 2)
            return new SkinningOperator
            {
                Type = SkinningType.SkinningBdef2,
                Param = new Bdef2 { BoneId = new[] { active[0].Key, active[1].Key }, BoneWeight = active[0].Value / sum }
            };
        var ids = new int[4];
        var weights = new float[4];
        for (int i = 0; i < active.Count; i++) { ids[i] = active[i].Key; weights[i] = active[i].Value / sum; }
        for (int i = active.Count; i < 4; i++) ids[i] = fallback;
        return new SkinningOperator { Type = SkinningType.SkinningBdef4, Param = new Bdef4 { BoneId = ids, BoneWeight = weights } };
    }
}
