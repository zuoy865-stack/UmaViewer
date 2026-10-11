using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>按参考 PNG 校准的静态颜色层规则，不依赖场景灯光。</summary>
internal sealed class PMXMaterialBakeProfile
{
    private const string Rule = "reference-png-rg-v1";
    internal Texture2D Main;
    private Texture2D _shade, _mask;
    private Vector4 _mainST, _shadeST, _maskST;
    private Color _tint = Color.white;
    private float _clip;
    private float _highlight = 1;
    internal string CacheKey;
    internal object Audit;

    internal static bool TryCreate(Renderer renderer, int slot, Material source,
        out PMXMaterialBakeProfile profile, out string status, out string reason)
    {
        profile = null;
        status = "unsupported";
        reason = "未校准的 Shader。";
        if (source == null || source.shader == null) return false;
        string shader = source.shader.name;
        if (shader == "Gallop/3D/Chara/ToonMayu" || shader == "Uma/Tail")
        {
            status = "no-applicable-layer";
            reason = "该材质没有已确认的静态高光遮罩。";
            return false;
        }
        bool local = shader == "Uma/Base";
        bool face = shader == "Gallop/3D/Chara/ToonFace/TSER" || shader == "Uma/Face";
        if (!local && shader != "Gallop/3D/Chara/Toon/TSER" &&
            shader != "Gallop/3D/Chara/ToonHair/TSER" && shader != "Gallop/3D/Chara/NolineToon/TSER" && !face)
        {
            return false;
        }
        var input = new EffectiveInputs(renderer, slot, source);
        string[] known = { "_USEOPTIONMASKMAP_ON", "_USEOPTIONMASKMAP_OFF" };
        if (source.IsKeywordEnabled("USE_MASK_COLOR"))
        {
            reason = "区域换色分支尚未针对该材质校准。";
            return false;
        }
        bool localTextures = local || shader == "Uma/Face";
        if (!localTextures && source.shaderKeywords.Any(keyword => !known.Contains(keyword)))
        {
            reason = "包含尚未校准的关键字分支：" + string.Join(",", source.shaderKeywords);
            return false;
        }
        foreach (string name in new[] { "_DirtTex", "_EmissiveTex" })
            if (input.Texture(name) != null)
            {
                reason = "存在未校准的额外颜色层：" + name;
                return false;
            }
        string shadeName = localTextures ? "_ShadTex" : "_ToonMap";
        string maskName = localTextures ? "_BaseTex" : "_TripleMaskMap";
        if (!(input.Texture("_MainTex") is Texture2D main) ||
            !(input.Texture(shadeName) is Texture2D shade) || !(input.Texture(maskName) is Texture2D mask))
        {
            reason = "主色、阴影色和 RG 遮罩必须是有效 Texture2D。";
            return false;
        }
        var result = new PMXMaterialBakeProfile
        {
            Main = main, _shade = shade, _mask = mask,
            _mainST = input.ST("_MainTex"), _shadeST = input.ST(shadeName), _maskST = input.ST(maskName),
            _tint = input.Color("_CharaColor", Color.white),
            // 本地 Uma/Base 的片元程序明确裁剪 Base.B < 0.5。
            _clip = local ? 0.5f : 0,
            // 脸部参考图只混合 R，叠 G 会把鼻部整片漂白。
            _highlight = face ? 0 : 1
        };
        result.Audit = new
        {
            rule = Rule, shader, formula = "lerp(lerp(shade,diffuse,mask.r),white,mask.g)",
            blendSpace = "sRGB encoded RGB; numeric masks; original diffuse alpha",
            highlightStrength = result._highlight,
            main = Describe(main, result._mainST), shade = Describe(shade, result._shadeST),
            mask = Describe(mask, result._maskST),
            tint = Rgba(result._tint), clipThreshold = result._clip,
            sampling = "UV0, explicit mip 0, source spatial filter and wrap; reference atlas reconstruction",
            omittedOptionMask = Describe(input.Texture("_OptionMaskMap") as Texture2D, input.ST("_OptionMaskMap")),
            colorSpace = QualitySettings.activeColorSpace.ToString(),
            omittedDirectionalInputs = "scene light, view, specular, rim, env map"
        };
        result.CacheKey = JsonConvert.SerializeObject(result.Audit);
        profile = result;
        status = "ready";
        reason = null;
        return true;
    }

    internal void Configure(Material target)
    {
        // Blit 会改写 _MainTex/ST，使用独立采样属性保留源变换。
        target.SetTexture("_DiffuseTex", Main);
        target.SetTexture("_ShadeTex", _shade);
        target.SetTexture("_MaskTex", _mask);
        target.SetVector("_DiffuseTex_ST", _mainST);
        target.SetVector("_ShadeTex_ST", _shadeST);
        target.SetVector("_MaskTex_ST", _maskST);
        bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        // 参考叠加使用编码 RGB；线性项目先恢复颜色，再在输出处转回线性。
        target.SetVector("_DecodeColor", new Vector4(ColorToEncoded(Main, linear), ColorToEncoded(_shade, linear), 0, 0));
        target.SetVector("_DecodeMask", new Vector4(linear && _mask.isDataSRGB ? 1 : 0, 0, 0, 0));
        target.SetVector("_Tint", (Vector4)_tint);
        target.SetFloat("_OutputLinear", linear ? 1 : 0);
        target.SetFloat("_ClipThreshold", _clip);
        target.SetFloat("_HighlightStrength", _highlight);
    }

    private static float ColorToEncoded(Texture2D texture, bool linear)
        => linear || !texture.isDataSRGB ? 1 : 0;
    private static float[] Rgba(Color color) => new[] { color.r, color.g, color.b, color.a };
    private static object Describe(Texture2D texture, Vector4 st) => texture == null ? null : new
    {
        name = texture.name, instanceId = texture.GetInstanceID(), texture.width, texture.height,
        texture.isDataSRGB, filter = texture.filterMode.ToString(),
        wrapU = texture.wrapModeU.ToString(), wrapV = texture.wrapModeV.ToString(),
        st = new[] { st.x, st.y, st.z, st.w }
    };

    private sealed class EffectiveInputs
    {
        private readonly Material _source;
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        internal EffectiveInputs(Renderer renderer, int slot, Material source)
        {
            _source = source;
            if (renderer == null) return;
            int actualSlot = Mathf.Min(slot, renderer.sharedMaterials.Length - 1);
            if (actualSlot >= 0) renderer.GetPropertyBlock(_block, actualSlot);
            // 材质级属性块优先，非空时不会合并 Renderer 级属性块。
            if (_block.isEmpty) renderer.GetPropertyBlock(_block);
        }
        internal Texture Texture(string name) => _block.HasProperty(name) ? _block.GetTexture(name) :
            _source.HasProperty(name) ? _source.GetTexture(name) : null;
        internal Color Color(string name, Color fallback) => _block.HasProperty(name) ? _block.GetColor(name) :
            _source.HasProperty(name) ? _source.GetColor(name) : fallback;
        internal Vector4 ST(string name)
        {
            if (_block.HasProperty(name + "_ST")) return _block.GetVector(name + "_ST");
            if (!_source.HasProperty(name)) return new Vector4(1, 1, 0, 0);
            Vector2 scale = _source.GetTextureScale(name), offset = _source.GetTextureOffset(name);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }
    }
}
