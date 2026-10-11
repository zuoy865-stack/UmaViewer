using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibMMD.Model;
using LibMMD.Reader;
using UnityEditor;
using UnityEngine;

/// <summary>为伪高光 PMX 生成固定视角的无光照贴图预览。</summary>
public static class PMXMaterialPmxPreview
{
    private const string Baseline = "Logs/pmx-pseudo-highlight-plan-20261006/baseline-export/teio_1003_00.pmx";
    private const string Reference = @"C:/Users/JuziD/Pictures/PMX/东海帝王决胜服by_SuGaR小塘/Tokai Teio.pmx";
    private const string Output = "Logs/pmx-pseudo-highlight-plan-20261006/preview";
    private static readonly List<UnityEngine.Object> Temporary = new List<UnityEngine.Object>();

    public static void RunBatch() => RunBatch(Environment.GetCommandLineArgs());

    public static void RunBatch(string[] args)
    {
        string output = Path.GetFullPath(Output);
        int exitCode = 1;
        RenderTexture previous = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        try
        {
            string fresh = Argument(args, "-pmxBakePreviewNew");
            string baseline = Path.GetFullPath(Baseline);
            string reference = Path.GetFullPath(Reference);
            if (!File.Exists(fresh) || !File.Exists(baseline) || !File.Exists(reference))
                throw new FileNotFoundException("新 PMX、固定 baseline 或只读 reference 缺失。", fresh);
            if (new[] { fresh, baseline, reference }.Any(path => new FileInfo(path).Length == 0))
                throw new InvalidDataException("输入 PMX 文件为空。");
            if (Directory.Exists(output) && Directory.GetFiles(output).Length > 0)
                throw new IOException("预览目录非空，为保护已有结果而停止：" + output);
            Directory.CreateDirectory(output);
            ModelView[] models = { Load("baseline", baseline), Load("reference", reference), Load("new", fresh) };
            int diffuseCount = 0;
            foreach (ModelView model in models) diffuseCount += model.TextureCount;
            if (diffuseCount == 0) throw new InvalidDataException("三个 PMX 均未读取到有效 diffuse PNG。");
            string[] names = { "front", "side", "back" };
            Vector3[] directions = { Vector3.forward, Vector3.right, Vector3.back };
            for (int view = 0; view < names.Length; view++)
                for (int model = 0; model < models.Length; model++)
                    Render(models[model], models, directions[view], Path.Combine(output, models[model].Name + "_" + names[view] + ".png"));
            File.WriteAllText(Path.Combine(output, "report.json"),
                "{\n  \"status\": \"passed\",\n  \"baseline\": \"" + Escape(baseline) + "\",\n  \"reference\": \"" + Escape(reference) + "\",\n  \"newPmx\": \"" + Escape(fresh) + "\",\n  \"diffusePngCount\": " + diffuseCount +",\n  \"views\": [\"front\", \"side\", \"back\"],\n  \"evidence\": \"Unity GPU unlit diffuse-texture preview; original mesh UV uses (u, 1-v). No lighting, MMD physics, or dynamic pose acceptance.\"\n}\n", Encoding.UTF8);
            exitCode = 0;
        }
        catch (Exception e)
        {
            try
            {
                Directory.CreateDirectory(output);
                string report = Path.Combine(output, File.Exists(Path.Combine(output, "report.json")) ? "report.failure.json" : "report.json");
                File.WriteAllText(report, "{\n  \"status\": \"failed\",\n  \"error\": \"" + Escape(e.ToString()) + "\"\n}\n", Encoding.UTF8);
            }
            catch (Exception reportError) { Debug.LogError("预览失败报告也无法写入：" + reportError); }
            Debug.LogException(e);
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSrgbWrite;
            for (int i = Temporary.Count - 1; i >= 0; i--)
                if (Temporary[i] != null) UnityEngine.Object.DestroyImmediate(Temporary[i]);
            Temporary.Clear();
            EditorApplication.Exit(exitCode);
        }
    }

    private sealed class ModelView
    {
        public string Name;
        public Mesh Mesh;
        public Material[] Materials;
        public int TextureCount;
        public Bounds Bounds;
    }

