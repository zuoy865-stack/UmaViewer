using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>把眼睛主色和当前高光烘到 UV0，供 PMX 漫反射材质引用。</summary>
public static class PMXEyeTextureExporter
{
    public static bool TryExport(Renderer renderer, Mesh mesh, int submesh, Material material,
        string directory, out string texturePath)
    {
        texturePath = null;
        if (string.IsNullOrEmpty(directory) || material == null || material.shader == null) return false;
        string shader = material.shader.name;
        if (shader != "Gallop/3D/Chara/ToonEye/T" && shader != "Uma/Eye" && shader != "Nars/UmaMusume/Eyes")
            return false;

        Texture2D result = null;
        try
        {
            result = Bake(renderer, mesh, submesh, material);
            byte[] png = result.EncodeToPNG();
            string digest;
            using (SHA256 hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(png)).Replace("-", "").Substring(0, 16);
            // 内容命名避免同名材质、不同参数和重复导出互相覆盖。
            texturePath = "Texture2D/pmx_eye_" + digest + ".png";
            Directory.CreateDirectory(Path.Combine(directory, "Texture2D"));
            File.WriteAllBytes(Path.Combine(directory, texturePath), png);
            return true;
        }
        catch (Exception exception)
        {
            texturePath = null;
            Debug.LogWarning($"PMX 眼睛高光未合成：{material.name}；保留主纹理。{exception.Message}");
            return false;
        }
        finally
        {
            DestroyTemporary(result);
        }
    }

