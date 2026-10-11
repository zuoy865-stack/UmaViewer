using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LibMMD.Material;
using LibMMD.Model;

public static class PMXMaterialBakeRegression
{
    private const string OutputRelativePath = "Logs/pmx-pseudo-highlight-plan-20261006/gpu-regression.json";
    private static BatchReport _lastReport;
    private static readonly List<UnityEngine.Object> TemporaryObjects = new List<UnityEngine.Object>();

    [Serializable]
    private sealed class BatchReport
    {
        public string capturedUtc, colorSpace, status, error;
        public CaseResult[] cases;
    }

    [Serializable]
    private sealed class CaseResult
    {
        public string name, status, details;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        public GameObject Root;
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Material Material;

        public T Keep<T>(T value) where T : UnityEngine.Object
        {
            _objects.Add(value);
            return value;
        }

        public void Dispose()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }
    }

    [MenuItem("UmaViewer/Run PMX Material Bake GPU Regression")]
    public static void RunFromMenu() => Run(false);

    public static void RunBatch() => Run(true);

    public static object Assert()
    {
        _lastReport = new BatchReport
        {
            capturedUtc = DateTime.UtcNow.ToString("O"),
            colorSpace = QualitySettings.activeColorSpace.ToString(),
            status = "failed"
        };
        var results = new List<CaseResult>();
        RunCase(results, "遮罩R切换主色与阴影色", TestRedMask);
        RunCase(results, "G通道伪高光及Alpha保留", TestHighlightAndAlpha);
        RunCase(results, "Uma/Face不叠加G通道", TestFaceNoHighlight);
        RunCase(results, "Base.B裁剪Alpha", TestClip);
        RunCase(results, "不同遮罩尺寸与UV偏移", TestMaskSizeAndOffset);
        RunCase(results, "主图UV变换及纵向保持", TestMainTransform);
        RunCase(results, "线性颜色输入转PNG编码", TestLinearColorInput);
        RunCase(results, "Renderer与材质级属性块优先级", TestPropertyBlocks);
        RunCase(results, "同名不同输入与重复PNG哈希", TestContentHashes);
        RunCase(results, "不可读纹理GPU采样", TestNonReadable);
        RunCase(results, "RT与sRGB写入状态恢复及源材质不变", TestStateAndSource);
        RunCase(results, "ReadParts绑定烘焙图且面数不变", TestContextBinding);
        RunCase(results, "缺少遮罩时保留原主贴图", TestMissingMaskFallback);
        _lastReport.cases = results.ToArray();
        _lastReport.status = results.All(item => item.status == "passed") ? "passed" : "failed";
        return _lastReport;
    }

    private static void Run(bool exitEditor)
    {
        try { Assert(); }
        catch (Exception exception)
        {
            if (_lastReport == null) _lastReport = new BatchReport();
            _lastReport.error = exception.ToString();
            if (_lastReport.cases == null) _lastReport.status = "failed";
        }
        try
        {
            string path = OutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(_lastReport, true));
            Debug.Log("PMX 材质GPU回归：" + _lastReport.status + "；报告：" + OutputRelativePath);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (_lastReport != null) _lastReport.status = "failed";
        }
        if (exitEditor) EditorApplication.Exit(_lastReport != null && _lastReport.status == "passed" ? 0 : 1);
    }

    private static void RunCase(List<CaseResult> results, string name, Action action)
    {
        try
        {
            action();
            results.Add(new CaseResult { name = name, status = "passed", details = "断言完成。" });
        }
        catch (Exception exception)
        {
            results.Add(new CaseResult { name = name, status = "failed", details = exception.ToString() });
        }
        finally
        {
            foreach (UnityEngine.Object resource in TemporaryObjects)
                if (resource != null) UnityEngine.Object.DestroyImmediate(resource);
            TemporaryObjects.Clear();
        }
    }

    private static void TestRedMask()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(204, 153, 102, 255), false),
                Solid(4, 4, C32(32, 64, 96, 255), false),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(204, 153, 102, 255), 3);
            f.Material.SetTexture("_BaseTex", SolidKeep(f, 4, 4, C32(0, 0, 255, 255), true));
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(32, 64, 96, 255), 3);
        }
    }

    private static void TestHighlightAndAlpha()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(51, 102, 153, 64), false),
                Solid(4, 4, C32(15, 30, 45, 255), false),
                Solid(4, 4, C32(255, 128, 255, 255), true));
            Texture2D baked = Bake(f.Renderer, 0, f.Material);
            AssertColor(baked, C32(153, 179, 204, 64), 4);
        }
    }

    private static void TestFaceNoHighlight()
    {
        using (Fixture f = NewFixture("Uma/Face"))
        {
            SetInputs(f.Material, Solid(4, 4, C32(51, 102, 153, 64), false),
                Solid(4, 4, C32(15, 30, 45, 255), false),
                Solid(4, 4, C32(255, 255, 255, 255), true));
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(51, 102, 153, 64), 4);
        }
    }

    private static void TestClip()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(120, 80, 40, 64), false),
                Solid(4, 4, C32(0, 0, 0, 255), false),
                Solid(4, 4, C32(255, 0, 0, 255), true));
            Texture2D baked = Bake(f.Renderer, 0, f.Material);
            if (baked.GetPixels32()[0].a != 0) Fail("Base.B=0 应裁为 alpha=0。");
        }
    }

    private static void TestMaskSizeAndOffset()
    {
        using (Fixture f = NewFixture())
        {
            Texture2D main = Solid(4, 4, C32(220, 40, 20, 255), false);
            Texture2D shade = Solid(4, 4, C32(20, 40, 220, 255), false);
            var pixels = new Color32[16];
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 8; x++)
                    pixels[y * 8 + x] = C32(x < 4 ? (byte)0 : (byte)255, 0, 255, 255);
            Texture2D mask = f.Keep(new Texture2D(8, 2, TextureFormat.RGBA32, false, true));
            mask.name = "different-size-linear-mask";
            mask.filterMode = FilterMode.Point;
            mask.wrapMode = TextureWrapMode.Repeat;
            mask.SetPixels32(pixels);
            mask.Apply(false, false);
            SetInputs(f.Material, main, shade, mask);
            f.Material.SetTextureOffset("_BaseTex", new Vector2(0.5f, 0));
            Texture2D baked = Bake(f.Renderer, 0, f.Material);
            AssertColor(baked, C32(220, 40, 20, 255), 4, 0);
            AssertColor(baked, C32(20, 40, 220, 255), 4, 3);
        }
    }

    private static void TestMainTransform()
    {
        using (Fixture f = NewFixture())
        {
            var main = f.Keep(new Texture2D(4, 2, TextureFormat.RGBA32, false, false));
            main.filterMode = FilterMode.Point;
            main.wrapMode = TextureWrapMode.Repeat;
            // 上下不同颜色，防止无意翻转或 Blit 丢失 ST。
            main.SetPixels32(new[] {
                C32(200, 20, 20, 255), C32(200, 20, 20, 255), C32(20, 200, 20, 255), C32(20, 200, 20, 255),
                C32(20, 20, 200, 255), C32(20, 20, 200, 255), C32(200, 200, 20, 255), C32(200, 200, 20, 255) });
            main.Apply(false, false);
            SetInputs(f.Material, main, Solid(4, 2, C32(0, 0, 0, 255), false),
                Solid(4, 2, C32(255, 0, 255, 255), true));
            f.Material.SetTextureOffset("_MainTex", new Vector2(0.5f, 0));
            Texture2D baked = Bake(f.Renderer, 0, f.Material);
            AssertColor(baked, C32(20, 200, 20, 255), 4, 0, 0);
            AssertColor(baked, C32(200, 200, 20, 255), 4, 0, 1);
            AssertColor(baked, C32(200, 20, 20, 255), 4, 3, 0);
        }
    }

    private static void TestLinearColorInput()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(64, 128, 192, 90), true),
                Solid(4, 4, C32(0, 0, 0, 255), true),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(137, 188, 225, 90), 4);
        }
    }

    private static void TestPropertyBlocks()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(51, 102, 153, 255), false),
                Solid(4, 4, C32(5, 10, 15, 255), false),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            var rendererBlock = new MaterialPropertyBlock();
            rendererBlock.SetTexture("_BaseTex", SolidKeep(f, 4, 4, C32(0, 0, 255, 255), true));
            f.Renderer.SetPropertyBlock(rendererBlock);
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(5, 10, 15, 255), 4);

            rendererBlock.SetTexture("_MainTex", SolidKeep(f, 4, 4, C32(0, 0, 0, 255), false));
            rendererBlock.SetTexture("_BaseTex", SolidKeep(f, 4, 4, C32(255, 0, 255, 255), true));
            f.Renderer.SetPropertyBlock(rendererBlock);
            var slotBlock = new MaterialPropertyBlock();
            slotBlock.SetTexture("_BaseTex", SolidKeep(f, 4, 4, C32(255, 0, 255, 255), true));
            f.Renderer.SetPropertyBlock(slotBlock, 0);
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(51, 102, 153, 255), 4);
        }
    }

    private static void TestContentHashes()
    {
        using (Fixture f = NewFixture())
        {
            Texture2D shade = Solid(4, 4, C32(1, 2, 3, 255), false);
            Texture2D mask = Solid(4, 4, C32(255, 0, 255, 255), true);
            Texture2D mainA = Solid(4, 4, C32(210, 30, 40, 255), false);
            Texture2D mainB = Solid(4, 4, C32(30, 210, 40, 255), false);
            mainA.name = mainB.name = "same-texture-name";
            f.Material.name = "same-material-name";
            SetInputs(f.Material, mainA, shade, mask);
            Texture2D first = Bake(f.Renderer, 0, f.Material);
            Texture2D repeated = Bake(f.Renderer, 0, f.Material);
            string firstHash = Hash(first.EncodeToPNG()), repeatedHash = Hash(repeated.EncodeToPNG());
            if (firstHash != repeatedHash) Fail("相同输入的PNG哈希必须稳定。");
            using (var other = NewFixture())
            {
                other.Material.name = "same-material-name";
                SetInputs(other.Material, mainB, shade, mask);
                string secondHash = Hash(Bake(other.Renderer, 0, other.Material).EncodeToPNG());
                if (firstHash == secondHash) Fail("同名材质的不同像素输入必须产生不同PNG。");
            }
        }
    }

    private static void TestNonReadable()
    {
        using (Fixture f = NewFixture())
        {
            Texture2D main = Solid(4, 4, C32(170, 90, 40, 255), false);
            main.Apply(false, true);
            SetInputs(f.Material, main, Solid(4, 4, C32(0, 0, 0, 255), false),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            AssertColor(Bake(f.Renderer, 0, f.Material), C32(170, 90, 40, 255), 4);
        }
    }

    private static void TestStateAndSource()
    {
        using (Fixture f = NewFixture())
        {
            Texture2D main = Solid(4, 4, C32(110, 130, 150, 200), false);
            SetInputs(f.Material, main, Solid(4, 4, C32(10, 30, 50, 255), false),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            f.Material.SetTextureScale("_MainTex", new Vector2(0.75f, 0.5f));
            f.Material.SetTextureOffset("_MainTex", new Vector2(0.125f, 0.25f));
            int mainId = f.Material.GetTexture("_MainTex").GetInstanceID();
            Vector2 scale = f.Material.GetTextureScale("_MainTex");
            Vector2 offset = f.Material.GetTextureOffset("_MainTex");
            RenderTexture oldActive = RenderTexture.active;
            bool oldSrgb = GL.sRGBWrite;
            var sentinel = f.Keep(new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32));
            sentinel.Create();
            try
            {
                RenderTexture.active = sentinel;
                GL.sRGBWrite = !oldSrgb;
                Bake(f.Renderer, 0, f.Material);
                if (RenderTexture.active != sentinel) Fail("RenderTexture.active 未恢复。");
                if (GL.sRGBWrite != !oldSrgb) Fail("Bake 未恢复进入时的 sRGB 写入状态。");
                if (f.Material.GetTexture("_MainTex").GetInstanceID() != mainId ||
                    f.Material.GetTextureScale("_MainTex") != scale ||
                    f.Material.GetTextureOffset("_MainTex") != offset)
                    Fail("烘焙改变了源材质贴图或 ST。");
            }
            finally
            {
                RenderTexture.active = oldActive;
                GL.sRGBWrite = oldSrgb;
            }
        }
    }

    private static void TestContextBinding()
    {
        using (Fixture f = NewFixture())
        {
            SetInputs(f.Material, Solid(4, 4, C32(80, 120, 160, 255), false),
                Solid(4, 4, C32(0, 0, 0, 255), false),
                Solid(4, 4, C32(255, 0, 255, 255), true));
            using (PMXMeshExportContext context = PMXMeshExportContext.Capture(new Renderer[] { f.Renderer }, f.Root.transform))
            {
                RawMMDModel model = NewModel();
                int sourceIndexCount = f.Mesh.triangles.Length;
                Part[] parts = context.ReadParts(model, OutputDirectory());
                if (context.Diagnostics.Count != 1 || context.Diagnostics[0].submeshes.Count != 1)
                    Fail("夹具网格未完整进入导出上下文。");
                var audit = context.Diagnostics[0].submeshes[0];
                if (audit.textureBake == null || audit.textureBake.Status != "baked")
                    Fail("诊断未报告成功烘焙：" + audit.textureBake?.Status + " " + audit.textureBake?.Reason);
                if (model.TriangleIndexes.Length != sourceIndexCount || audit.sourceIndexCount != audit.exportedIndexCount)
                    Fail("材质烘焙改变了三角面索引数量。");
                if (parts.Length != 1 || parts[0].Material.Texture == null ||
                    parts[0].Material.Texture.TexturePath != audit.textureBake.TexturePath)
                    Fail("PMX Part 未绑定新烘焙贴图。");
                string pngPath = Path.Combine(OutputDirectory(), audit.textureBake.TexturePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(pngPath)) Fail("内容寻址PNG未写到导出目录。");
                string pngHash = Hash(File.ReadAllBytes(pngPath));
                if (pngHash != Path.GetFileNameWithoutExtension(pngPath).Replace("pmx_material_", ""))
                    Fail("导出PNG哈希与文件名不一致。");
            }
        }
    }

    private static void TestMissingMaskFallback()
    {
        using (Fixture f = NewFixture())
        {
            Texture2D main = Solid(4, 4, C32(90, 120, 180, 255), false);
            SetInputs(f.Material, main, Solid(4, 4, C32(0, 0, 0, 255), false), null);
            string originalPath = "Texture2D/" + main.name + ".png";
            using (PMXMeshExportContext context = PMXMeshExportContext.Capture(new Renderer[] { f.Renderer }, f.Root.transform))
            {
                RawMMDModel model = NewModel();
                model.TextureList.Add(new MMDTexture(originalPath));
                Part[] parts = context.ReadParts(model, OutputDirectory());
                var audit = context.Diagnostics[0].submeshes[0];
                if (audit.textureBake == null || audit.textureBake.Status == "baked")
                    Fail("缺少遮罩时不能报告已烘焙。");
                if (parts.Length != 1 || parts[0].Material.Texture == null || parts[0].Material.Texture.TexturePath != originalPath)
                    Fail("缺少遮罩时应继续引用原主贴图。");
            }
        }
    }

    private static Fixture NewFixture(string shaderName = "Uma/Base")
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null) Fail("找不到 GPU 夹具 Shader：" + shaderName);
        var f = new Fixture();
        f.Root = f.Keep(new GameObject("PMX bake regression"));
        f.Mesh = f.Keep(new Mesh { name = "PMX bake quad" });
        f.Mesh.vertices = new[]
        {
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
            new Vector3(1, 1, 0), new Vector3(-1, 1, 0)
        };
        f.Mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
        f.Mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        f.Mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        f.Root.AddComponent<MeshFilter>().sharedMesh = f.Mesh;
        f.Renderer = f.Root.AddComponent<MeshRenderer>();
        f.Material = f.Keep(new Material(shader) { name = "PMX bake fixture material" });
        f.Renderer.sharedMaterial = f.Material;
        return f;
    }

    private static void SetInputs(Material material, Texture2D main, Texture2D shade, Texture2D mask)
    {
        material.SetTexture("_MainTex", main);
        material.SetTexture("_ShadTex", shade);
        material.SetTexture("_BaseTex", mask);
        material.SetColor("_CharaColor", Color.white);
    }

    private static Texture2D Solid(int width, int height, Color32 color, bool linear)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear)
        {
            name = "PMX bake fixture texture",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        TemporaryObjects.Add(texture);
        texture.SetPixels32(Enumerable.Repeat(color, width * height).ToArray());
        texture.Apply(false, false);
        return texture;
    }

    private static Texture2D SolidKeep(Fixture fixture, int width, int height, Color32 color, bool linear)
        => fixture.Keep(Solid(width, height, color, linear));

    private static Texture2D Bake(Renderer renderer, int slot, Material material)
    {
        Texture2D texture = PMXMaterialTextureExporter.Bake(renderer, slot, material);
        TemporaryObjects.Add(texture);
        return texture;
    }

    private static Color32 C32(byte r, byte g, byte b, byte a) => new Color32(r, g, b, a);

    private static void AssertColor(Texture2D texture, Color32 expected, int tolerance, int x = 0, int y = 0)
    {
        Color32 actual = texture.GetPixels32()[y * texture.width + x];
        if (Math.Abs(actual.r - expected.r) > tolerance || Math.Abs(actual.g - expected.g) > tolerance ||
            Math.Abs(actual.b - expected.b) > tolerance || Math.Abs(actual.a - expected.a) > tolerance)
            Fail("像素不符，实际=(" + actual.r + "," + actual.g + "," + actual.b + "," + actual.a +
                ")，预期=(" + expected.r + "," + expected.g + "," + expected.b + "," + expected.a + ")。");
    }

    private static string Hash(byte[] bytes)
    {
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private static RawMMDModel NewModel() => new RawMMDModel
    {
        Name = "PMX texture fixture", NameEn = "PMX texture fixture", Description = "", DescriptionEn = "",
        Vertices = Array.Empty<Vertex>(), ExtraUvNumber = 0, TriangleIndexes = Array.Empty<int>(), Parts = Array.Empty<Part>(),
        Bones = Array.Empty<Bone>(), Morphs = Array.Empty<Morph>(), Rigidbodies = Array.Empty<MMDRigidBody>(),
        Joints = Array.Empty<MMDJoint>()
    };

    private static string OutputDirectory()
        => Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            "Logs", "pmx-pseudo-highlight-plan-20261006", "gpu-regression-output");

    private static string OutputPath()
        => Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void Fail(string message) => throw new InvalidOperationException(message);
}

