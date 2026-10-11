using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LibMMD.Material;
using LibMMD.Model;
using LibMMD.Reader;
using LibMMD.Writer;
using UnityEditor;
using UnityEngine;

/// <summary>PMX 骨骼、物理和眼纹理导出的回归断言。</summary>
public static class PMXExportRegressionTests
{
    private static readonly List<CaseResult> Results = new List<CaseResult>();

    [Serializable]
    private sealed class Report
    {
        public string startedUtc;
        public string status;
        public CaseResult[] cases;
    }

    [Serializable]
    private sealed class CaseResult
    {
        public string name;
        public string status;
        public string details;
    }

    private sealed class Check
    {
        private readonly List<string> _failures = new List<string>();
        public void That(bool condition, string message)
        {
            if (!condition) _failures.Add(message);
        }
        public string FailureText => string.Join("\n", _failures.ToArray());
        public bool Passed => _failures.Count == 0;
    }

    [MenuItem("UmaViewer/Run PMX Export Regression Tests")]
    public static void RunFromMenu() => Run(writeBatchReport: true, exitWhenDone: false);

    // 可由隔离工程的 Unity -executeMethod 调用。
    public static void RunBatch() => Run(writeBatchReport: true, exitWhenDone: true);

    private static void Run(bool writeBatchReport, bool exitWhenDone)
    {
        Results.Clear();
        RunCase("眼骨构建与 PMX Writer/Reader 往返", TestBonesAndPhysics);
        RunCase("裙锚点、动态片与腿部碰撞体掩码", check => PMXSkirtMaskRegression.AssertExportedMasks());
        RunCase("Uma/Eye 合成、属性块与文件隔离", TestUmaEyeBake);
        RunNonReadableEyeCase();
        RunNarsEyeCase();
        RunOfficialEyeCase();

        bool failed = Results.Any(item => item.status == "failed");
        var report = new Report
        {
            startedUtc = DateTime.UtcNow.ToString("O"),
            status = failed ? "failed" : "passed",
            cases = Results.ToArray()
        };
        string reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/pmx-export-validation/assertions.json"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Debug.LogError("PMX 回归报告写入失败：" + exception);
            failed = true;
        }