    private static ModelView Load(string name, string path)
    {
        RawMMDModel model = new PMXReader().Read(path, new ModelConfig());
        if (model.Vertices == null || model.Vertices.Length == 0 || model.TriangleIndexes == null || model.TriangleIndexes.Length == 0)
            throw new InvalidDataException("PMX 没有可渲染的原始网格：" + path);
        var result = new ModelView { Name = name };
        result.Mesh = new Mesh { name = name + " original PMX mesh", indexFormat = model.Vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        Temporary.Add(result.Mesh);
        var vertices = new Vector3[model.Vertices.Length];
        var normals = new Vector3[vertices.Length];
        var uv = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = model.Vertices[i].Coordinate;
            normals[i] = model.Vertices[i].Normal;
            Vector2 source = model.Vertices[i].UvCoordinate;
            uv[i] = new Vector2(source.x, 1f - source.y);
        }
        result.Mesh.vertices = vertices;
        result.Mesh.normals = normals;
        result.Mesh.uv = uv;
        var materials = new List<Material>();
        result.Mesh.subMeshCount = model.Parts.Length;
        foreach (var part in model.Parts)
        {
            int[] indices = new int[part.TriangleIndexNum];
            Array.Copy(model.TriangleIndexes, part.BaseShift, indices, 0, indices.Length);
            result.Mesh.SetTriangles(indices, materials.Count, false);
            var material = new Material(FindUnlitShader()) { name = name + " " + materials.Count };
            material.color = part.Material == null ? Color.white : part.Material.DiffuseColor;
            if (part.Material != null && part.Material.Texture != null && !string.IsNullOrWhiteSpace(part.Material.Texture.TexturePath))
            {
                string texturePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), part.Material.Texture.TexturePath));
                if (!File.Exists(texturePath)) throw new FileNotFoundException("PMX diffuse 贴图缺失：" + part.Material.Texture.TexturePath, texturePath);
                byte[] bytes = File.ReadAllBytes(texturePath);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = Path.GetFileName(texturePath) };
                Temporary.Add(texture);
                if (bytes.Length == 0 || !ImageConversion.LoadImage(texture, bytes, false)) throw new InvalidDataException("diffuse PNG 读取失败：" + texturePath);
                texture.wrapMode = TextureWrapMode.Repeat;
                material.SetTexture("_MainTex", texture);
                result.TextureCount++;
            }
            materials.Add(material);
            Temporary.Add(material);
        }
        result.Mesh.RecalculateBounds();
        result.Bounds = result.Mesh.bounds;
        result.Materials = materials.ToArray();
        return result;
    }

    private static Shader FindUnlitShader()
    {
        Shader shader = Shader.Find("Hidden/UmaViewer/PMXPreviewUnlit");
        if (shader == null) throw new InvalidOperationException("找不到可用的 Unity Unlit shader。");
        return shader;
    }

    private static void Render(ModelView selected, ModelView[] pair, Vector3 direction, string path)
    {
        Bounds union = pair[0].Bounds;
        for (int i = 1; i < pair.Length; i++) union.Encapsulate(pair[i].Bounds);
        Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
        if (right.sqrMagnitude < 0.5f) right = Vector3.right;
        float width = 0f, height = 0f;
        foreach (Vector3 corner in Corners(union))
        {
            Vector3 delta = corner - union.center;
            width = Mathf.Max(width, Mathf.Abs(Vector3.Dot(delta, right)) * 2f);
            height = Mathf.Max(height, Mathf.Abs(delta.y) * 2f);
        }
        var cameraObject = new GameObject("PMX preview camera");
        Temporary.Add(cameraObject);
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = Mathf.Max(height, width) * 0.56f;
        camera.aspect = 1f;
        camera.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 1f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = Mathf.Max(1000f, union.size.magnitude * 4f);
        camera.transform.position = union.center + direction * (union.size.magnitude * 2f + 1f);
        camera.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
        var target = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
        Temporary.Add(target);
        target.Create();
        RenderTexture.active = target;
        GL.sRGBWrite = target.sRGB;
        GL.Clear(true, true, camera.backgroundColor);
        GL.PushMatrix();
        try
        {
            GL.LoadProjectionMatrix(camera.projectionMatrix);
            GL.modelview = camera.worldToCameraMatrix;
            for (int slot = 0; slot < selected.Materials.Length; slot++)
            {
                Material material = selected.Materials[slot];
                if (!material.SetPass(0)) throw new InvalidOperationException("PMX preview shader pass 无法启用。");
                Graphics.DrawMeshNow(selected.Mesh, Matrix4x4.identity, slot);
            }
        }
        finally { GL.PopMatrix(); }
        var image = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, false);
        Temporary.Add(image);
        image.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
        image.Apply(false, false);
        Color32 background = (Color32)camera.backgroundColor;
        int foreground = 0;
        foreach (Color32 pixel in image.GetPixels32())
            if (Mathf.Abs(pixel.r - background.r) + Mathf.Abs(pixel.g - background.g) + Mathf.Abs(pixel.b - background.b) > 18) foreground++;
        if (foreground < 100) throw new IOException("GPU 渲染结果没有可见模型像素：" + path);
        byte[] png = image.EncodeToPNG();
        if (png == null || png.Length == 0) throw new IOException("GPU 预览导出空图：" + path);
        File.WriteAllBytes(path, png);
    }

    private static IEnumerable<Vector3> Corners(Bounds bounds)
    {
        Vector3 min = bounds.min, max = bounds.max;
        for (int x = 0; x < 2; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            yield return new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
    }

    private static string Argument(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException(key + " 后必须跟新 PMX 路径。");
                return Path.GetFullPath(args[i + 1]);
            }
        throw new ArgumentException("缺少命令行参数 " + key + " <newpmx>。");
    }

    private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}
