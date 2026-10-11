using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Gallop;
using LibMMD.Model;
using LibMMD.Reader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static LibMMD.Model.SkinningOperator;

/// <summary>冻结真实 Teio 网格重放裙骨替换；不代替实际角色导出。</summary>
public static class PMXSkirtRealMeshRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void RunFixture()
    {
        const string source = "Logs/teio-export-stage1-20261005/teio-final/teio_1003_00.pmx";
        const string inputDirectory = "Logs/teio-export-stage2a-20261005";
        var input = JsonConvert.DeserializeObject<PMXSkirtTopologyInput>(File.ReadAllText(inputDirectory + "/teio-input.json"));
        JObject provenance = JObject.Parse(File.ReadAllText(inputDirectory + "/input-provenance.json"));
        JObject sidecar = JObject.Parse(File.ReadAllText(source + ".export.json"));
            RawMMDModel model = new PMXReader().Read(source, new ModelConfig { GlobalToonPath = "Toon" });
        Type physics = typeof(ModelExporter).Assembly.GetType("PMXPhysicsExporter", true);
        Type layout = typeof(ModelExporter).Assembly.GetType("PMXSkirtLayoutExporter", true);
        var root = new GameObject("Frozen Teio production replay");
        PMXMeshExportContext mesh = null;
        try
        {
            // Reader 反转 X/Z；只反转 X 使坐标与只读输入采用同一空间。
            foreach (Bone bone in model.Bones) bone.Position = new Vector3(-bone.Position.x, bone.Position.y, bone.Position.z);
            for (int i = 0; i < model.Vertices.Length; i++)
            { model.Vertices[i].Coordinate = input.Vertices[i]; model.Vertices[i].Normal = input.VertexNormals[i]; }
            Vector3[] frozenPositions = model.Vertices.Select(v => v.Coordinate).ToArray();
            var transforms = model.Bones.Select(b => new GameObject(b.Name).transform).ToArray();
            for (int i = 0; i < transforms.Length; i++)
            {
                int parent = model.Bones[i].ParentIndex;
                transforms[i].SetParent(parent >= 0 ? transforms[parent] : root.transform, false);
                transforms[i].localPosition = model.Bones[i].Position - (parent >= 0 ? model.Bones[parent].Position : Vector3.zero);
            }
            object context = Activator.CreateInstance(physics.GetNestedType("Context", BindingFlags.NonPublic), true);
            Set(context, "SkirtController", root.AddComponent<SkirtController>());
            mesh = (PMXMeshExportContext)Activator.CreateInstance(typeof(PMXMeshExportContext), Fields, null, new object[] { root.transform }, null);
            Set(context, "Mesh", mesh);
            MethodInfo convert = typeof(LibMMD.Unity3D.MMDModel).GetMethod("ConvertBoneWeight", Methods);
            foreach (JObject rendererInfo in sidecar["meshDiagnostics"].OfType<JObject>().Where(r => (string)r["status"] == "captured"))
            {
                int offset = (int)rendererInfo["vertexOffset"], count = (int)rendererInfo["vertexCount"];
                var renderer = new GameObject((string)rendererInfo["rendererPath"]);
                renderer.transform.SetParent(root.transform, false);
                var item = new PMXMeshExportContext.Item { Renderer = renderer.AddComponent<MeshRenderer>(), VertexOffset = offset };
                Set(item, "Positions", input.Vertices.Skip(offset).Take(count).ToArray());
                Set(item, "Bones", transforms);
                Set(item, "Weights", model.Vertices.Skip(offset).Take(count).Select(v => (BoneWeight)convert.Invoke(null, new object[] { v.SkinningOperator })).ToArray());
                var indices = new List<int>();
                for (int i = 0; i < input.TriangleIndices.Count; i += 3)
                    if (Enumerable.Range(0, 3).All(j => input.TriangleIndices[i + j] >= offset && input.TriangleIndices[i + j] < offset + count))
                        indices.AddRange(Enumerable.Range(0, 3).Select(j => input.TriangleIndices[i + j] - offset));
                object submesh = Activator.CreateInstance(typeof(PMXMeshExportContext).GetNestedType("Submesh", BindingFlags.NonPublic), true);
                Set(submesh, "Indices", indices.ToArray()); ((IList)Get(item, "Submeshes")).Add(submesh);
                mesh.Items.Add(item);
            }
            foreach (JObject evidence in provenance["columns"])
            {
                int[] chainIndices = evidence["chainBones"].Values<int>().ToArray();
                var expected = input.Columns.First(c => c.ColumnId == "bone:" + (int)evidence["rootBone"]);
                for (int j = 0; j < chainIndices.Length; j++)
                    if (Vector3.Distance(transforms[chainIndices[j]].position, expected.RestPolyline[j]) > 1e-6f)
                        throw new InvalidOperationException("冻结重放的骨坐标与独立解析输入不符。");
                object chain = Activator.CreateInstance(physics.GetNestedType("Chain", BindingFlags.NonPublic), true);
                Set(chain, "Root", transforms[(int)evidence["rootBone"]]);
                foreach (int index in chainIndices)
                {
                    ((HashSet<Transform>)Get(chain, "Bones")).Add(transforms[index]);
                    ((Dictionary<Transform, float>)Get(chain, "Radii"))[transforms[index]] = 0.012f;
                }
                object column = Activator.CreateInstance(physics.GetNestedType("SkirtColumn", BindingFlags.NonPublic), true);
                Set(column, "Chain", chain); ((IList)Get(context, "SkirtColumns")).Add(column);
            }
            var sourceChains = provenance["columns"].OfType<JObject>().ToDictionary(
                evidence => evidence["rootBone"].Value<int>().ToString(), evidence => evidence["chainBones"].Values<int>()
                    .Select(index => model.Bones[index].Name).ToArray());
            var sourcePositions = model.Bones.ToDictionary(bone => bone.Name, bone => bone.Position, StringComparer.Ordinal);
            object bones = Activator.CreateInstance(typeof(ModelExporter).Assembly.GetType("PMXBoneExporter", true).GetNestedType("Result", BindingFlags.NonPublic), true);
            bones.GetType().GetProperty("Bones").SetValue(bones, model.Bones);
            bones.GetType().GetProperty("BoneIndexes").SetValue(bones, transforms.Select((t, i) => new { t, i }).ToDictionary(p => p.t, p => p.i));
            object result = layout.GetMethod("TryBuild", Methods).Invoke(null, new[] { context, root.transform, bones, model });
            if (result == null) throw new InvalidOperationException("真实冻结 Teio 的原骨四段替换布局回退。");
            var columns = (IList)Get(result, "Columns");
            if (columns.Count != 10 || columns.Count != input.Columns.Count)
                throw new InvalidOperationException("真实裙摆周向列数不为原有十列。");
            if (!frozenPositions.SequenceEqual(model.Vertices.Select(v => v.Coordinate)))
                throw new InvalidOperationException("布局改变了冻结顶点位置。");
            PMXSkirtProductionRegression.CheckOrderedRestSkinning(model);
            var replaced = ((IList)Get(result, "ReplacedBoneNames")).Cast<string>().ToHashSet(StringComparer.Ordinal);
            foreach (JObject evidence in provenance["columns"])
            {
                string[] chainNames = sourceChains[evidence["rootBone"].Value<int>().ToString()];
                if (chainNames.Length < 2) throw new InvalidOperationException("真实裙骨链缺少末端 marker。");
                string markerName = chainNames[chainNames.Length - 1];
                foreach (string sourceName in chainNames.Take(chainNames.Length - 1))
                    if (!replaced.Contains(sourceName) || model.Bones.Any(b => b.Name == sourceName))
                        throw new InvalidOperationException("真实 Teio 原裙物理骨未从最终骨架删除。");
                int markerIndex = Array.FindIndex(model.Bones, b => b.Name == markerName);
                if (markerIndex < 0) throw new InvalidOperationException("真实 Teio 末端 marker 丢失。");
                var column = columns.Cast<object>().Single(c => (string)Get(c, "ColumnId") == chainNames[0]);
                var segments = ((IList)Get(column, "Segments")).Cast<object>().ToArray();
                var perSource = segments.GroupBy(s => (string)Get(s, "SourceBoneName"));
                foreach (var group in perSource)
                {
                    object[] four = group.ToArray();
                    if (four.Length != 4) throw new InvalidOperationException("真实 Teio 每条原骨未精确拆成四段。");
                    string[] path = chainNames;
                    int sourceAt = Array.IndexOf(path, group.Key);
                    if (sourceAt < 0 || sourceAt + 1 >= path.Length ||
                        Vector3.Distance((Vector3)Get(four[0], "OriginalStart"), sourcePositions[path[sourceAt]]) > 1e-5f ||
                        Vector3.Distance((Vector3)Get(four[0], "OriginalEnd"), sourcePositions[path[sourceAt + 1]]) > 1e-5f)
                        throw new InvalidOperationException("真实 Teio 区间未使用原骨与实际 chain child 端点。");
                    for (int part = 0; part < 4; part++)
                    {
                        Vector3 start = Vector3.Lerp(sourcePositions[path[sourceAt]], sourcePositions[path[sourceAt + 1]], part / 4f);
                        Vector3 end = Vector3.Lerp(sourcePositions[path[sourceAt]], sourcePositions[path[sourceAt + 1]], (part + 1) / 4f);
                        if (Vector3.Distance((Vector3)Get(four[part], "Start"), start) > 1e-5f ||
                            Vector3.Distance((Vector3)Get(four[part], "End"), end) > 1e-5f)
                            throw new InvalidOperationException("真实 Teio 新骨没有沿原区间直线四等分。");
                    }
                    foreach (object segment in four)
                    {
                        if ((bool)Get(segment, "IsVirtual")) throw new InvalidOperationException("真实 Teio 布局出现虚拟段。");
                        int finalIndex = (int)Get(segment, "BoneIndex");
                        if (finalIndex < 0 || finalIndex >= model.Bones.Length)
                            throw new InvalidOperationException("真实 Teio 新段索引不是最终骨架索引。");
                    }
                }
                string lastSourceName = chainNames[chainNames.Length - 2];
                object[] lastFour = segments.Where(s => (string)Get(s, "SourceBoneName") == lastSourceName).ToArray();
                if (lastFour.Length != 4 || model.Bones[markerIndex].ParentIndex != (int)Get(lastFour[3], "BoneIndex"))
                    throw new InvalidOperationException("真实 Teio 末端 marker 未接到最后一段。");
            }
            if (model.Vertices.SelectMany(v => Read(v.SkinningOperator)).Any(index => index < 0 || index >= model.Bones.Length))
                throw new InvalidOperationException("真实 Teio 蒙皮引用包含越界骨索引。");
            foreach (Bone bone in model.Bones)
            {
                if (bone.ParentIndex < -1 || bone.ParentIndex >= model.Bones.Length ||
                    (bone.ChildBoneVal != null && bone.ChildBoneVal.ChildUseId &&
                     (bone.ChildBoneVal.Index < 0 || bone.ChildBoneVal.Index >= model.Bones.Length)))
                    throw new InvalidOperationException("真实 Teio 骨架父子引用越界。");
                if ((bone.AppendRotate || bone.AppendTranslate) &&
                    (bone.AppendBoneVal.Index < 0 || bone.AppendBoneVal.Index >= model.Bones.Length))
                    throw new InvalidOperationException("真实 Teio 骨骼追加变形引用越界。");
                if (bone.HasIk && (bone.IkInfoVal == null || bone.IkInfoVal.IkTargetIndex < 0 ||
                    bone.IkInfoVal.IkTargetIndex >= model.Bones.Length || (bone.IkInfoVal.IkLinks ?? Array.Empty<Bone.IkLink>()).Any(link =>
                        link.LinkIndex < 0 || link.LinkIndex >= model.Bones.Length)))
                    throw new InvalidOperationException("真实 Teio IK引用越界。");
            }
            foreach (Morph morph in model.Morphs ?? Array.Empty<Morph>())
            {
                foreach (Morph.MorphData data in morph.MorphDatas ?? Array.Empty<Morph.MorphData>())
                {
                    if (data is Morph.BoneMorphData boneMorph &&
                        (boneMorph.BoneIndex < 0 || boneMorph.BoneIndex >= model.Bones.Length))
                        throw new InvalidOperationException("真实 Teio 骨骼形态引用越界。");
                    if (data is Morph.GroupMorphData groupMorph &&
                        (groupMorph.MorphIndex < 0 || groupMorph.MorphIndex >= (model.Morphs?.Length ?? 0)))
                        throw new InvalidOperationException("真实 Teio 组合形态引用越界。");
                }
            }
            foreach (PMXEntryItem entry in model.Entrys ?? new List<PMXEntryItem>())
                foreach (PMXEntryItem.Element item in entry.Elements ?? new List<PMXEntryItem.Element>())
                    if (item.IsMorph ? item.MorphIndex < 0 || item.MorphIndex >= (model.Morphs?.Length ?? 0) :
                        item.BoneIndex < 0 || item.BoneIndex >= model.Bones.Length)
                        throw new InvalidOperationException("真实 Teio 显示框引用越界。");
            foreach (MMDRigidBody body in model.Rigidbodies ?? Array.Empty<MMDRigidBody>())
                if (body.AssociatedBoneIndex < -1 || body.AssociatedBoneIndex >= model.Bones.Length)
                    throw new InvalidOperationException("真实 Teio 刚体骨骼引用越界。");
            foreach (MMDJoint joint in model.Joints ?? Array.Empty<MMDJoint>())
                if (joint.AssociatedRigidBodyIndex.Any(index => index < -1 || index >= (model.Rigidbodies?.Length ?? 0)))
                    throw new InvalidOperationException("真实 Teio 关节刚体引用越界。");
        }
        finally { mesh?.Dispose(); UnityEngine.Object.DestroyImmediate(root); }
    }

    private static object Get(object value, string name) => value.GetType().GetField(name, Fields).GetValue(value);
    private static void Set(object value, string name, object data) => value.GetType().GetField(name, Fields).SetValue(value, data);
    private static IEnumerable<int> Read(SkinningOperator op)
    {
        if (op.Param is Bdef1 one) return new[] { one.BoneId };
        if (op.Param is Bdef2 two) return two.BoneId;
        return ((Bdef4)op.Param).BoneId;
    }
}
