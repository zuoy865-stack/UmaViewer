using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibMMD.Model;
using LibMMD.Reader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>真实 1003/00 生产导出探针；需由 Editor runner 在 Play Mode 启动。</summary>
public static class PMXStage1TeioProbe
{
    private const int CharacterId = 1003;
    private const string CostumeId = "00";
    private const string BodyMetaName = "3d/chara/body/bdy1003_00/pfb_bdy1003_00";
    private const int StartupTimeoutSeconds = 150;
    private const float PositionTolerance = 0.000001f;
    private const float RotationToleranceDegrees = 0.00001f;
    private const float ScaleTolerance = 0.000001f;

    public static void StartProbe()
    {
        if (!Application.isPlaying)
            throw new InvalidOperationException("StartProbe 需要已进入 Play Mode 的 Version2 场景。");
        UmaViewerMain host = UmaViewerMain.Instance;
        if (host == null) throw new InvalidOperationException("找不到 UmaViewerMain 协程宿主。");
        host.StartCoroutine(Run());
    }

    public static IEnumerator Run()
    {
        if (!Application.isPlaying)
            throw new InvalidOperationException("请在 Version2 场景进入 Play Mode 后启动 PMXStage1TeioProbe.Run()。");

        float startTime = Time.realtimeSinceStartup;
        while (!IsApplicationReady())
        {
            if (Time.realtimeSinceStartup - startTime > StartupTimeoutSeconds)
            {
                WriteFailure("启动等待超时。", null);
                throw new TimeoutException("UmaViewer 启动超时：等待角色数据库、资源索引或 Shader 列表完成。");
            }
            yield return null;
        }

        RunExport();
    }

