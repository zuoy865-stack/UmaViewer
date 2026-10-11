using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Gallop;
using LibMMD.Model;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using static LibMMD.Model.SkinningOperator;

/// <summary>验证实际裙骨、权重及纵横刚体网络，不代替 Bullet 动态验收。</summary>
public static class PMXSkirtProductionRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Runtime = typeof(ModelExporter).Assembly;
    private static Type Physics => Runtime.GetType("PMXPhysicsExporter", true);
    private static Type Layout => Runtime.GetType("PMXSkirtLayoutExporter", true);

    public static void RunBatch()
    {
        var cases = new JArray();
        Run(cases, "实际布局加密、蒙皮与锚点连接", () => Check(0.3f, false, false));
        Run(cases, "不等长裙列保持原骨段", () => Check(0.8f, true, false));
        Run(cases, "独立裙层不串接", () => Check(0.3f, false, true));
        Run(cases, "斜折多段链四分与引用重映射", CheckCurvedChain);
        Run(cases, "真实冻结 Teio 原位四段替换", PMXSkirtRealMeshRegression.RunFixture);
        Run(cases, "纯拓扑边界与曲率", PMXSkirtTopologyRegression.RunFixtures);
        Run(cases, "腿壳半径按网格与根变换拟合", CheckColliderFit);
        Run(cases, "附件参数、权重与断链", PMXAttachmentChainRegression.RunFixtures);
        Run(cases, "既有眼部与物理掩码", CheckLegacy);
        bool passed = cases.OfType<JObject>().All(c => (string)c["status"] == "passed");
        string output = Argument("-pmxStage2Output", "Logs/teio-export-stage2-20261005");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "production-fixtures.json"), new JObject
        {
            ["status"] = passed ? "passed" : "failed", ["cases"] = cases,
            ["runtimeMvid"] = Runtime.ManifestModule.ModuleVersionId.ToString(),
            ["targetBulletVisualValidation"] = "not-run"
        }.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private sealed class Fixture : IDisposable
    {
        internal GameObject Root = new GameObject("Skirt production fixture");
        internal object Context, BoneResult, LayoutResult;
        internal RawMMDModel Model;
        internal Vector3[] Before;
        internal int SourceColumns;
        internal PMXMeshExportContext Mesh;
        internal readonly List<MMDRigidBody> Bodies = new List<MMDRigidBody>();
        internal readonly List<MMDJoint> Joints = new List<MMDJoint>();
        internal readonly List<Dictionary<string, float>> OriginalWeights = new List<Dictionary<string, float>>();
        public void Dispose() { Mesh?.Dispose(); UnityEngine.Object.DestroyImmediate(Root); }
    }

    private static void Check(float length, bool asymmetric, bool layers, bool curved = false)
    {
        using (Fixture f = Make(length, asymmetric, layers, curved))
        {
            f.LayoutResult = Layout.GetMethod("TryBuild", Methods).Invoke(null,
                new[] { f.Context, f.Root.transform, f.BoneResult, f.Model });
            Require(f.LayoutResult != null, "有效裙面没有进入生产增强布局。");
            var columns = (IList)Get(f.LayoutResult, "Columns");
            Require(columns.Count == f.SourceColumns, "布局改变了原周向列数。");
            Require(((IList)Get(f.LayoutResult, "ReplacedBoneNames")).Count > 0, "布局没有替换原骨段。");
            Require(f.Before.SequenceEqual(f.Model.Vertices.Select(v => v.Coordinate)), "布局改变了可见顶点。");
            CheckOrderedRestSkinning(f.Model);
            var finalIndexes = (Dictionary<Transform, int>)f.BoneResult.GetType().GetProperty("BoneIndexes").GetValue(f.BoneResult);
            for (int col = 0; col < columns.Count; col++)
            {
                object column = columns[col];
                var segments = ((IList)Get(column, "Segments")).Cast<object>().ToArray();
                var groups = segments.GroupBy(s => (string)Get(s, "SourceBoneName")).ToArray();
                Require(groups.Length == (curved ? 2 : 1) && groups.All(g => g.Count() == 4),
                    "每条原物理骨段必须恰好拆成四段。");
                Require(segments.All(s => !(bool)Get(s, "IsVirtual")), "布局生成了虚拟段。");
                foreach (var group in groups)
                {
                    object[] four = group.ToArray();
                    string sourceName = group.Key;
                    Require(!f.Model.Bones.Any(b => b.Name == sourceName), "被替换的原物理骨仍留在最终数组。");
                    var sourceColumn = ((IList)Get(f.Context, "SkirtColumns")).Cast<object>()
                        .First(c => ((Transform)Get(Get(c, "Chain"), "Root")).name == (string)Get(segments[0], "SourceBoneName"));
                    Transform sourceTransform = FindChild(sourceColumn, sourceName);
                    Transform endpoint = sourceTransform.GetChild(0);
                    Vector3 start = (Vector3)Get(four[0], "OriginalStart"), end = (Vector3)Get(four[0], "OriginalEnd");
                    Require(Vector3.Distance(start, sourceTransform.position) < 1e-5f &&
                        Vector3.Distance(end, endpoint.position) < 1e-5f, "原始区间未使用真实骨 start 与可见 childend。");
                    for (int i = 0; i < 4; i++)
                    {
                        object segment = four[i];
                        Vector3 expectedStart = Vector3.Lerp(start, end, i / 4f);
                        Vector3 expectedEnd = Vector3.Lerp(start, end, (i + 1) / 4f);
                        Require(Vector3.Distance((Vector3)Get(segment, "Start"), expectedStart) < 1e-5f &&
                            Vector3.Distance((Vector3)Get(segment, "End"), expectedEnd) < 1e-5f,
                            "新段没有沿原骨 start→childend 等分，或改变了端点方向。");
                        int index = (int)Get(segment, "BoneIndex");
                        Require(index >= 0 && index < f.Model.Bones.Length &&
                            f.Model.Bones[index].Name == (string)Get(segment, "Name"),
                            "段骨索引不是最终骨架索引。");
                        if (i == 0) Require(finalIndexes[sourceTransform] == index,
                            "源 Transform 未映射到替换首段。");
                        Bone generated = f.Model.Bones[index];
                        Require(generated.UseLocalAxis && generated.LocalAxisVal.AxisX == Vector3.right &&
                            generated.LocalAxisVal.AxisZ == Vector3.forward, "新段未复制原骨局部轴。");
                    }
                }
                var sourceColumnForMarker = ((IList)Get(f.Context, "SkirtColumns")).Cast<object>()
                    .First(c => ((Transform)Get(Get(c, "Chain"), "Root")).name == (string)Get(segments[0], "SourceBoneName"));
                Transform markerTransform = curved
                    ? ((Transform)Get(Get(sourceColumnForMarker, "Chain"), "Root")).GetChild(0).GetChild(0)
                    : ((Transform)Get(Get(sourceColumnForMarker, "Chain"), "Root")).GetChild(0);
                int marker = Array.FindIndex(f.Model.Bones, b => b.Name == markerTransform.name);
                Require(marker >= 0 && f.Model.Bones[marker].ParentIndex == (int)Get(segments[segments.Length - 1], "BoneIndex"),
                    "末端 marker 未保留或未接到第四段。");
                Require(((HashSet<int>)Get(column, "SourceBoneIndexes")).Count == groups.Length,
                    "原物理骨索引集合包含终端 marker 或漏掉被替换骨。");
            }
            for (int i = 0; i < f.Model.Vertices.Length; i++)
            {
                var weights = Read(f.Model.Vertices[i].SkinningOperator);
                Require(System.Math.Abs(weights.Sum(w => w.Value) - 1f) < 1e-6f, "权重未归一。");
                Require(weights.Count <= 4, "超过四影响上限。");
                Require(weights.All(w => w.Key >= 0 && w.Key < f.Model.Bones.Length), "蒙皮引用越界骨索引。");
                float expected = i % 5 == 0 ? 1f : 0.35f;
                Require(System.Math.Abs(weights.Where(w => f.Model.Bones[w.Key].Name == "body").Sum(w => w.Value) - expected) < 1e-6f,
                    "固定腰口或非裙身体权重被改变。");
            }
            Runtime.GetType("PMXSkirtPhysicsExporter", true).GetMethod("BuildSkirtPhysics", Methods).Invoke(null,
                new object[] { f.Context, f.Root.transform, f.BoneResult, f.Bodies, f.Joints,
                    new Dictionary<Transform, int>(), f.LayoutResult });
            var dynamic = f.Bodies.Where(b => b.Type != MMDRigidBody.RigidBodyType.RigidTypeKinematic).ToList();
            Require(dynamic.Count == columns.Cast<object>().Sum(c => ((IList)Get(c, "Segments")).Count),
                "每个分段必须恰好有一个动态刚体。");
            Require(dynamic.Select(b => b.AssociatedBoneIndex).Distinct().Count() == dynamic.Count, "多个刚体争写同一骨。");
            Require(dynamic.All(b => b.Mass > 0 && (b.CollisionMask & (1 << b.CollisionGroup)) == 0),
                "增强裙体质量或禁自碰策略错误。");
            Require(f.Joints.Any(j => j.Name.Contains("horizontal")), "缺少横向约束。");
            Require(f.Joints.Select(j => string.Join(":", j.AssociatedRigidBodyIndex.OrderBy(v => v))).Distinct().Count() == f.Joints.Count,
                "重复刚体对关节。");
            var reached = new HashSet<int>(Enumerable.Range(0, f.Bodies.Count).Where(i =>
                f.Bodies[i].Type == MMDRigidBody.RigidBodyType.RigidTypeKinematic));
            bool changed;
            do { changed = false; foreach (var j in f.Joints) if (j.AssociatedRigidBodyIndex.Any(reached.Contains))
                foreach (int i in j.AssociatedRigidBodyIndex) changed |= reached.Add(i); } while (changed);
            Require(reached.Count == f.Bodies.Count, "存在无法到达跟随锚点的刚体。");
            if (layers)
                foreach (var joint in f.Joints.Where(j => j.Name.Contains("horizontal")))
                {
                    string a = f.Model.Bones[f.Bodies[joint.AssociatedRigidBodyIndex[0]].AssociatedBoneIndex].Name;
                    string b = f.Model.Bones[f.Bodies[joint.AssociatedRigidBodyIndex[1]].AssociatedBoneIndex].Name;
                    Require(a.Contains("layer1") == b.Contains("layer1"), "横向关节串接独立裙层。");
                }
            // 真实蒙皮份额必须落到新增动态骨，不能只增加无驱动盒体。
            var generatedIndexes = columns.Cast<object>().SelectMany(c => ((IList)Get(c, "Segments")).Cast<object>()
                .Select(s => (int)Get(s, "BoneIndex"))).ToHashSet();
            Require(f.Model.Vertices.Any(v => Read(v.SkinningOperator).Any(w => generatedIndexes.Contains(w.Key) && w.Value > 0)),
                "新增裙骨没有实际蒙皮作用。");
            CheckNonSkirtWeights(f, columns);
            CheckReferenceRemap(f, columns);
            Require(f.Model.Vertices.SelectMany(v => Read(v.SkinningOperator)).All(w => w.Key >= 0 && w.Key < f.Model.Bones.Length),
                "存在非法蒙皮索引。");
            float budget = (float)Get(Physics.GetField("SkirtPreset", Methods).GetValue(null), "RootMass") * f.SourceColumns;
            Require(System.Math.Abs(dynamic.Sum(b => b.Mass) - budget) < 1e-5f, "加密使裙摆总质量随刚体数增大。");
            CheckDrivenPose(f, dynamic.Select(b => b.AssociatedBoneIndex).ToHashSet());
        }
    }

    private static void CheckNonSkirtWeights(Fixture f, IList columns)
    {
        var replaced = columns.Cast<object>().SelectMany(c => ((IList)Get(c, "Segments")).Cast<object>())
            .Select(s => (string)Get(s, "SourceBoneName")).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < f.Model.Vertices.Length; i++)
        {
            var actual = Read(f.Model.Vertices[i].SkinningOperator)
                .GroupBy(w => f.Model.Bones[w.Key].Name).ToDictionary(g => g.Key, g => g.Sum(w => w.Value), StringComparer.Ordinal);
            var expected = f.OriginalWeights[i].Where(p => !replaced.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var pair in expected)
                Require(actual.TryGetValue(pair.Key, out float value) && System.Math.Abs(value - pair.Value) < 1e-6f,
                    "按骨名保留的非裙权重发生变化：" + pair.Key);
            Require(System.Math.Abs(expected.Values.Sum() - actual.Where(p => !generatedBoneName(p.Key)).Sum(p => p.Value)) < 1e-6f,
                "非裙总权重发生变化。");
        }
    }

    private static bool generatedBoneName(string name) => name.Contains("_seg");

    private static void CheckReferenceRemap(Fixture f, IList columns)
    {
        var firstColumnSegments = ((IList)Get(columns[0], "Segments")).Cast<object>().ToArray();
        string firstName = (string)Get(firstColumnSegments[0], "Name");
        int expected = Array.FindIndex(f.Model.Bones, bone => bone.Name == firstName);
        Bone body = f.Model.Bones.Single(bone => bone.Name == "body");
        Require(expected >= 0 && body.ChildBoneVal.Index == expected && body.AppendBoneVal.Index == expected &&
            body.IkInfoVal.IkTargetIndex == expected && body.IkInfoVal.IkLinks[0].LinkIndex == expected,
            "body的子骨、append或IK索引未指向被删原骨的首段。");
        Require(((Morph.BoneMorphData)f.Model.Morphs[0].MorphDatas[0]).BoneIndex == expected &&
            f.Model.Rigidbodies[0].AssociatedBoneIndex == expected &&
            f.Model.Entrys[0].Elements[0].BoneIndex == expected,
            "骨morph、既有刚体或显示框没有映射到被删原骨的首段。");
    }

    private static void CheckCurvedChain()
    {
        Check(0.6f, false, false, true);
        CheckSdefRemap();
    }

    private static void CheckSdefRemap()
    {
        var bones = new[]
        {
            new Bone { Name = "body", ParentIndex = -1 }, new Bone { Name = "old_skirt", ParentIndex = 0 },
            new Bone { Name = "retained", ParentIndex = 0 }
        };
        var vertex = new Vertex { SkinningOperator = new SkinningOperator { Type = SkinningType.SkinningSdef,
            Param = new Sdef { BoneId = new[] { 1, 2 }, BoneWeight = 0.7f } } };
        var model = new RawMMDModel { Bones = bones, Vertices = new[] { vertex } };
        object boneResult = Activator.CreateInstance(Runtime.GetType("PMXBoneExporter", true)
            .GetNestedType("Result", BindingFlags.NonPublic), true);
        boneResult.GetType().GetProperty("Bones").SetValue(boneResult, bones);
        int[] first = { 0, 1, 3 }, last = { 0, 2, 3 };
        MethodInfo remap = Runtime.GetType("PMXBoneIndexRemapper", true).GetMethods(Methods)
            .Single(m => m.Name == "Remap" && m.GetParameters().Length == 6);
        remap.Invoke(null, new object[] { model, boneResult, bones, new List<Bone> { bones[0],
            new Bone { Name = "old_skirt_seg0" }, new Bone { Name = "old_skirt_seg1" }, bones[2] }, first, last });
        var ids = ((Sdef)model.Vertices[0].SkinningOperator.Param).BoneId;
        Require(ids[0] == first[1] && ids[1] == first[2], "SDEF蒙皮没有映射到首段与保留骨。");
    }

    private static void CheckDrivenPose(Fixture f, HashSet<int> dynamicBones)
    {
        // 用真实蒙皮烘焙验证新增段旋转传到裙面，腰口保持固定。
        var host = new GameObject("Exported skirt skinning probe");
        Mesh mesh = new Mesh(), before = new Mesh(), after = new Mesh();
        try
        {
            var transforms = f.Model.Bones.Select(b => new GameObject(b.Name).transform).ToArray();
            for (int i = 0; i < transforms.Length; i++)
            {
                int parent = f.Model.Bones[i].ParentIndex;
                transforms[i].SetParent(parent >= 0 ? transforms[parent] : host.transform, false);
            }
            for (int i = 0; i < transforms.Length; i++)
            {
                int parent = f.Model.Bones[i].ParentIndex;
                transforms[i].localPosition = f.Model.Bones[i].Position -
                    (parent >= 0 ? f.Model.Bones[parent].Position : Vector3.zero);
            }
            var renderer = host.AddComponent<SkinnedMeshRenderer>();
            mesh.vertices = f.Model.Vertices.Select(v => v.Coordinate).ToArray();
            mesh.normals = f.Model.Vertices.Select(v => v.Normal).ToArray();
            MethodInfo convert = typeof(LibMMD.Unity3D.MMDModel).GetMethod("ConvertBoneWeight", Methods);
            mesh.boneWeights = f.Model.Vertices.Select(v => (BoneWeight)convert.Invoke(null, new object[] { v.SkinningOperator })).ToArray();
            mesh.bindposes = transforms.Select(t => t.worldToLocalMatrix * host.transform.localToWorldMatrix).ToArray();
            renderer.sharedMesh = mesh; renderer.bones = transforms; renderer.rootBone = transforms[0];
            renderer.BakeMesh(before);
            int selected = dynamicBones.OrderByDescending(b => f.Model.Vertices.Sum(v =>
                Read(v.SkinningOperator).Where(w => w.Key == b).Sum(w => w.Value) *
                (v.Coordinate - f.Model.Bones[b].Position).sqrMagnitude)).First();
            transforms[selected].localRotation = Quaternion.AngleAxis(20, Vector3.right);
            renderer.BakeMesh(after);
            Require(before.vertexCount == f.Model.Vertices.Length && after.vertexCount == before.vertexCount,
                "实际蒙皮烘焙丢失顶点。");
            var a = before.vertices; var bpoints = after.vertices;
            Require(a.Where((p, i) => (p - bpoints[i]).sqrMagnitude > 1e-8f).Any(), "新增段旋转没有驱动裙面。");
            for (int i = 0; i < a.Length; i += 5)
                Require((a[i] - bpoints[i]).sqrMagnitude < 1e-10f, "旋转裙段移动了固定腰口。");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(before); UnityEngine.Object.DestroyImmediate(after);
        }
    }

    private static Fixture Make(float length, bool asymmetric, bool layers, bool curved = false)
    {
        var f = new Fixture();
        Transform root = f.Root.transform;
        var controller = f.Root.AddComponent<SkirtController>();
        f.Context = Activator.CreateInstance(Physics.GetNestedType("Context", BindingFlags.NonPublic), true);
        Set(f.Context, "SkirtController", controller);
        f.Mesh = (PMXMeshExportContext)Activator.CreateInstance(typeof(PMXMeshExportContext), Fields, null, new object[] { root }, null);
        Set(f.Context, "Mesh", f.Mesh);
        var bodyBone = new Bone { Name = "body", ParentIndex = -1,
            ChildBoneVal = new Bone.ChildBone { ChildUseId = true, Index = 1 },
            AppendTranslate = true, AppendBoneVal = new Bone.AppendBone { Index = 1, Ratio = 0.5f },
            HasIk = true, IkInfoVal = new Bone.IkInfo { IkTargetIndex = 1, IkLinks = new[] { new Bone.IkLink { LinkIndex = 1 } } } };
        var bones = new List<Bone> { bodyBone };
        var transforms = new List<Transform> { root };
        var indexes = new Dictionary<Transform, int> { [root] = 0 };
        var positions = new List<Vector3>();
        var sourceWeights = new List<BoneWeight>();
        var vertices = new List<Vertex>();
        var triangles = new List<int>();
        for (int layer = 0; layer < (layers ? 2 : 1); layer++)
            for (int col = 0; col < 5; col++)
            {
                float real = asymmetric && col == 0 ? length * 0.6f : length;
                if (asymmetric && col == 1) real = length * 0.8f;
                var start = Child(root, "layer" + layer + "_skirt" + col, new Vector3(col * 0.09f, 1, layer * 0.01f));
                Transform middle = curved ? Child(start, start.name + "_bend", new Vector3(0.025f, -real * 0.5f, 0.015f)) : start;
                var end = Child(middle, start.name + "_end", curved
                    ? new Vector3(-0.025f, -real * 0.5f, -0.015f) : new Vector3(0, -real, 0));
                int stride = curved ? 3 : 2;
                int bone = 1 + layer * 3 * stride + (col / 2) * stride;
                if (col % 2 == 0)
                {
                    int endBone = bone + (curved ? 2 : 1);
                indexes[start] = bone;
                if (curved) indexes[middle] = bone + 1;
                indexes[end] = endBone;
                transforms.Add(start); if (curved) transforms.Add(middle); transforms.Add(end);
                bones.Add(new Bone { Name = start.name, Position = start.position, ParentIndex = 0,
                    UseLocalAxis = true, LocalAxisVal = new Bone.LocalAxis { AxisX = Vector3.right, AxisY = Vector3.up, AxisZ = Vector3.forward },
                    ChildBoneVal = new Bone.ChildBone { ChildUseId = true, Index = bone + 1 } });
                if (curved)
                    bones.Add(new Bone { Name = middle.name, Position = middle.position, ParentIndex = bone,
                        UseLocalAxis = true, LocalAxisVal = new Bone.LocalAxis { AxisX = Vector3.right, AxisY = Vector3.up, AxisZ = Vector3.forward },
                        ChildBoneVal = new Bone.ChildBone { ChildUseId = true, Index = endBone } });
                bones.Add(new Bone { Name = end.name, Position = end.position, ParentIndex = endBone - 1, ChildBoneVal = new Bone.ChildBone() });
                object chain = Activator.CreateInstance(Physics.GetNestedType("Chain", BindingFlags.NonPublic), true);
                Set(chain, "Root", start);
                ((HashSet<Transform>)Get(chain, "Bones")).UnionWith(curved ? new[] { start, middle, end } : new[] { start, end });
                var radii = (Dictionary<Transform, float>)Get(chain, "Radii"); radii[start] = 0.012f; radii[middle] = 0.012f; radii[end] = 0.012f;
                object column = Activator.CreateInstance(Physics.GetNestedType("SkirtColumn", BindingFlags.NonPublic), true);
                Set(column, "Chain", chain); ((IList)Get(f.Context, "SkirtColumns")).Add(column);
                }
                for (int row = 0; row < 5; row++)
                {
                    float t = row / 4f;
                    Vector3 point = curved && t <= 0.5f
                        ? Vector3.Lerp(start.position, middle.position, t * 2)
                        : curved ? Vector3.Lerp(middle.position, end.position, (t - 0.5f) * 2)
                            : start.position + Vector3.down * real * t;
                    positions.Add(point);
                    float body = row == 0 ? 1 : 0.35f;
                    float share = col % 2 == 0 ? 1 - body : (1 - body) * 0.5f;
                    int leftBone = col % 2 == 0 ? bone : 1 + layer * 3 * stride + ((col - 1) / 2) * stride;
                    int nextBone = col % 2 == 0 ? leftBone : leftBone + stride;
                    vertices.Add(new Vertex { Coordinate = point, Normal = Vector3.forward, SkinningOperator = new SkinningOperator
                    { Type = SkinningType.SkinningBdef4, Param = new Bdef4 { BoneId = new[] { 0, leftBone, nextBone, 0 },
                        BoneWeight = new[] { body, share, col % 2 == 0 ? 0 : share, 0f } } } });
                    sourceWeights.Add(new BoneWeight { boneIndex0 = 0, weight0 = body, boneIndex1 = leftBone, weight1 = share,
                        boneIndex2 = nextBone, weight2 = col % 2 == 0 ? 0 : share });
                    if (col > 0 && row > 0)
                    {
                        int v = positions.Count - 1;
                        triangles.AddRange(new[] { v - 6, v - 5, v, v - 6, v, v - 1 });
                    }
                }
            }
        f.SourceColumns = (layers ? 6 : 3);
        f.BoneResult = Activator.CreateInstance(Runtime.GetType("PMXBoneExporter", true).GetNestedType("Result", BindingFlags.NonPublic), true);
        f.BoneResult.GetType().GetProperty("Bones").SetValue(f.BoneResult, bones.ToArray());
        f.BoneResult.GetType().GetProperty("BoneIndexes").SetValue(f.BoneResult, indexes);
        f.Model = new RawMMDModel { Bones = bones.ToArray(), Vertices = vertices.ToArray() };
        f.Model.Morphs = new[] { new Morph { Type = Morph.MorphType.MorphTypeBone,
            MorphDatas = new Morph.MorphData[] { new Morph.BoneMorphData { BoneIndex = 1 } } } };
        f.Model.Rigidbodies = new[] { new MMDRigidBody { AssociatedBoneIndex = 1 } };
        f.Model.Entrys.Add(new PMXEntryItem { Elements = new List<PMXEntryItem.Element> {
            new PMXEntryItem.Element { BoneIndex = 1 } } });
        foreach (Vertex vertex in f.Model.Vertices)
            f.OriginalWeights.Add(Read(vertex.SkinningOperator).GroupBy(w => f.Model.Bones[w.Key].Name)
                .ToDictionary(g => g.Key, g => g.Sum(w => w.Value), StringComparer.Ordinal));
        var item = new PMXMeshExportContext.Item { VertexOffset = 0, Renderer = f.Root.AddComponent<MeshRenderer>() };
        Set(item, "Positions", positions.ToArray()); Set(item, "Weights", sourceWeights.ToArray()); Set(item, "Bones", transforms.ToArray());
        object sub = Activator.CreateInstance(typeof(PMXMeshExportContext).GetNestedType("Submesh", BindingFlags.NonPublic), true);
        Set(sub, "Indices", triangles.ToArray()); ((IList)Get(item, "Submeshes")).Add(sub);
        f.Mesh.Items.Add(item); f.Before = positions.ToArray();
        return f;
    }

    internal static void CheckOrderedRestSkinning(RawMMDModel model)
    {
        var positions = new Vector3[model.Bones.Length];
        var evaluated = new bool[model.Bones.Length];
        // 按 PMX 求值顺序重建零姿态，避免只检查静态骨坐标。
        foreach (int index in Enumerable.Range(0, model.Bones.Length)
            .OrderBy(i => model.Bones[i].PostPhysics).ThenBy(i => model.Bones[i].TransformLevel).ThenBy(i => i))
        {
            Bone bone = model.Bones[index];
            int parent = bone.ParentIndex;
            Require(parent < 0 || evaluated[parent], "父骨晚于子骨求值：" + bone.Name);
            positions[index] = parent < 0 ? bone.Position :
                positions[parent] + (bone.Position - model.Bones[parent].Position);
            evaluated[index] = true;
            Require(Vector3.Distance(positions[index], bone.Position) < 1e-5f, "零姿态骨位置漂移：" + bone.Name);
        }
        foreach (Vertex vertex in model.Vertices)
        {
            Vector3 delta = Vector3.zero;
            foreach (var weight in Read(vertex.SkinningOperator))
                delta += (positions[weight.Key] - model.Bones[weight.Key].Position) * weight.Value;
            Require(delta.magnitude < 1e-5f, "零姿态蒙皮被骨骼顺序拉伸。");
        }
    }

    private static List<KeyValuePair<int, float>> Read(SkinningOperator op)
    {
        if (op.Param is Bdef1 one) return new List<KeyValuePair<int, float>> { new KeyValuePair<int, float>(one.BoneId, 1) };
        if (op.Param is Bdef2 two) return new List<KeyValuePair<int, float>> {
            new KeyValuePair<int, float>(two.BoneId[0], two.BoneWeight), new KeyValuePair<int, float>(two.BoneId[1], 1 - two.BoneWeight) };
        var four = (Bdef4)op.Param;
        return Enumerable.Range(0, 4).Where(i => four.BoneWeight[i] > 0)
            .Select(i => new KeyValuePair<int, float>(four.BoneId[i], four.BoneWeight[i])).ToList();
    }
    private static void CheckLegacy()
    {
        string path = Path.GetFullPath("Logs/pmx-export-validation/assertions.json");
        byte[] before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            typeof(PMXExportRegressionTests).GetMethod("Run", Methods).Invoke(null, new object[] { true, false });
            JObject report = JObject.Parse(File.ReadAllText(path));
            Require((string)report["status"] == "passed", "既有眼部/掩码回归失败：" + report);
            string output = Argument("-pmxStage2Output", "Logs/teio-export-stage2-20261005");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "legacy-regression.json"), report.ToString());
        }
        finally { if (before != null) File.WriteAllBytes(path, before); else if (File.Exists(path)) File.Delete(path); }
    }
    private static void CheckColliderFit()
    {
        using (var f = new Fixture())
        {
            Transform root = f.Root.transform;
            root.SetPositionAndRotation(new Vector3(2, 3, 4), Quaternion.Euler(20, 60, 15));
            root.localScale = Vector3.one * 1.3f;
            Transform thigh = Child(root, "thigh", Vector3.zero);
            Transform knee = Child(thigh, "knee", Vector3.down * 0.4f);
            f.Mesh = (PMXMeshExportContext)Activator.CreateInstance(typeof(PMXMeshExportContext), Fields, null, new object[] { root }, null);
            var item = new PMXMeshExportContext.Item { Renderer = f.Root.AddComponent<MeshRenderer>() };
            var points = Enumerable.Range(0, 24).Select(i => new Vector3(
                0.025f * Mathf.Cos(i * Mathf.PI / 6), i < 12 ? -0.1f : -0.3f, 0.025f * Mathf.Sin(i * Mathf.PI / 6))).ToArray();
            Set(item, "Positions", points);
            Set(item, "Bones", new[] { thigh, knee });
            Set(item, "Weights", points.Select(p => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray());
            f.Mesh.Items.Add(item);
            MethodInfo fit = Runtime.GetType("PMXCollisionMeshFitter", true).GetMethod("TryFitCapsuleRadius", Methods);
            object[] args = { f.Mesh, root, thigh, knee, 0f };
            Require((bool)fit.Invoke(null, args), "有效加权腿网格拟合失败。");
            Require(System.Math.Abs((float)args[4] - 0.025f * 1.05f) < 1e-5f, "拟合使用了错误坐标或单位。");
            Set(item, "Positions", points.Select(p => new Vector3(p.x * 2.4f, p.y, p.z * 2.4f)).ToArray());
            var fittedBodies = new List<MMDRigidBody>();
            Runtime.GetType("PMXSkirtPhysicsExporter", true).GetMethod("AddKinematicCapsuleEnhanced", Methods)
                .Invoke(null, new object[] { "large_thigh_probe", thigh, knee, 0.025f, root,
                    new Dictionary<Transform, int> { [thigh] = 0, [knee] = 1 }, fittedBodies, f.Mesh });
            Require(fittedBodies.Count == 1 && System.Math.Abs(fittedBodies[0].Dimemsions.x - 0.06f * 1.05f) < 1e-5f,
                "增强碰撞体仍被旧绝对半径上限截断。");
            Set(item, "Weights", points.Select(p => new BoneWeight()).ToArray());
            Require(!(bool)fit.Invoke(null, args), "无蒙皮证据时必须回退。");
        }
    }
    private static object Get(object value, string name) => value.GetType().GetField(name, Fields).GetValue(value);
    private static void Set(object value, string name, object data) => value.GetType().GetField(name, Fields).SetValue(value, data);
    private static Transform Child(Transform parent, string name, Vector3 position)
    { var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.localPosition = position; return go.transform; }
    private static Transform FindChild(object sourceColumn, string name)
    {
        Transform root = (Transform)Get(Get(sourceColumn, "Chain"), "Root");
        if (root.name == name) return root;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) if (child.name == name) return child;
        throw new InvalidOperationException("找不到原裙骨Transform：" + name);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Argument(string name, string fallback)
    { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return Path.GetFullPath(args[i + 1]); return Path.GetFullPath(fallback); }
    private static void Run(JArray cases, string name, Action action)
    {
        try { action(); cases.Add(new JObject { ["name"] = name, ["status"] = "passed" }); }
        catch (Exception ex) { Debug.LogException(ex); cases.Add(new JObject { ["name"] = name, ["status"] = "failed", ["error"] = ex.ToString() }); }
    }
}
