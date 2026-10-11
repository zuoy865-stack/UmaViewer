using System;
using System.Linq;
using System.Collections.Generic;
using LibMMD.Material;
using LibMMD.Model;
using UnityEngine;

/// <summary>材质转换独立于索引布局，眼睛继续使用既有合成路径。</summary>
internal static class PMXMeshMaterialExporter
{
    internal static MMDMaterial Create(Renderer renderer, Mesh mesh, int submesh, Material source,
        RawMMDModel model, string directory)
        => Create(renderer, mesh, submesh, source, model, directory, null, out _);

    internal static MMDMaterial Create(Renderer renderer, Mesh mesh, int submesh, Material source,
        RawMMDModel model, string directory, Dictionary<string, PMXMaterialTextureExporter.Result> cache,
        out PMXMaterialTextureExporter.Result textureBake)
    {
        var material = new MMDMaterial
        {
            Name = source.name.Replace(" (Instance)", ""), NameEn = source.name.Replace(" (Instance)", ""),
            DiffuseColor = Color.white, SpecularColor = Color.clear, AmbientColor = Color.white * 0.5f,
            Shiness = 5, CastSelfShadow = true, DrawGroundShadow = true, DrawSelfShadow = true,
            EdgeColor = Color.black, EdgeSize = 0.4f, MetaInfo = ""
        };
        material.Texture = source.HasProperty("_MainTex") && source.mainTexture != null
            ? model.TextureList.Find(texture => texture.TexturePath.Contains("/" + source.mainTexture.name + ".png"))
            : null;
        if (material.Texture == null && model.TextureList.Count > 0) material.Texture = model.TextureList[0];
        string shader = source.shader.name;
        bool eye = shader == "Gallop/3D/Chara/ToonEye/T" || shader == "Uma/Eye" || shader == "Nars/UmaMusume/Eyes";
        if (eye)
        {
            bool baked = PMXEyeTextureExporter.TryExport(renderer, mesh, submesh, source, directory, out string eyePath);
            textureBake = new PMXMaterialTextureExporter.Result
            {
                Status = string.IsNullOrEmpty(directory) ? "not_requested" : baked ? "baked-eye" : "failed",
                Reason = baked ? null : "眼图未合成，详见眼图导出警告。", TexturePath = eyePath
            };
            if (baked)
            {
                BindTexture(material, model, eyePath);
                material.MetaInfo = "Eye diffuse and highlights baked from current material inputs.";
            }
        }
        else
        {
            textureBake = PMXMaterialTextureExporter.TryExport(renderer, submesh, source, directory, cache);
            if (textureBake.Status == "baked")
            {
                BindTexture(material, model, textureBake.TexturePath);
                material.MetaInfo = "Static material color layers and pseudo highlights baked into diffuse (reference-png-rg-v1).";
            }
        }
        return material;
    }

    private static void BindTexture(MMDMaterial material, RawMMDModel model, string path)
    {
        var texture = model.TextureList.Find(item => item.TexturePath == path);
        if (texture == null) { texture = new MMDTexture(path); model.TextureList.Add(texture); }
        material.Texture = texture;
    }

    internal static string Signature(MMDMaterial material)
    {
        // 同名材质不足以证明源层相同；签名是去重的第二重限制。
        // 字段以数值数组写入，避免区域设置影响诊断。
        return Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            material.Name, texture = material.Texture?.TexturePath,
            diffuse = Rgba(material.DiffuseColor), specular = Rgba(material.SpecularColor),
            ambient = Rgba(material.AmbientColor), edge = Rgba(material.EdgeColor),
            material.Shiness, material.EdgeSize, material.DrawDoubleFace, material.DrawEdge,
            material.DrawGroundShadow, material.CastSelfShadow, material.DrawSelfShadow, material.MetaInfo
        });
    }
    private static float[] Rgba(Color color) => new[] { color.r, color.g, color.b, color.a };
}