    private static void RunExport()
    {
        string outputDirectory = ResolveOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        string pmxPath = Path.Combine(outputDirectory, "teio_1003_00.pmx");
        string reportPath = pmxPath + ".probe.json";
        if (File.Exists(pmxPath))
            throw new IOException("目标 PMX 已存在，为保护既有探针输出而中止：" + pmxPath);

        JObject report = new JObject
        {
            ["status"] = "running",
            ["characterId"] = CharacterId,
            ["costumeId"] = CostumeId,
            ["pmxPath"] = pmxPath,
            ["unityVersion"] = Application.unityVersion,
            ["applicationVersion"] = Application.version
        };
        try
        {
        UmaViewerMain main = UmaViewerMain.Instance;
        UmaViewerBuilder builder = UmaViewerBuilder.Instance;
        if (!main.AbList.TryGetValue(BodyMetaName, out UmaDatabaseEntry bodyEntry))
            throw new InvalidOperationException("本地解密后的 meta 不含目标身体资源：" + BodyMetaName);
        if (!File.Exists(bodyEntry.Path))
            throw new FileNotFoundException("本地 meta 有目标项，但身体 AssetBundle 不在磁盘；为避免下载已中止。", bodyEntry.Path);

        report["metaEntry"] = new JObject
        {
            ["name"] = bodyEntry.Name,
            ["url"] = bodyEntry.Url,
            ["checksum"] = bodyEntry.Checksum,
            ["encrypted"] = bodyEntry.IsEncrypted,
            ["bundlePresentLocally"] = true,
            ["bundlePath"] = bodyEntry.Path
        };

        CharaEntry character = main.Characters.FirstOrDefault(item => item.Id == CharacterId);
        if (character == null)
            throw new InvalidOperationException("meta 已就绪，但角色表中找不到角色 1003。");

        IEnumerator load = builder.LoadUma(character, CostumeId, false);
        while (load.MoveNext())
        {
            if (load.Current != null)
                throw new InvalidOperationException("LoadUma 发生异步等待；请由 Play Mode runner 启动 Run() 协程。");
        }

        UmaContainerCharacter container = builder.CurrentUMAContainer;
        if (container == null || container.CharaEntry == null || container.CharaEntry.Id != CharacterId)
            throw new InvalidOperationException("LoadUma 未生成目标角色容器。");
        if (container.GetComponentsInChildren<Renderer>(true).Length == 0)
            throw new InvalidOperationException("目标角色已创建，但没有可导出的 Renderer。");

        TransformSnapshot poseBefore = CaptureTransforms(container);
        MeshSnapshot meshesBefore = CaptureMeshes(container);
        report["transformExactSha256Before"] = poseBefore.ExactSha256;
        report["transformPoseBefore"] = poseBefore.ToJson();
        report["meshDataBefore"] = meshesBefore.ToJson();
        ModelExporter.ExportModel(container, pmxPath);
        TransformSnapshot poseAfter = CaptureTransforms(container);
        MeshSnapshot meshesAfter = CaptureMeshes(container);
        TransformDiff transformDiff = CompareTransforms(poseBefore, poseAfter);
        string meshDiff = CompareMeshes(meshesBefore, meshesAfter);
        report["transformExactSha256After"] = poseAfter.ExactSha256;
        report["transformPoseAfter"] = poseAfter.ToJson();
        report["exactBytesEqual"] = string.Equals(poseBefore.ExactSha256, poseAfter.ExactSha256, StringComparison.Ordinal);
        report["transformSemanticRestored"] = transformDiff.Restored;
        report["transformMaximumPositionDelta"] = transformDiff.MaxPositionDelta;
        report["transformMaximumPositionPath"] = transformDiff.MaxPositionPath;
        report["transformMaximumRotationDeltaDegrees"] = transformDiff.MaxRotationDeltaDegrees;
        report["transformMaximumRotationPath"] = transformDiff.MaxRotationPath;
        report["transformMaximumScaleDelta"] = transformDiff.MaxScaleDelta;
        report["transformMaximumScalePath"] = transformDiff.MaxScalePath;
        report["poseTolerances"] = new JObject
        {
            ["position"] = PositionTolerance,
            ["rotationDegrees"] = RotationToleranceDegrees,
            ["scale"] = ScaleTolerance
        };
        report["transformNodeSetStable"] = transformDiff.NodeSetStable;
        report["transformDifferences"] = JArray.FromObject(transformDiff.Differences);
        report["meshDataAfter"] = meshesAfter.ToJson();
        report["meshDataPreserved"] = string.IsNullOrEmpty(meshDiff);
        report["meshDataDifferences"] = meshDiff;
        report["blendShapesBeforeAfter"] = CompareBlendShapes(meshesBefore, meshesAfter);

        RawMMDModel exported = null;
        string structureError = null;
        try
        {
            exported = ReadPmx(pmxPath);
            ValidateStructure(exported);
        }
        catch (Exception exception)
        {
            structureError = exception.ToString();
        }
        string diagnosticsPath = pmxPath + ".export.json";
        JObject diagnostics = JObject.Parse(File.ReadAllText(diagnosticsPath));
        JToken meshes = diagnostics["meshDiagnostics"] ?? JValue.CreateNull();
        JToken[] removedValues = meshes.SelectTokens("$..removedTriangles").ToArray();
        int removedTriangles = removedValues.Sum(token => (int?)token ?? 0);

        JObject summary = new JObject
        {
            ["characterId"] = CharacterId,
            ["costumeId"] = CostumeId,
            ["pmxPath"] = pmxPath,
            ["pmxSha256"] = HashFile(pmxPath),
            ["parsedModel"] = new JObject
            {
                ["vertices"] = exported?.Vertices?.Length ?? 0,
                ["bones"] = exported?.Bones?.Length ?? 0,
                ["morphs"] = exported?.Morphs?.Length ?? 0,
                ["materials"] = exported?.Parts?.Length ?? 0,
                ["displayFrames"] = exported?.Entrys?.Count ?? 0,
                ["rigidBodies"] = exported?.Rigidbodies?.Length ?? 0,
                ["joints"] = exported?.Joints?.Length ?? 0
            },
            ["removedTrianglesTotal"] = removedTriangles,
            ["sourceMeshAndMaterialAudit"] = meshes,
            ["exportDiagnosticsPath"] = diagnosticsPath,
            ["unityVersion"] = Application.unityVersion,
            ["applicationVersion"] = Application.version
        };
        report["structureError"] = structureError == null ? JValue.CreateNull() : new JValue(structureError);
        report.Merge(summary, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        bool poseRestored = transformDiff.Restored && string.IsNullOrEmpty(meshDiff);
        report["poseRestored"] = poseRestored;
        report["status"] = poseRestored && structureError == null ? "passed" : "failed";
        SaveReport(reportPath, report);
        if (!poseRestored || structureError != null)
            throw new InvalidOperationException("真实 Teio 导出验收失败；逐项原因已写入 " + reportPath);
        Debug.Log("真实 Teio PMX 导出探针通过：" + reportPath);
        }
        catch (Exception exception)
        {
            report["status"] = "failed";
            report["error"] = exception.ToString();
            report["pmxExists"] = File.Exists(pmxPath);
            SaveReport(reportPath, report);
            throw;
        }
    }

    private static void WriteFailure(string message, Exception exception)
    {
        string directory = ResolveOutputDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "teio_1003_00.pmx.probe.json");
        var report = new JObject
        {
            ["status"] = "failed",
            ["characterId"] = CharacterId,
            ["costumeId"] = CostumeId,
            ["error"] = message,
            ["detail"] = exception == null ? JValue.CreateNull() : new JValue(exception.ToString()),
            ["unityVersion"] = Application.unityVersion
        };
        SaveReport(path, report);
    }

