Shader "Hidden/UmaViewer/PMXMaterialLayerBake"
{
    Properties
    {
        _DiffuseTex ("Main", 2D) = "white" {}
        _ShadeTex ("Shade", 2D) = "white" {}
        _MaskTex ("Mask", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _DiffuseTex, _ShadeTex, _MaskTex;
            float4 _DiffuseTex_ST, _ShadeTex_ST, _MaskTex_ST;
            float4 _DecodeColor, _DecodeMask, _Tint;
            float _OutputLinear, _ClipThreshold, _HighlightStrength;

            float3 Encoded(float3 color, float decode)
            {
                // 精确转换保留暗色，近似函数会压黑低亮度像素。
                return decode > 0.5 ? float3(LinearToGammaSpaceExact(color.r),
                    LinearToGammaSpaceExact(color.g), LinearToGammaSpaceExact(color.b)) : color;
            }
            float2 UV(float2 uv, float4 st) { return uv * st.xy + st.zw; }
            float4 frag(v2f_img input) : SV_Target
            {
                float4 main = tex2Dlod(_DiffuseTex, float4(UV(input.uv, _DiffuseTex_ST), 0, 0));
                float3 shade = tex2Dlod(_ShadeTex, float4(UV(input.uv, _ShadeTex_ST), 0, 0)).rgb;
                float3 mask = tex2Dlod(_MaskTex, float4(UV(input.uv, _MaskTex_ST), 0, 0)).rgb;
                main.rgb = Encoded(main.rgb, _DecodeColor.x);
                shade = Encoded(shade, _DecodeColor.y);
                mask = Encoded(mask, _DecodeMask.x);
                float3 color = lerp(shade, main.rgb, saturate(mask.r)) * _Tint.rgb;
                color = lerp(color, float3(1, 1, 1), saturate(mask.g * _HighlightStrength));
                float alpha = main.a * _Tint.a;
                if (_ClipThreshold > 0) alpha *= step(_ClipThreshold, mask.b);
                color = saturate(color);
                if (_OutputLinear > 0.5) color = float3(GammaToLinearSpaceExact(color.r),
                    GammaToLinearSpaceExact(color.g), GammaToLinearSpaceExact(color.b));
                return float4(color, alpha);
            }
            ENDCG
        }
    }
}