    public static Texture2D Bake(Renderer renderer, Mesh mesh, int submesh, Material material)
    {
        var inputs = new Inputs(renderer, submesh, material);
        Vector2[] uv0 = mesh.uv;
        if (uv0.Length != mesh.vertexCount) throw new InvalidOperationException("缺少眼睛 UV0。");
        SampleVertex[] samples = inputs.MapVertices(mesh);
        int width = inputs.Main.Width, height = inputs.Main.Height;
        Color[] colors = new Color[width * height];
        bool[] covered = new bool[colors.Length];
        int[] triangles = mesh.GetTriangles(submesh);

        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            Vector2 p0 = uv0[a], p1 = uv0[b], p2 = uv0[c];
            float area = Cross(p1 - p0, p2 - p0);
            if (Mathf.Abs(area) < 0.00000001f) continue;
            if (!InsideAtlas(p0) || !InsideAtlas(p1) || !InsideAtlas(p2))
                throw new InvalidOperationException("UV0 超出单张图集范围，不能安全合成。");
            int x0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)) * width), 0, width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)) * width), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) * height), 0, height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)) * height), 0, height - 1);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / width, (y + 0.5f) / height);
                float w1 = Cross(p - p0, p2 - p0) / area;
                float w2 = Cross(p1 - p0, p - p0) / area;
                float w0 = 1 - w1 - w2;
                if (w0 < -0.00001f || w1 < -0.00001f || w2 < -0.00001f) continue;
                Color color = inputs.Composite(SampleVertex.Interpolate(samples[a], samples[b], samples[c], w0, w1, w2));
                if (QualitySettings.activeColorSpace == ColorSpace.Linear) color = color.gamma;
                int pixel = y * width + x;
                if (covered[pixel] && ColorDifference(colors[pixel], color) > 0.01f)
                    throw new InvalidOperationException("重叠 UV0 对应不同眼睛颜色，不能合到同一贴图。");
                covered[pixel] = true;
                colors[pixel] = color;
            }
        }
        if (Array.IndexOf(covered, true) < 0) throw new InvalidOperationException("眼部面片没有可烘焙像素。");
        PadEdges(colors, covered, width, height);
        // PNG 不翻转；PMX 顶点读取处已经执行 1-v。
        var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        result.SetPixels(colors);
        result.Apply();
        return result;
    }

    private sealed class Inputs
    {
        internal readonly PMXEyeTextureSampler Main;
        private readonly PMXEyeTextureSampler[] _high = new PMXEyeTextureSampler[3];
        private readonly PMXEyeTextureSampler _mask;
        private readonly Material _material;
        private readonly MaterialPropertyBlock _block;
        private readonly string _shader;
        private readonly Vector4[] _mainParams, _highParams1, _highParams2;
        private readonly Vector4 _mainST, _highST, _offset;
        private readonly float _limit;
        private readonly bool _maskColor, _narsHighlights, _narsOne, _narsSwap;
        private readonly int _style;
        private readonly float _brightness, _show0, _show1;
        private readonly Color[] _maskTints;

        internal Inputs(Renderer renderer, int submesh, Material material)
        {
            _material = material;
            _shader = material.shader.name;
            _block = new MaterialPropertyBlock();
            if (renderer != null)
            {
                int materialIndex = Mathf.Min(submesh, renderer.sharedMaterials.Length - 1);
                if (materialIndex >= 0) renderer.GetPropertyBlock(_block, materialIndex);
                // Unity 的材质级 block 非空时替代 Renderer 级 block。
                if (_block.isEmpty) renderer.GetPropertyBlock(_block);
            }
            Main = new PMXEyeTextureSampler(Texture("_MainTex"));
            _mainST = TextureST("_MainTex");
            _highST = TextureST("_High0Tex");
            _offset = Vector("_Offset");
            _limit = Float("_Limit", 0.5f);
            _maskColor = material.IsKeywordEnabled("USE_MASK_COLOR");
            if (_shader == "Gallop/3D/Chara/ToonEye/T")
            {
                _mainParams = Array("_MainParam");
                _highParams1 = Array("_HighParam1");
                _highParams2 = Array("_HighParam2");
                if (_mainParams.Length < 2 || _highParams1.Length < 3 || _highParams2.Length < 2)
                    throw new InvalidOperationException("缺少 ToonEye 的 MainParam/HighParam，不能猜测高光状态。");
                for (int layer = 0; layer < 3; layer++) _high[layer] = OptionalTexture("_High" + layer + "Tex");
                if (_maskColor)
                {
                    _mask = new PMXEyeTextureSampler(Texture("_MaskColorTex"));
                    string[] names = { "_MaskColorR1", "_MaskColorR2", "_MaskColorG1", "_MaskColorG2", "_MaskColorB1", "_MaskColorB2" };
                    _maskTints = new Color[names.Length];
                    for (int i = 0; i < names.Length; i++)
                        _maskTints[i] = _block.HasProperty(names[i]) ? _block.GetColor(names[i]) : material.GetColor(names[i]);
                }
            }
            else if (_shader == "Uma/Eye")
            {
                _high[0] = OptionalTexture("_Highlight00");
                _high[1] = OptionalTexture("_Highlight01");
                _style = Mathf.RoundToInt(Float("_EyeSelect", 0));
                _show0 = Float("_Show00", 0.35f);
                _show1 = Float("_Show01", 0.35f);
            }
            else
            {
                _high[0] = OptionalTexture("_High0Tex");
                _high[1] = OptionalTexture("_High1Tex");
                _narsHighlights = !material.IsKeywordEnabled("_HASHIGHLIGHT_NO");
                _narsOne = material.IsKeywordEnabled("_NUMBEROFHIGHLIGHTS_ONE");
                _narsSwap = Float("_Switch1and2", 0) != 0;
                _brightness = Float("_HighLightBrightness", 1);
                // shader 的无关键字分支也使用 Eye1 和两个高光。
                for (int i = 1; i < 4; i++)
                    if (material.IsKeywordEnabled("_STYLES_EYE" + (i + 1))) { _style = i; break; }
            }
        }

        internal SampleVertex[] MapVertices(Mesh mesh)
        {
            Vector2[] uv0 = mesh.uv, uv1 = mesh.uv2, uv2 = mesh.uv3;
            bool gallop = _shader == "Gallop/3D/Chara/ToonEye/T";
            if (gallop && (uv1.Length != uv0.Length || uv2.Length != uv0.Length))
                throw new InvalidOperationException("ToonEye 缺少独立高光 UV1/UV2。");
            var samples = new SampleVertex[uv0.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = new SampleVertex();
                if (gallop)
                {
                    bool upper = uv0[i].y >= 0.5f;
                    Vector4 p0 = upper ? _highParams1[0] : _highParams2[0];
                    Vector4 p1 = upper ? _highParams1[1] : _highParams2[1];
                    sample.Main = TransformUV(RotateUV(uv0[i], _mainParams[upper ? 0 : 1]), _mainST);
                    Vector2 offset = upper ? new Vector2(_offset.x, _offset.y) : new Vector2(_offset.z, _offset.w);
                    sample.High0 = TransformUV(RotateUV(uv1[i], p0) + offset, _highST);
                    sample.High1 = TransformUV(RotateUV(uv1[i], p1), _highST);
                    sample.High2 = TransformUV(RotateUV(uv2[i], _highParams1[2]), _highST);
                    sample.Strength = new Vector3(p0.w, p1.w, _highParams1[2].w);
                }
                else if (_shader == "Uma/Eye")
                {
                    sample.Main = TransformUV(uv0[i] + new Vector2(_style * 0.25f, 0), _mainST);
                    Vector2 uv = Vector2.Scale(uv0[i], new Vector2(4, 2));
                    sample.High0 = TransformUV(uv, TextureST("_Highlight00"));
                    sample.High1 = TransformUV(uv, TextureST("_Highlight01"));
                }
                else
                {
                    Vector2 uv = TransformUV(uv0[i], TextureST("_texcoord"));
                    sample.Main = new Vector2(uv.x * 0.25f + _style * 0.25f, uv.y);
                    sample.High0 = sample.High1 = new Vector2(uv.x, uv.y * 2);
                }
                samples[i] = sample;
            }
            return samples;
        }

        internal Color Composite(SampleVertex input)
        {
            Color color = Main.Sample(input.Main);
            if (_shader == "Gallop/3D/Chara/ToonEye/T")
            {
                if (_maskColor) color = ApplyMask(color, _mask.Sample(input.Main));
                float highlight = Binary(_high[0], input.High0, input.Strength.x) +
                    Binary(_high[1], input.High1, input.Strength.y) + Binary(_high[2], input.High2, input.Strength.z);
                color.r += highlight; color.g += highlight; color.b += highlight;
            }
            else if (_shader == "Uma/Eye")
            {
                color += Step(_high[0], input.High0, _show0) + Step(_high[1], input.High1, _show1);
            }
            else if (_narsHighlights)
            {
                Color h0 = _high[0]?.Sample(input.High0) ?? Color.clear;
                Color h1 = _high[1]?.Sample(input.High1) ?? Color.clear;
                Color mask = _narsOne ? (_narsSwap ? h1 : h0) : h0 + h1;
                color.r += (_brightness - color.r) * mask.r;
                color.g += (_brightness - color.g) * mask.g;
                color.b += (_brightness - color.b) * mask.b;
            }
            if (_shader == "Nars/UmaMusume/Eyes") color.a = 1; // 无高光时也不透明。
            return new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), Mathf.Clamp01(color.a));
        }

        private float Binary(PMXEyeTextureSampler texture, Vector2 uv, float strength)
            => texture != null && texture.Sample(uv).r * strength > _limit ? 1 : 0;

        private static Color Step(PMXEyeTextureSampler texture, Vector2 uv, float threshold)
        {
            if (texture == null) return Color.clear;
            Color c = texture.Sample(uv);
            return new Color(c.r >= threshold ? 1 : 0, c.g >= threshold ? 1 : 0,
                c.b >= threshold ? 1 : 0, c.a >= threshold ? 1 : 0);
        }

        private Color ApplyMask(Color color, Color mask)
        {
            for (int i = 0; i < _maskTints.Length; i++)
            {
                float value = i < 2 ? mask.r : i < 4 ? mask.g : mask.b;
                float weight = i % 2 == 0 ? (0.49f - Mathf.Min(value, 0.49f)) / 0.49f : (Mathf.Max(value, 0.51f) - 0.51f) / 0.49f;
                Color tint = _maskTints[i];
                color.r *= 1 + weight * (tint.r - 1);
                color.g *= 1 + weight * (tint.g - 1);
                color.b *= 1 + weight * (tint.b - 1);
            }
            return color;
        }

        private Texture2D Texture(string name)
        {
            Texture texture = _block.HasProperty(name) ? _block.GetTexture(name) : _material.GetTexture(name);
            if (!(texture is Texture2D value)) throw new InvalidOperationException(name + " 不是 Texture2D。");
            return value;
        }

        private PMXEyeTextureSampler OptionalTexture(string name)
        {
            Texture texture = _block.HasProperty(name) ? _block.GetTexture(name) : _material.GetTexture(name);
            // 与 shader 的默认采样值一致，Nars 未绑定高光时默认白色。
            if (texture == null) texture = _shader == "Nars/UmaMusume/Eyes" ? Texture2D.whiteTexture : Texture2D.blackTexture;
            if (!(texture is Texture2D value)) throw new InvalidOperationException(name + " 不是 Texture2D。");
            return new PMXEyeTextureSampler(value);
        }

        private float Float(string name, float fallback)
            => _block.HasProperty(name) ? _block.GetFloat(name) : _material.HasProperty(name) ? _material.GetFloat(name) : fallback;

        private Vector4 Vector(string name)
            => _block.HasProperty(name) ? _block.GetVector(name) : _material.HasProperty(name) ? _material.GetVector(name) : Vector4.zero;

        private Vector4 TextureST(string name)
        {
            if (_block.HasProperty(name + "_ST")) return _block.GetVector(name + "_ST");
            if (!_material.HasProperty(name)) return new Vector4(1, 1, 0, 0);
            Vector2 scale = _material.GetTextureScale(name), offset = _material.GetTextureOffset(name);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        private Vector4[] Array(string name)
        {
            Vector4[] values = _block.HasProperty(name) ? _block.GetVectorArray(name) : _material.GetVectorArray(name);
            if (values == null || values.Length == 0) values = Shader.GetGlobalVectorArray(name);
            return values ?? System.Array.Empty<Vector4>();
        }
    }

    private struct SampleVertex
    {
        internal Vector2 Main, High0, High1, High2;
        internal Vector3 Strength;
        internal static SampleVertex Interpolate(SampleVertex a, SampleVertex b, SampleVertex c, float x, float y, float z)
            => new SampleVertex { Main = a.Main * x + b.Main * y + c.Main * z,
                High0 = a.High0 * x + b.High0 * y + c.High0 * z,
                High1 = a.High1 * x + b.High1 * y + c.High1 * z,
                High2 = a.High2 * x + b.High2 * y + c.High2 * z,
                Strength = a.Strength * x + b.Strength * y + c.Strength * z };
    }

    private static Vector2 RotateUV(Vector2 uv, Vector4 parameter)
    {
        // 与 ToonEye 顶点程序相同：交换轴、偏移、旋转，再恢复中心。
        Vector2 v = new Vector2(uv.y - 0.5f + parameter.y, uv.x - 0.5f + parameter.x);
        float s = Mathf.Sin(parameter.z), c = Mathf.Cos(parameter.z);
        return new Vector2(v.y * c - v.x * s, v.x * c + v.y * s) + new Vector2(0.5f, 0.5f);
    }

    private static Vector2 TransformUV(Vector2 uv, Vector4 st)
        => new Vector2(uv.x * st.x + st.z, uv.y * st.y + st.w);
    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    private static bool InsideAtlas(Vector2 uv) => uv.x >= 0 && uv.x <= 1 && uv.y >= 0 && uv.y <= 1;
    private static float ColorDifference(Color a, Color b)
        => Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)), Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a)));

    private static void PadEdges(Color[] colors, bool[] covered, int width, int height)
    {
        // 图集边缘扩两像素，避免双线性采样出现黑边。
        for (int pass = 0; pass < 2; pass++)
        {
            bool[] old = (bool[])covered.Clone();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (old[i]) continue;
                int other = x > 0 && old[i - 1] ? i - 1 : x + 1 < width && old[i + 1] ? i + 1 :
                    y > 0 && old[i - width] ? i - width : y + 1 < height && old[i + width] ? i + width : -1;
                if (other < 0) continue;
                covered[i] = true; colors[i] = colors[other];
            }
        }
    }

    internal static void DestroyTemporary(UnityEngine.Object resource)
    {
        if (resource == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
        else UnityEngine.Object.DestroyImmediate(resource);
    }
}