        Debug.Log($"PMX 回归断言完成：{(failed ? "失败" : "通过")}；报告：{reportPath}");
        foreach (CaseResult item in Results)
            Debug.Log($"[{item.status}] {item.name}: {item.details}");
        if (exitWhenDone) EditorApplication.Exit(failed ? 1 : 0);
    }

    private static void RunCase(string name, Action<Check> test)
    {
        var check = new Check();
        try
        {
            test(check);
            Results.Add(new CaseResult
            {
                name = name,
                status = check.Passed ? "passed" : "failed",
                details = check.Passed ? "断言完成" : check.FailureText
            });
        }
        catch (Exception exception)
        {
            Results.Add(new CaseResult { name = name, status = "failed", details = exception.ToString() });
        }
    }

    private static void RunOfficialEyeCase()
    {
        Shader shader;
        try { shader = LoadLocalOfficialEyeShader(); }
        catch (Exception exception)
        {
            Results.Add(new CaseResult
            {
                name = "Gallop ToonEye 本地 AssetBundle 合成",
                status = "not_covered",
                details = "本机没有可读的本地 meta/AssetBundle 或 Shader 条目：" + exception.Message
            });
            return;
        }
        if (shader == null)
        {
            Results.Add(new CaseResult
            {
                name = "Gallop ToonEye 本地 AssetBundle 合成",
                status = "not_covered",
                details = "本地 AssetBundle 未提供目标 ToonEye shader。"
            });
            return;
        }

        RunCase("Gallop ToonEye 当前高光 UV/阈值合成", check => TestOfficialEyeBake(shader, check));
    }

    private static void TestBonesAndPhysics(Check check)
    {
        GameObject hipObject = null;
        GameObject tailRootObject = null;
        GameObject tailTipObject = null;
        GameObject meshObject = null;
        Mesh mesh = null;
        try
        {
            hipObject = new GameObject("Hip");
            Transform hip = hipObject.transform;
            Transform spine = Child(hip, "Spine", Vector3.up * 0.4f);
            Transform chest = Child(spine, "Chest", Vector3.up * 0.3f);
            Transform neck = Child(chest, "Neck", Vector3.up * 0.2f);
            Transform head = Child(neck, "Head", Vector3.up * 0.2f);
            Transform eyeL = Child(head, "Eye_L", Vector3.left * 0.04f);
            Transform eyeR = Child(head, "Eye_R", Vector3.right * 0.04f);
            Child(head, "Nose", Vector3.forward * 0.05f);
            tailRootObject = new GameObject("Tail_01");
            tailRootObject.transform.SetParent(hip, false);
            tailRootObject.transform.localPosition = Vector3.up * 0.2f;
            tailTipObject = new GameObject("Tail_02");
            tailTipObject.transform.SetParent(tailRootObject.transform, false);
            tailTipObject.transform.localPosition = Vector3.up * 0.2f;

            meshObject = new GameObject("PMX regression skin");
            meshObject.transform.SetParent(hip, false);
            SkinnedMeshRenderer skin = meshObject.AddComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "PMX regression triangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 1, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 }
            };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.triangles = new[] { 0, 1, 2 };
            skin.sharedMesh = mesh;
            skin.bones = new[] { eyeL, eyeR };
            skin.rootBone = hip;

            object boneResult = InvokeBoneBuild(hip, skin, new[] { tailRootObject.transform, tailTipObject.transform });
            Bone[] bones = (Bone[])Property(boneResult, "Bones");
            Dictionary<Transform, int> boneIndexes = (Dictionary<Transform, int>)Property(boneResult, "BoneIndexes");
            int headIndex = IndexOf(bones, "頭");
            int bothIndex = IndexOf(bones, "両目");
            foreach (string eyeName in new[] { "左目", "右目", "両目" })
            {
                Bone eye = bones[IndexOf(bones, eyeName)];
                check.That(eye.ParentIndex == headIndex, eyeName + " 应直接以頭为父骨。");
            }
            foreach (string eyeName in new[] { "左目", "右目" })
            {
                Bone eye = bones[IndexOf(bones, eyeName)];
                check.That(eye.AppendRotate && !eye.AppendTranslate, eyeName + " 应只追加旋转。");
                check.That(eye.AppendBoneVal.Index == bothIndex && Mathf.Abs(eye.AppendBoneVal.Ratio - 1) < 0.0001f,
                    eyeName + " 应以両目为追加源且倍率为 1。");
                check.That(eye.TransformLevel == 1, eyeName + " TransformLevel 应为 1。");
            }
            check.That(bones[bothIndex].ParentIndex == headIndex, "両目应直接以頭为父骨。");

            RawMMDModel model = NewEmptyModel(bones);
            BuildTailPhysics(boneResult, hip, tailRootObject.transform, tailTipObject.transform, model, true);
            RawMMDModel hairModel = NewEmptyModel(bones);
            BuildTailPhysics(boneResult, hip, tailRootObject.transform, tailTipObject.transform, hairModel, false);
            MMDRigidBody hairAnchor = FindBody(hairModel, "Tail_01_anchor");
            MMDRigidBody hairBody = FindBody(hairModel, "Tail_01_physics");
            check.That(hairAnchor != null && hairAnchor.CollisionMask == 0x0030 &&
                hairBody != null && hairBody.CollisionMask == 0x0030,
                "非尾巴链应只允许组5、6，mask 0x0030。");
            MMDRigidBody anchor = FindBody(model, "Tail_01_anchor");
            MMDRigidBody blocker = FindBody(model, "Tail_01_body_blocker");
            MMDRigidBody rootBody = FindBody(model, "Tail_01_physics");
            MMDRigidBody tipBody = FindBody(model, "Tail_02_physics");
            check.That(anchor != null && anchor.CollisionGroup == 3 && anchor.CollisionMask == 0x0030,
                "尾根静态锚点应为 group 3 / mask 0x0030，排除组4自碰和组3阻挡体。");
            check.That(blocker != null && blocker.CollisionGroup == 2 && blocker.CollisionMask == 0xFFFF,
                "尾部 blocker 应为 group 2 / mask 0xFFFF。");
            check.That(rootBody != null && rootBody.CollisionMask == 0x0034 && tipBody != null && tipBody.CollisionMask == 0x0034,
                "尾部动态链应为 mask 0x0034，禁止组4自碰并保留组3/5/6。");
            check.That(rootBody != null && Mathf.Abs(rootBody.Dimemsions.x - 0.02f) < 0.001f &&
                Mathf.Abs(rootBody.Dimemsions.y - 0.16f) < 0.002f, "尾根胶囊的半径与长度应维持原几何计算。");
            check.That(tipBody != null && Mathf.Abs(tipBody.Dimemsions.x - 0.02f) < 0.001f &&
                Mathf.Abs(tipBody.Dimemsions.y - 0.0725f) < 0.002f, "尾端胶囊的半径与长度应维持原几何计算。");
            MMDJoint tailJoint = model.Joints.FirstOrDefault(item => item.Name == "Tail_01_joint");
            check.That(tailJoint != null && Mathf.Abs(tailJoint.RotationHiLimit.x - 75 * Mathf.Deg2Rad) < 0.001f &&
                Mathf.Abs(tailJoint.SpringRotate.x - 12) < 0.001f, "尾根关节限制与弹簧参数应保持原值。");

            RawMMDModel reread = RoundTrip(model);
            Bone roundTripLeft = reread.Bones[IndexOf(reread.Bones, "左目")];
            check.That(roundTripLeft.ParentIndex == IndexOf(reread.Bones, "頭") && roundTripLeft.AppendRotate &&
                !roundTripLeft.AppendTranslate && roundTripLeft.TransformLevel == 1,
                "Reader 回读后的左目父骨、追加旋转和 TransformLevel 应一致。");
            MMDRigidBody roundTripTail = FindBody(reread, "Tail_01_physics");
            check.That(roundTripTail != null && roundTripTail.CollisionMask == 0x0034 &&
                Vector3.Distance(roundTripTail.Dimemsions, rootBody.Dimemsions) < 0.001f,
                "Reader 回读应保留尾链掩码与刚体尺寸。");
        }
        finally
        {
            if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
            Destroy(hipObject); Destroy(tailRootObject); Destroy(tailTipObject); Destroy(meshObject);
        }
    }

    private static void TestUmaEyeBake(Check check)
    {
        Shader shader = Shader.Find("Uma/Eye");
        if (shader == null)
        {
            check.That(false, "项目内 Uma/Eye shader 不可用，不能验证眼纹理合成。");
            return;
        }

        var owned = new List<UnityEngine.Object>();
        GameObject rendererObject = null;
        string outputRoot = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs/pmx-export-validation/eye-textures");
        try
        {
            Texture2D main = SolidTexture(16, 16, new Color(0.04f, 0.06f, 0.08f, 1)); owned.Add(main);
            Texture2D highLeft = HalfMaskTexture(leftHalf: true); owned.Add(highLeft);
            Texture2D highRight = HalfMaskTexture(leftHalf: false); owned.Add(highRight);
            Texture2D black = SolidTexture(4, 2, Color.clear); owned.Add(black);
            Mesh mesh = SquareMesh(); owned.Add(mesh);
            main.name = "pmx_eye_main_fixture";
            rendererObject = new GameObject("PMX eye bake test");
            // 夹具拥有网格实例，避免编辑模式读取 mesh 时额外复制。
            MeshFilter filter = rendererObject.AddComponent<MeshFilter>(); filter.mesh = mesh;
            MeshRenderer renderer = rendererObject.AddComponent<MeshRenderer>();
            Material material = new Material(shader) { name = "PMX eye A" }; owned.Add(material);
            material.SetTexture("_MainTex", main);
            material.SetTexture("_Highlight00", black);
            material.SetTexture("_Highlight01", black);
            material.SetFloat("_Show00", 0.5f); material.SetFloat("_Show01", 0.5f);
            renderer.sharedMaterial = material;

            Texture2D noLayer = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(noLayer);
            check.That(BrightPixels(noLayer) == 0, "无高光时结果不应产生高光像素。");
            material.SetTexture("_Highlight00", highLeft);
            Texture2D firstLayer = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(firstLayer);
            material.SetTexture("_Highlight00", black); material.SetTexture("_Highlight01", highRight);
            Texture2D swappedLayer = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(swappedLayer);
            material.SetTexture("_Highlight00", highLeft); material.SetTexture("_Highlight01", highRight);
            Texture2D bothLayers = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(bothLayers);
            int firstCount = BrightPixels(firstLayer), swappedCount = BrightPixels(swappedLayer);
            check.That(firstCount > 0 && firstCount < 256 && swappedCount > 0 && swappedCount < 256,
                "不同 UV 区域的单层高光应只覆盖部分眼部像素。");
            check.That(firstCount > 0 && swappedCount > 0 &&
                Mathf.Abs(firstLayer.GetPixel(1, 8).r - swappedLayer.GetPixel(1, 8).r) > 0.5f,
                "交换高光贴图槽位应改变 UV 对应的结果。");
            check.That(BrightPixels(bothLayers) > Mathf.Max(firstCount, swappedCount),
                "双层高光应覆盖两个单层的并集。");

            material.SetTexture("_Highlight01", black);
            Texture oldMain = material.GetTexture("_MainTex");
            Texture oldHigh0 = material.GetTexture("_Highlight00");
            Texture oldHigh1 = material.GetTexture("_Highlight01");
            RawMMDModel exportModel = NewEmptyModel(new[] { RootBone() });
            exportModel.TextureList.Add(new MMDTexture("Texture2D/" + main.name + ".png"));
            exportModel.Vertices = VerticesForRoundTrip(mesh);
            exportModel.TriangleIndexes = new[] { 0, 1, 2, 0, 2, 3, 0, 1, 2, 0, 2, 3 };
            Material sameNameOtherMaterial = new Material(shader) { name = material.name }; owned.Add(sameNameOtherMaterial);
            sameNameOtherMaterial.SetTexture("_MainTex", main);
            sameNameOtherMaterial.SetTexture("_Highlight00", black);
            sameNameOtherMaterial.SetTexture("_Highlight01", highRight);
            sameNameOtherMaterial.SetFloat("_Show00", 0.5f); sameNameOtherMaterial.SetFloat("_Show01", 0.5f);
            renderer.sharedMaterial = material;
            using (var context = PMXMeshExportContext.Capture(new[] { renderer }, renderer.transform))
                exportModel.Parts = context.ReadParts(exportModel, outputRoot);
            string firstPath = exportModel.Parts[0].Material.Texture.TexturePath;
            renderer.sharedMaterial = sameNameOtherMaterial;
            Part[] secondParts;
            using (var context = PMXMeshExportContext.Capture(new[] { renderer }, renderer.transform))
                secondParts = context.ReadParts(exportModel, outputRoot);
            string secondPath = secondParts[0].Material.Texture.TexturePath;
            exportModel.TriangleIndexes = new[] { 0, 1, 2, 0, 2, 3, 0, 1, 2, 0, 2, 3 };
            secondParts[0].BaseShift = 6;
            exportModel.Parts = new[] { exportModel.Parts[0], secondParts[0] };
            check.That(firstPath.StartsWith("Texture2D/pmx_eye_") && File.Exists(Path.Combine(outputRoot, firstPath)) &&
                File.Exists(Path.Combine(outputRoot, secondPath)), "统一材质上下文 应调用 TryExport 并写出被材质引用的 PNG。");
            check.That(firstPath != secondPath && exportModel.Parts[0].Material.Name == exportModel.Parts[1].Material.Name,
                "同名材质的不同合成内容应引用不同文件，避免串图覆盖。");
            if (File.Exists(Path.Combine(outputRoot, firstPath)))
            {
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                owned.Add(decoded);
                check.That(decoded.LoadImage(File.ReadAllBytes(Path.Combine(outputRoot, firstPath))), "输出 PNG 应能回读。");
                check.That(decoded.width == 16 && decoded.height == 16, "PNG 应保留主纹理尺寸。");
                check.That(decoded.GetPixel(1, 8).r > 0.5f && decoded.GetPixel(3, 8).r < 0.5f,
                    "PNG 回读后应仍保留第一层高光的 UV 像素位置。");
            }
            if (File.Exists(Path.Combine(outputRoot, secondPath)))
            {
                Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                owned.Add(decoded);
                check.That(decoded.LoadImage(File.ReadAllBytes(Path.Combine(outputRoot, secondPath))), "第二材质 PNG 应能回读。");
                check.That(decoded.GetPixel(1, 8).r < 0.5f && decoded.GetPixel(3, 8).r > 0.5f,
                    "第二材质 PNG 应保留交换后的高光位置。");
            }
            RawMMDModel reread = RoundTrip(exportModel);
            check.That(reread.Parts.Length == 2 && reread.Parts[0].Material.Texture.TexturePath == firstPath &&
                reread.Parts[1].Material.Texture.TexturePath == secondPath,
                "PMX Writer/Reader 应保留各同名材质指向的独立合成图路径。");
            check.That(material.GetTexture("_MainTex") == oldMain && material.GetTexture("_Highlight00") == oldHigh0 &&
                material.GetTexture("_Highlight01") == oldHigh1 && sameNameOtherMaterial.GetTexture("_MainTex") == main,
                "统一材质上下文/TryExport 不应替换源材质纹理。");

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            block.SetTexture("_Highlight00", highLeft); block.SetFloat("_Show00", 0.5f);
            renderer.SetPropertyBlock(block, 0);
            sameNameOtherMaterial.SetTexture("_Highlight00", black);
            Texture2D blockBake = PMXEyeTextureExporter.Bake(renderer, mesh, 0, sameNameOtherMaterial); owned.Add(blockBake);
            check.That(BrightPixels(blockBake) > 0, "材质属性块中的高光纹理应覆盖材质值参与合成。");
        }
        finally
        {
            Destroy(rendererObject);
            foreach (UnityEngine.Object resource in owned) DestroyResource(resource);
        }
    }

    private static void RunNarsEyeCase()
    {
        Shader shader = Shader.Find("Nars/UmaMusume/Eyes");
        if (shader == null)
        {
            Results.Add(new CaseResult { name = "Nars Eye 高光关键字合成", status = "not_covered", details = "本地 Nars Eye shader 不可用。" });
            return;
        }
        RunCase("Nars Eye 关闭、交换和双层合成", check => TestNarsEyeBake(shader, check));
    }

    private static void RunNonReadableEyeCase()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            Results.Add(new CaseResult
            {
                name = "不可读眼纹理 GPU 回读与状态恢复",
                status = "not_covered",
                details = "当前 Unity 图形设备为 Null，无法执行 Graphics.Blit/ReadPixels 回读。"
            });
            return;
        }
        RunCase("不可读眼纹理 GPU 回读与状态恢复", TestNonReadableEyeTextures);
    }

    private static void TestNonReadableEyeTextures(Check check)
    {
        Shader shader = Shader.Find("Uma/Eye");
        if (shader == null) { check.That(false, "Uma/Eye shader 不可用，无法测试 GPU 回读。"); return; }
        var owned = new List<UnityEngine.Object>();
        GameObject go = null;
        RenderTexture sentinel = null;
        RenderTexture previousActive = RenderTexture.active;
        bool previousSrgb = GL.sRGBWrite;
        try
        {
            Texture2D main = SolidTexture(16, 16, new Color(0.04f, 0.06f, 0.08f, 1)); owned.Add(main);
            Texture2D highlight = HalfMaskTexture(true); owned.Add(highlight);
            Texture2D black = SolidTexture(4, 2, Color.clear); owned.Add(black);
            Mesh mesh = SquareMesh(); owned.Add(mesh);
            go = new GameObject("PMX non-readable eye fixture");
            MeshFilter filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            Material material = new Material(shader) { name = "PMX non-readable eye" }; owned.Add(material);
            material.SetTexture("_MainTex", main);
            material.SetTexture("_Highlight00", highlight);
            material.SetTexture("_Highlight01", black);
            material.SetFloat("_Show00", 0.5f); material.SetFloat("_Show01", 0.5f);
            renderer.sharedMaterial = material;

            Texture2D baseline = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(baseline);
            sentinel = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32); owned.Add(sentinel);
            sentinel.Create();
            RenderTexture.active = sentinel;
            GL.sRGBWrite = !previousSrgb;
            main.Apply(false, true);
            highlight.Apply(false, true);
            Texture2D gpuReadback = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(gpuReadback);

            check.That(ImagesEqual(baseline, gpuReadback), "不可读主纹理和高光经 GPU 回读后应与可读基准一致。");
            check.That(RenderTexture.active == sentinel, "GPU 回读应恢复调用前的 RenderTexture.active。");
            check.That(GL.sRGBWrite == !previousSrgb, "GPU 回读应恢复调用前的 GL.sRGBWrite。");
        }
        finally
        {
            RenderTexture.active = previousActive;
            GL.sRGBWrite = previousSrgb;
            Destroy(go);
            foreach (UnityEngine.Object resource in owned) DestroyResource(resource);
        }
    }

    private static void TestNarsEyeBake(Shader shader, Check check)
    {
        var owned = new List<UnityEngine.Object>();
        GameObject go = null;
        try
        {
            Texture2D main = SolidTexture(16, 16, new Color(0.08f, 0.08f, 0.08f, 1)); owned.Add(main);
            Texture2D first = HalfMaskTexture(true); owned.Add(first);
            Texture2D second = HalfMaskTexture(false); owned.Add(second);
            Mesh mesh = SquareMesh(); owned.Add(mesh);
            go = new GameObject("PMX Nars eye fixture");
            MeshFilter filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            Material material = new Material(shader) { name = "PMX Nars eye" }; owned.Add(material);
            material.SetTexture("_MainTex", main);
            material.SetTexture("_High0Tex", first); material.SetTexture("_High1Tex", second);
            material.SetFloat("_HighLightBrightness", 1); material.SetFloat("_Switch1and2", 0);
            renderer.sharedMaterial = material;

            material.EnableKeyword("_HASHIGHLIGHT_NO"); material.DisableKeyword("_HASHIGHLIGHT_YES");
            Texture2D disabled = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(disabled);
            material.DisableKeyword("_HASHIGHLIGHT_NO"); material.EnableKeyword("_HASHIGHLIGHT_YES");
            material.EnableKeyword("_NUMBEROFHIGHLIGHTS_ONE"); material.DisableKeyword("_NUMBEROFHIGHLIGHTS_TWO");
            Texture2D one = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(one);
            material.SetFloat("_Switch1and2", 1);
            Texture2D swapped = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(swapped);
            material.DisableKeyword("_NUMBEROFHIGHLIGHTS_ONE"); material.EnableKeyword("_NUMBEROFHIGHLIGHTS_TWO");
            Texture2D both = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(both);

            check.That(BrightPixels(disabled) == 0, "Nars 的 _HASHIGHLIGHT_NO 应关闭高光。");
            check.That(BrightPixels(one) > 0 && BrightPixels(one) < 256 && BrightPixels(swapped) > 0 && BrightPixels(swapped) < 256,
                "Nars 单层模式应只混合被选中的遮罩区域。");
            check.That(!ImagesEqual(one, swapped), "Nars _Switch1and2 应交换单层高光来源。");
            check.That(BrightPixels(both) > Mathf.Max(BrightPixels(one), BrightPixels(swapped)),
                "Nars 双层模式应合并两组高光遮罩。");
        }
        finally
        {
            Destroy(go);
            foreach (UnityEngine.Object resource in owned) DestroyResource(resource);
        }
    }

    private static void TestOfficialEyeBake(Shader shader, Check check)
    {
        var owned = new List<UnityEngine.Object>();
        try
        {
            Material material = new Material(shader) { name = "Gallop ToonEye CPU fixture" }; owned.Add(material);
            Texture2D main = SolidTexture(16, 16, new Color(0.05f, 0.05f, 0.05f, 1)); owned.Add(main);
            Texture2D half = HalfMaskTexture(leftHalf: true); owned.Add(half);
            Texture2D black = SolidTexture(8, 8, Color.clear); owned.Add(black);
            material.SetTexture("_MainTex", main);
            material.SetTexture("_High0Tex", half); material.SetTexture("_High1Tex", black); material.SetTexture("_High2Tex", black);
            material.SetFloat("_Limit", 0.5f);
            material.SetVectorArray("_MainParam", new[] { Vector4.zero, Vector4.zero });
            material.SetVectorArray("_HighParam1", new[] { new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 1) });
            material.SetVectorArray("_HighParam2", new[] { new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 1) });
            Mesh mesh = SquareMesh(); owned.Add(mesh);
            mesh.SetUVs(1, new List<Vector2> { new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1) });
            mesh.SetUVs(2, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.one, Vector2.up });
            GameObject go = new GameObject("Official ToonEye fixture"); owned.Add(go);
            MeshFilter filter = go.AddComponent<MeshFilter>(); filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;

            Texture2D uv1Bake = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(uv1Bake);
            check.That(BrightPixels(uv1Bake) > 0 && BrightPixels(uv1Bake) < 256,
                "实际 Gallop shader 参数下，UV1 高光应定位到部分面片像素。");
            material.SetTexture("_High0Tex", black);
            Texture2D noHighlight = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(noHighlight);
            material.SetTexture("_High2Tex", half);
            Texture2D uv2Bake = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(uv2Bake);
            check.That(BrightPixels(uv2Bake) > 0 && BrightPixels(uv2Bake) < 256,
                "实际 Gallop shader 的 High2 应使用 UV2 产生独立高光。");
            check.That(!ImagesEqual(noHighlight, uv2Bake), "High2 开关应改变最终合成纹理。");

            material.SetTexture("_High2Tex", black); material.SetTexture("_High0Tex", half);
            const float rgbaHalf = 128f / 255f;
            material.SetFloat("_Limit", rgbaHalf);
            Texture2D thresholdEqual = SolidThresholdBake(renderer, mesh, material, rgbaHalf, owned);
            material.SetFloat("_Limit", rgbaHalf - 0.01f);
            Texture2D thresholdBelow = SolidThresholdBake(renderer, mesh, material, rgbaHalf, owned);
            check.That(BrightPixels(thresholdEqual) == 0 && BrightPixels(thresholdBelow) > 0,
                "实际 shader 的严格阈值等值不应点亮，超过阈值后应点亮。");
        }
        finally
        {
            foreach (UnityEngine.Object resource in owned) DestroyResource(resource);
        }
    }

    private static Texture2D SolidThresholdBake(Renderer renderer, Mesh mesh, Material material, float value,
        List<UnityEngine.Object> owned)
    {
        Texture2D threshold = SolidTexture(8, 8, new Color(value, value, value, 1)); owned.Add(threshold);
        material.SetTexture("_High0Tex", threshold);
        Texture2D result = PMXEyeTextureExporter.Bake(renderer, mesh, 0, material); owned.Add(result);
        return result;
    }

    private static Shader LoadLocalOfficialEyeShader()
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string configPath = Path.Combine(project, "Config.json");
        if (!File.Exists(configPath)) throw new FileNotFoundException("Config.json 不存在。");
        LocalConfig config = JsonUtility.FromJson<LocalConfig>(File.ReadAllText(configPath));
        if (config == null || string.IsNullOrWhiteSpace(config.MainPath)) throw new InvalidOperationException("MainPath 为空。");
        byte[] dbKey = Hex(config.DBKeyText), dbBase = Hex(config.DBBaseKeyText), abBase = Hex(config.ABKeyText);
        if (dbBase.Length < 13 || abBase.Length == 0) throw new InvalidOperationException("本地 DB/AB key 无效。");
        for (int i = 0; i < dbKey.Length; i++) dbKey[i] ^= dbBase[i % 13];
        string metaPath = Path.Combine(config.MainPath, "meta");
        if (!File.Exists(metaPath)) throw new FileNotFoundException("本地 meta 不存在。", metaPath);
        Dictionary<string, UmaDatabaseEntry> entries = UmaDatabaseController.ReadMetaFromEncryptedDb(metaPath, dbKey, 3);
        const string bundleName = "shader";
        const string shaderAssetPath = "assets/_gallop/resources/shader/3d/character/charactertooneyet.shader";
        UmaDatabaseEntry entry = entries.Values.FirstOrDefault(item =>
            string.Equals(item.Name, bundleName, StringComparison.OrdinalIgnoreCase));
        if (entry == null || string.IsNullOrEmpty(entry.Url)) throw new InvalidOperationException("meta 中没有 shader AssetBundle 条目。");
        string filePath = Path.Combine(config.MainPath, "dat", entry.Url.Substring(0, 2), entry.Url);
        if (!File.Exists(filePath)) throw new FileNotFoundException("shader AssetBundle 不在本地缓存。", filePath);
        byte[] key = AssetBundleKey(abBase, entry.Key);
        using (var stream = new UmaAssetBundleStream(filePath, key))
        {
            AssetBundle bundle = AssetBundle.LoadFromStream(stream);
            if (bundle == null) throw new InvalidDataException("本地 shader AssetBundle 无法加载。");
            try { return bundle.LoadAsset<Shader>(shaderAssetPath); }
            finally { bundle.Unload(false); }
        }
    }

    [Serializable]
    private sealed class LocalConfig
    {
        public string MainPath;
        public string DBKeyText;
        public string DBBaseKeyText;
        public string ABKeyText;
    }

    private static byte[] AssetBundleKey(byte[] baseKey, long key)
    {
        if (key == 0) return null;
        byte[] keyBytes = BitConverter.GetBytes(key);
        if (!BitConverter.IsLittleEndian) Array.Reverse(keyBytes);
        byte[] result = new byte[baseKey.Length * 8];
        for (int i = 0; i < baseKey.Length; i++)
            for (int j = 0; j < 8; j++) result[i * 8 + j] = (byte)(baseKey[i] ^ keyBytes[j]);
        return result;
    }

    private static byte[] Hex(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length % 2 != 0) return Array.Empty<byte>();
        byte[] bytes = new byte[value.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static object InvokeBoneBuild(Transform hip, Renderer renderer, Transform[] extraBones)
    {
        Type exporter = typeof(PMXEyeTextureExporter).Assembly.GetType("PMXBoneExporter", true);
        MethodInfo method = exporter.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);
        return Invoke(method, null, hip, hip, new[] { renderer }, extraBones);
    }

    private static void BuildTailPhysics(object boneResult, Transform hip, Transform tailRoot, Transform tailTip, RawMMDModel model, bool isTail)
    {
        Assembly assembly = typeof(PMXEyeTextureExporter).Assembly;
        Type exporter = assembly.GetType("PMXPhysicsExporter", true);
        Type contextType = exporter.GetNestedType("Context", BindingFlags.NonPublic);
        Type chainType = exporter.GetNestedType("Chain", BindingFlags.NonPublic);
        object context = Activator.CreateInstance(contextType, true);
        object chain = Activator.CreateInstance(chainType, true);
        SetField(chainType, chain, "Root", tailRoot);
        SetField(chainType, chain, "IsTail", isTail);
        SetField(chainType, chain, "IsEar", false);
        var bones = (HashSet<Transform>)GetField(chainType, chain, "Bones");
        var radii = (Dictionary<Transform, float>)GetField(chainType, chain, "Radii");
        bones.Add(tailRoot); bones.Add(tailTip); radii.Add(tailRoot, 0.02f); radii.Add(tailTip, 0.02f);
        IList chains = (IList)GetField(contextType, context, "Chains"); chains.Add(chain);
        MethodInfo method = exporter.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);
        Invoke(method, null, context, hip, boneResult, model);
    }

    private static object Invoke(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    private static object Property(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(instance);

    private static object GetField(Type type, object instance, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(instance);

    private static void SetField(Type type, object instance, string name, object value)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(instance, value);

    private static Transform Child(Transform parent, string name, Vector3 position)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false); child.localPosition = position;
        return child;
    }

    private static RawMMDModel NewEmptyModel(Bone[] bones) => new RawMMDModel
    {
        Name = "PMX regression", NameEn = "PMX regression", Description = "", DescriptionEn = "",
        Vertices = Array.Empty<Vertex>(), ExtraUvNumber = 0, TriangleIndexes = Array.Empty<int>(),
        Parts = Array.Empty<Part>(), Bones = bones, Morphs = Array.Empty<Morph>(),
        Rigidbodies = Array.Empty<MMDRigidBody>(), Joints = Array.Empty<MMDJoint>()
    };

    private static Bone RootBone() => new Bone
    {
        Name = "Root", NameEn = "Root", Position = Vector3.zero, ParentIndex = -1,
        Rotatable = true, Visible = true, Controllable = true,
        ChildBoneVal = new Bone.ChildBone { ChildUseId = false, Offset = Vector3.up * 0.1f }
    };

    private static Vertex[] VerticesForRoundTrip(Mesh mesh)
    {
        Vector3[] positions = mesh.vertices, normals = mesh.normals;
        Vector2[] uv = mesh.uv;
        return Enumerable.Range(0, positions.Length).Select(i => new Vertex
        {
            Coordinate = positions[i], Normal = normals[i], UvCoordinate = new Vector2(uv[i].x, 1 - uv[i].y),
            ExtraUvCoordinate = new[] { Vector4.zero, Vector4.zero, Vector4.zero }, EdgeScale = 1,
            SkinningOperator = new SkinningOperator
            {
                Type = SkinningOperator.SkinningType.SkinningBdef1,
                Param = new SkinningOperator.Bdef1 { BoneId = 0 }
            }
        }).ToArray();
    }

    private static RawMMDModel RoundTrip(RawMMDModel source)
    {
        var config = new ModelConfig();
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, true)) PMXWriter.Write(writer, source, config);
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, Encoding.Unicode, true)) return new PMXReader().Read(reader, config);
        }
    }

    private static int IndexOf(Bone[] bones, string name)
        => Array.FindIndex(bones, bone => bone.Name == name);

    private static MMDRigidBody FindBody(RawMMDModel model, string name)
        => model.Rigidbodies.FirstOrDefault(body => body.Name == name);

    private static Mesh SquareMesh()
    {
        var mesh = new Mesh { name = "PMX eye atlas fixture" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
        mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        return mesh;
    }

    private static Texture2D SolidTexture(int width, int height, Color color)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
        {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat
        };
        Color[] pixels = Enumerable.Repeat(color, width * height).ToArray();
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    private static Texture2D HalfMaskTexture(bool leftHalf)
    {
        var texture = new Texture2D(4, 2, TextureFormat.RGBA32, false, true)
        {
            filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat
        };
        var pixels = new Color[8];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                pixels[y * 4 + x] = ((x < 2) == leftHalf) ? Color.white : Color.clear;
        texture.SetPixels(pixels); texture.Apply(false, false);
        return texture;
    }

    private static int BrightPixels(Texture2D texture)
    {
        int count = 0;
        foreach (Color pixel in texture.GetPixels())
            if (Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) > 0.5f) count++;
        return count;
    }

    private static bool ImagesEqual(Texture2D a, Texture2D b)
    {
        if (a.width != b.width || a.height != b.height) return false;
        Color[] x = a.GetPixels(), y = b.GetPixels();
        for (int i = 0; i < x.Length; i++)
            if (Mathf.Abs(x[i].r - y[i].r) > 0.01f || Mathf.Abs(x[i].g - y[i].g) > 0.01f || Mathf.Abs(x[i].b - y[i].b) > 0.01f) return false;
        return true;
    }

    private static void Destroy(GameObject value)
    {
        if (value != null) UnityEngine.Object.DestroyImmediate(value);
    }

    private static void DestroyResource(UnityEngine.Object value)
    {
        if (value != null) UnityEngine.Object.DestroyImmediate(value);
    }
}