    private static void SaveReport(string path, JObject report) =>
        File.WriteAllText(path, report.ToString(Formatting.Indented), new UTF8Encoding(false));

    private static bool IsApplicationReady()
    {
        UmaViewerMain main = UmaViewerMain.Instance;
        UmaViewerBuilder builder = UmaViewerBuilder.Instance;
        return main != null && main.AbList != null && main.AbList.Count > 0 &&
               main.Characters != null && main.Characters.Any(item => item.Id == CharacterId) &&
               UmaViewerUI.Instance != null && UmaViewerUI.Instance.ModelSettings != null &&
               builder != null && builder.ShaderList != null && builder.ShaderList.Count > 0;
    }

    private static RawMMDModel ReadPmx(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream, Encoding.Unicode, false))
            return new PMXReader().Read(reader, new ModelConfig { GlobalToonPath = "Toon" });
    }

    private static void ValidateStructure(RawMMDModel model)
    {
        if (model.Entrys.Count(frame => string.Equals(frame.EntryItemName, "Root", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("实际 PMX 的 Root 显示框数量不是 1。");
        for (int frameIndex = 0; frameIndex < model.Entrys.Count; frameIndex++)
        {
            PMXEntryItem frame = model.Entrys[frameIndex];
            if (frame.Elements == null) throw new InvalidDataException($"显示框 {frameIndex} 缺少元素列表。");
            foreach (PMXEntryItem.Element element in frame.Elements)
            {
                int index = element.IsMorph ? element.MorphIndex : element.BoneIndex;
                int count = element.IsMorph ? model.Morphs.Length : model.Bones.Length;
                if (index < 0 || index >= count)
                    throw new InvalidDataException($"显示框“{frame.EntryItemName}”的{(element.IsMorph ? "表情" : "骨骼")}索引 {index} 越界 (0..{count - 1})。");
            }
        }

        int[] morphIndexes = model.Entrys.SelectMany(frame => frame.Elements)
            .Where(element => element.IsMorph).Select(element => element.MorphIndex).Distinct().OrderBy(index => index).ToArray();
        int[] boneIndexes = model.Entrys.SelectMany(frame => frame.Elements)
            .Where(element => !element.IsMorph).Select(element => element.BoneIndex).Distinct().OrderBy(index => index).ToArray();
        if (!morphIndexes.SequenceEqual(Enumerable.Range(0, model.Morphs.Length)))
            throw new InvalidDataException("实际 PMX 显示框未覆盖全部表情。");
        if (!boneIndexes.SequenceEqual(Enumerable.Range(0, model.Bones.Length)))
            throw new InvalidDataException("实际 PMX 显示框未覆盖全部骨骼。");
    }

    private sealed class TransformPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
    }

    private sealed class TransformSnapshot
    {
        public readonly System.Collections.Generic.Dictionary<string, TransformPose> Poses =
            new System.Collections.Generic.Dictionary<string, TransformPose>(StringComparer.Ordinal);
        public string ExactSha256;

        public JArray ToJson()
        {
            var result = new JArray();
            foreach (var pair in Poses.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                result.Add(new JObject
                {
                    ["path"] = pair.Key,
                    ["position"] = new JArray(pair.Value.Position.x, pair.Value.Position.y, pair.Value.Position.z),
                    ["rotation"] = new JArray(pair.Value.Rotation.x, pair.Value.Rotation.y,
                        pair.Value.Rotation.z, pair.Value.Rotation.w),
                    ["scale"] = new JArray(pair.Value.Scale.x, pair.Value.Scale.y, pair.Value.Scale.z)
                });
            }
            return result;
        }
    }

    private sealed class TransformDiff
    {
        public bool NodeSetStable;
        public bool Restored;
        public float MaxPositionDelta;
        public double MaxRotationDeltaDegrees;
        public float MaxScaleDelta;
        public string MaxPositionPath, MaxRotationPath, MaxScalePath;
        public readonly System.Collections.Generic.List<JObject> Differences = new System.Collections.Generic.List<JObject>();
    }

    private sealed class MeshRecord
    {
        public string RendererPath, RendererType, MeshName;
        public int RendererId, MeshId, VertexCount, SubmeshCount, BlendShapeCount;
        public string VerticesSha256, NormalsSha256, Uv0Sha256, Uv1Sha256, Uv2Sha256, Uv3Sha256;
        public string IndicesSha256, WeightsSha256;
        public string[] BonePaths, BlendShapeNames;
        public bool HasMesh;

        public JObject ToJson() => JObject.FromObject(this);
    }

    private sealed class MeshSnapshot
    {
        public readonly System.Collections.Generic.Dictionary<string, MeshRecord> Records =
            new System.Collections.Generic.Dictionary<string, MeshRecord>(StringComparer.Ordinal);

        public JArray ToJson()
        {
            var result = new JArray();
            foreach (var pair in Records.OrderBy(item => item.Key))
            {
                JObject record = pair.Value.ToJson();
                record["key"] = pair.Key;
                result.Add(record);
            }
            return result;
        }
    }

    private static TransformSnapshot CaptureTransforms(UmaContainerCharacter container)
    {
        var snapshot = new TransformSnapshot();
        var text = new StringBuilder();
        foreach (Transform transform in container.GetComponentsInChildren<Transform>(true))
        {
            string path = PathOf(transform, container.transform);
            var pose = new TransformPose
            {
                Position = transform.localPosition,
                Rotation = transform.localRotation,
                Scale = transform.localScale
            };
            snapshot.Poses.Add(path, pose);
            text.Append(path).Append('|').Append(Vector(pose.Position)).Append('|')
                .Append(QuaternionValue(pose.Rotation)).Append('|').Append(Vector(pose.Scale)).Append('\n');
        }
        snapshot.ExactSha256 = HashText(text.ToString());
        return snapshot;
    }

    private static TransformDiff CompareTransforms(TransformSnapshot before, TransformSnapshot after)
    {
        var result = new TransformDiff();
        var keys = new System.Collections.Generic.HashSet<string>(before.Poses.Keys, StringComparer.Ordinal);
        keys.UnionWith(after.Poses.Keys);
        result.NodeSetStable = before.Poses.Count == after.Poses.Count && before.Poses.Keys.All(after.Poses.ContainsKey);
        result.Restored = result.NodeSetStable;
        foreach (string path in keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            bool hasFirst = before.Poses.TryGetValue(path, out TransformPose first);
            bool hasLast = after.Poses.TryGetValue(path, out TransformPose last);
            if (!hasFirst || !hasLast)
            {
                result.Restored = false;
                result.Differences.Add(new JObject
                {
                    ["path"] = path,
                    ["change"] = hasFirst ? "removed" : "added"
                });
                continue;
            }

            float position = Vector3.Distance(first.Position, last.Position);
            double rotation = RotationDeltaDegrees(first.Rotation, last.Rotation);
            float scale = Vector3.Distance(first.Scale, last.Scale);
            if (position > result.MaxPositionDelta) { result.MaxPositionDelta = position; result.MaxPositionPath = path; }
            if (rotation > result.MaxRotationDeltaDegrees) { result.MaxRotationDeltaDegrees = rotation; result.MaxRotationPath = path; }
            if (scale > result.MaxScaleDelta) { result.MaxScaleDelta = scale; result.MaxScalePath = path; }

            var changed = new JArray();
            if (position > 0) changed.Add("position");
            if (rotation > 0) changed.Add("rotation");
            if (scale > 0) changed.Add("scale");
            if (changed.Count > 0)
                result.Differences.Add(new JObject
                {
                    ["path"] = path,
                    ["positionDelta"] = position,
                    ["rotationDeltaDegrees"] = rotation,
                    ["scaleDelta"] = scale,
                    ["positionBefore"] = new JArray(first.Position.x, first.Position.y, first.Position.z),
                    ["positionAfter"] = new JArray(last.Position.x, last.Position.y, last.Position.z),
                    ["rotationBefore"] = new JArray(first.Rotation.x, first.Rotation.y, first.Rotation.z, first.Rotation.w),
                    ["rotationAfter"] = new JArray(last.Rotation.x, last.Rotation.y, last.Rotation.z, last.Rotation.w),
                    ["scaleBefore"] = new JArray(first.Scale.x, first.Scale.y, first.Scale.z),
                    ["scaleAfter"] = new JArray(last.Scale.x, last.Scale.y, last.Scale.z),
                    ["changed"] = changed
                });

            if (position > PositionTolerance || rotation > RotationToleranceDegrees || scale > ScaleTolerance)
                result.Restored = false;
        }
        return result;
    }

    private static MeshSnapshot CaptureMeshes(UmaContainerCharacter container)
    {
        var snapshot = new MeshSnapshot();
        foreach (Renderer renderer in container.GetComponentsInChildren<Renderer>(true))
        {
            string path = PathOf(renderer.transform, container.transform);
            string key = path + "#" + renderer.GetType().Name;
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else if (renderer is MeshRenderer)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null) mesh = filter.sharedMesh;
            }
            var record = new MeshRecord
            {
                RendererPath = path,
                RendererType = renderer.GetType().FullName,
                RendererId = renderer.GetInstanceID(),
                HasMesh = mesh != null,
                MeshName = mesh == null ? null : mesh.name,
                MeshId = mesh == null ? 0 : mesh.GetInstanceID(),
                VertexCount = mesh == null ? 0 : mesh.vertexCount,
                SubmeshCount = mesh == null ? 0 : mesh.subMeshCount,
                BlendShapeCount = mesh == null ? 0 : mesh.blendShapeCount,
                BlendShapeNames = mesh == null ? Array.Empty<string>() : Enumerable.Range(0, mesh.blendShapeCount)
                    .Select(index => mesh.GetBlendShapeName(index)).ToArray(),
                BonePaths = renderer is SkinnedMeshRenderer skin && skin.bones != null
                    ? skin.bones.Select(bone => bone == null ? null : PathOf(bone, container.transform)).ToArray()
                    : Array.Empty<string>()
            };
            if (mesh != null)
            {
                record.VerticesSha256 = HashVectors(mesh.vertices);
                record.NormalsSha256 = HashVectors(mesh.normals);
                record.Uv0Sha256 = HashVectors(mesh.uv);
                record.Uv1Sha256 = HashVectors(mesh.uv2);
                record.Uv2Sha256 = HashVectors(mesh.uv3);
                record.Uv3Sha256 = HashVectors(mesh.uv4);
                var indices = new StringBuilder();
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    int[] values = mesh.GetIndices(submesh);
                    indices.Append(submesh).Append(':').Append(HashIntegers(values)).Append('\n');
                }
                record.IndicesSha256 = HashText(indices.ToString());
                if (renderer is SkinnedMeshRenderer)
                    record.WeightsSha256 = HashWeights(mesh.boneWeights);
            }
            snapshot.Records.Add(key, record);
        }
        return snapshot;
    }

    private static string CompareMeshes(MeshSnapshot before, MeshSnapshot after)
    {
        var differences = new System.Collections.Generic.List<string>();
        foreach (var pair in before.Records)
        {
            if (!after.Records.TryGetValue(pair.Key, out MeshRecord last))
            {
                differences.Add(pair.Key + ": renderer removed");
                continue;
            }
            MeshRecord first = pair.Value;
            CompareField(differences, pair.Key, "rendererId", first.RendererId, last.RendererId);
            CompareField(differences, pair.Key, "meshId", first.MeshId, last.MeshId);
            CompareField(differences, pair.Key, "meshName", first.MeshName, last.MeshName);
            CompareField(differences, pair.Key, "vertexCount", first.VertexCount, last.VertexCount);
            CompareField(differences, pair.Key, "submeshCount", first.SubmeshCount, last.SubmeshCount);
            CompareField(differences, pair.Key, "vertices", first.VerticesSha256, last.VerticesSha256);
            CompareField(differences, pair.Key, "normals", first.NormalsSha256, last.NormalsSha256);
            CompareField(differences, pair.Key, "uv0", first.Uv0Sha256, last.Uv0Sha256);
            CompareField(differences, pair.Key, "uv1", first.Uv1Sha256, last.Uv1Sha256);
            CompareField(differences, pair.Key, "uv2", first.Uv2Sha256, last.Uv2Sha256);
            CompareField(differences, pair.Key, "uv3", first.Uv3Sha256, last.Uv3Sha256);
            CompareField(differences, pair.Key, "indices", first.IndicesSha256, last.IndicesSha256);
            CompareField(differences, pair.Key, "weights", first.WeightsSha256, last.WeightsSha256);
            if (!first.BonePaths.SequenceEqual(last.BonePaths)) differences.Add(pair.Key + ": bone paths changed");
        }
        foreach (string added in after.Records.Keys.Except(before.Records.Keys))
            differences.Add(added + ": renderer added");
        return string.Join("\n", differences.ToArray());
    }

    private static JArray CompareBlendShapes(MeshSnapshot before, MeshSnapshot after)
    {
        var result = new JArray();
        foreach (var pair in before.Records.OrderBy(item => item.Key))
        {
            after.Records.TryGetValue(pair.Key, out MeshRecord last);
            last = last ?? new MeshRecord { BlendShapeNames = Array.Empty<string>() };
            result.Add(new JObject
            {
                ["renderer"] = pair.Key,
                ["beforeCount"] = pair.Value.BlendShapeCount,
                ["beforeNames"] = JArray.FromObject(pair.Value.BlendShapeNames),
                ["afterCount"] = last.BlendShapeCount,
                ["afterNames"] = JArray.FromObject(last.BlendShapeNames)
            });
        }
        return result;
    }

    private static void CompareField<T>(System.Collections.Generic.List<string> differences,
        string path, string field, T first, T last)
    {
        if (!Equals(first, last)) differences.Add(path + ": " + field + " changed");
    }

    private static string PathOf(Transform transform, Transform root)
    {
        var parts = new System.Collections.Generic.List<string>();
        while (transform != null && transform != root)
        {
            parts.Add(transform.name + "[" + transform.GetSiblingIndex() + "]");
            transform = transform.parent;
        }
        parts.Reverse();
        return parts.Count == 0 ? "." : string.Join("/", parts.ToArray());
    }

    private static string HashVectors(Vector3[] values) => HashValues(values, (text, value) => text.Append(Vector(value)).Append('\n'));
    private static string HashVectors(Vector2[] values) => HashValues(values, (text, value) => text.Append(Vector(value)).Append('\n'));
    private static string HashIntegers(int[] values) => HashValues(values, (text, value) => text.Append(value).Append('\n'));

    private static string HashWeights(BoneWeight[] values) => HashValues(values, (text, value) =>
        text.Append(value.boneIndex0).Append(',').Append(F(value.weight0)).Append('|')
            .Append(value.boneIndex1).Append(',').Append(F(value.weight1)).Append('|')
            .Append(value.boneIndex2).Append(',').Append(F(value.weight2)).Append('|')
            .Append(value.boneIndex3).Append(',').Append(F(value.weight3)).Append('\n'));

    private static string HashValues<T>(T[] values, Action<StringBuilder, T> append)
    {
        var text = new StringBuilder();
        foreach (T value in values) append(text, value);
        return HashText(text.ToString());
    }

    private static string HashText(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }

    private static string F(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static double RotationDeltaDegrees(Quaternion first, Quaternion last)
    {
        double firstLength = Math.Sqrt((double)first.x * first.x + (double)first.y * first.y +
            (double)first.z * first.z + (double)first.w * first.w);
        double lastLength = Math.Sqrt((double)last.x * last.x + (double)last.y * last.y +
            (double)last.z * last.z + (double)last.w * last.w);
        if (firstLength < 1e-30 || lastLength < 1e-30) return double.PositiveInfinity;
        double dot = Math.Abs(((double)first.x * last.x + (double)first.y * last.y +
            (double)first.z * last.z + (double)first.w * last.w) / (firstLength * lastLength));
        dot = Math.Max(0.0, Math.Min(1.0, dot));
        return 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
    }

    private static string Vector(Vector2 value) => F(value.x) + "," + F(value.y);

    private static string Vector(Vector3 value) =>
        F(value.x) + "," + F(value.y) + "," + F(value.z);

    private static string QuaternionValue(Quaternion value) =>
        F(value.x) + "," + F(value.y) + "," + F(value.z) + "," + F(value.w);

    private static string ResolveOutputDirectory()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("-pmxStage1Output=", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i].Substring("-pmxStage1Output=".Length));
            if (string.Equals(args[i], "-pmxStage1Output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("-pmxStage1Output 后必须跟输出目录。");
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return Path.GetFullPath(Path.Combine(Application.dataPath,
            "../Logs/teio-export-stage1-20261005/teio-new"));
    }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
