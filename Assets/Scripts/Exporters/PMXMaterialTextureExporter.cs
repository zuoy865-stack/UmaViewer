using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>通过 GPU 将已校准的材质结果烘焙为 PMX 贴图。</summary>
public static class PMXMaterialTextureExporter
{
    public sealed class Result
    {
        public string Status, Reason, TexturePath;
        public object Inputs;
    }

    internal static Result TryExport(Renderer renderer, int slot, Material source, string directory,
        Dictionary<string, Result> cache)
    {
        if (string.IsNullOrEmpty(directory))
            return new Result { Status = "not_requested", Reason = "未请求贴图导出。" };
        if (source == null)
            return new Result { Status = "failed", Reason = "源材质为空。" };

        Texture2D baked = null;
        try
        {
            if (!PMXMaterialBakeProfile.TryCreate(renderer, slot, source, out PMXMaterialBakeProfile profile,
                    out string status, out string reason))
                return new Result { Status = status, Reason = reason };

            if (profile == null || profile.Main == null)
                throw new InvalidOperationException("材质烘焙配置缺少主贴图。");
            if (cache != null && !string.IsNullOrEmpty(profile.CacheKey) &&
                cache.TryGetValue(profile.CacheKey, out Result cached) && cached != null && cached.Status == "baked")
                return cached;

            baked = Bake(profile);
            byte[] png = baked.EncodeToPNG();
            string digest;
            using (SHA256 hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(png)).Replace("-", "").ToLowerInvariant();

            string relativePath = Path.Combine("Texture2D", "pmx_material_" + digest + ".png");
            Directory.CreateDirectory(Path.Combine(directory, "Texture2D"));
            File.WriteAllBytes(Path.Combine(directory, relativePath), png);
            var result = new Result
            {
                Status = "baked",
                TexturePath = relativePath.Replace('\\', '/'),
                Inputs = profile.Audit
            };
            if (cache != null && !string.IsNullOrEmpty(profile.CacheKey)) cache[profile.CacheKey] = result;
            return result;
        }
        catch (Exception exception)
        {
            string reason = "材质贴图烘焙失败；保留原主贴图。" + exception.Message;
            Debug.LogWarning($"PMX 材质高光未烘焙：{source.name}；{reason}");
            return new Result { Status = "failed", Reason = reason };
        }
        finally
        {
            DestroyTemporary(baked);
        }
    }

    /// <summary>供真实 GPU 夹具直接取得烘焙贴图。</summary>
    public static Texture2D Bake(Renderer renderer, int slot, Material source)
    {
        if (!PMXMaterialBakeProfile.TryCreate(renderer, slot, source, out PMXMaterialBakeProfile profile,
                out string status, out string reason))
            throw new InvalidOperationException("材质烘焙配置不可用：" + status + "；" + reason);
        if (profile == null) throw new InvalidOperationException("材质烘焙配置为空。");
        return Bake(profile);
    }

    private static Texture2D Bake(PMXMaterialBakeProfile profile)
    {
        if (profile.Main == null || profile.Main.width <= 0 || profile.Main.height <= 0)
            throw new InvalidOperationException("主贴图尺寸无效。");
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            throw new InvalidOperationException("当前没有可用的图形设备。");
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            throw new InvalidOperationException("图形设备不支持 RGBAFloat 烘焙与回读。");

        Shader shader = Resources.Load<Shader>("PMXMaterialLayerBake");
        if (shader == null || !shader.isSupported)
            throw new InvalidOperationException("PMXMaterialLayerBake 缺失或当前设备不支持该 Shader。");

        RenderTexture target = null;
        Texture2D readback = null;
        Texture2D output = null;
        Material bakeMaterial = null;
        RenderTexture previousTarget = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        try
        {
            target = RenderTexture.GetTemporary(profile.Main.width, profile.Main.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            // 临时 RT 可能延迟创建，首次 Blit 前显式分配。
            if (target != null && !target.IsCreated()) target.Create();
            if (target == null || !target.IsCreated())
                throw new InvalidOperationException("无法创建线性浮点烘焙目标。");

            bakeMaterial = new Material(shader)
            {
                name = "Hidden/UmaViewer/PMXMaterialLayerBake",
                hideFlags = HideFlags.HideAndDontSave
            };
            profile.Configure(bakeMaterial);
            GL.sRGBWrite = false;
            Graphics.Blit(profile.Main, target, bakeMaterial, 0);

            RenderTexture.active = target;
            readback = new Texture2D(profile.Main.width, profile.Main.height, TextureFormat.RGBAFloat, false, true);
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
            readback.Apply(false, false);

            Color[] pixels = readback.GetPixels();
            output = new Texture2D(profile.Main.width, profile.Main.height, TextureFormat.RGBA32, false, true);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    pixels[i] = new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g),
                        Mathf.LinearToGammaSpace(c.b), c.a);
                }
            }
            output.SetPixels(pixels);
            output.Apply(false, false);
            Texture2D result = output;
            output = null;
            return result;
        }
        finally
        {
            GL.sRGBWrite = previousSrgbWrite;
            RenderTexture.active = previousTarget;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            DestroyTemporary(readback);
            DestroyTemporary(output);
            DestroyTemporary(bakeMaterial);
        }
    }

    private static void DestroyTemporary(UnityEngine.Object resource)
    {
        if (resource == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
        else UnityEngine.Object.DestroyImmediate(resource);
    }
}
