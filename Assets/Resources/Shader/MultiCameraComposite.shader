Shader "Hidden/UmaViewer/MultiCameraComposite"
{
    Properties
    {
        _MainTex ("Main Camera Texture", 2D) = "white" {}
        _SubTex ("Sub Camera Texture", 2D) = "white" {}
        _DivideLineColor ("Divide Line Color", Color) = (1, 1, 1, 1)
        _DivideLineParam ("Divide Line Param (OffsetX, OffsetY, CosRoll, SinRoll)", Vector) = (0, 0, 1, 0)
        _DivideLineSetting ("Divide Line Setting (LineType, Thickness, FadeValue, Feather)", Vector) = (1, 0.015, 1, 0.002)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _SubTex;
            fixed4 _DivideLineColor;
            float4 _DivideLineParam;    // x: OffsetX, y: OffsetY, z: CosRoll, w: SinRoll
            float4 _DivideLineSetting;  // x: LineType (0:Fade, 1:Color), y: LineThickness, z: FadeValue, w: Feather

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float fade = clamp(_DivideLineSetting.z, 0.0, 1.0);

                fixed4 col0 = tex2D(_MainTex, uv);
                fixed4 col1 = tex2D(_SubTex, uv);

                fixed4 finalColor = col0;

                // 判断是否为不分屏模式（即 IsScreenDivide == false，由 _DivideLineSetting.y < 0.0 表示）
                if (_DivideLineSetting.y < 0.0)
                {
                    // 不分屏模式：跳过分割线计算与左右分屏判别，直接取次机位全屏画面
                    // 支持 MaskType.All 与全屏分屏混合淡入淡出
                    finalColor = col1;
                }
                else
                {
                    // 分屏模式：计算屏幕像素点到中心倾斜分割线的带符号垂直距离 (SDF)
                    float2 center = float2(0.5, 0.5) + _DivideLineParam.xy;
                    float2 diff = uv - center;
                    float dist = diff.x * _DivideLineParam.z + diff.y * _DivideLineParam.w;
                    float halfThickness = max(0.0001, _DivideLineSetting.y * 0.5);
                    float feather = max(0.0005, _DivideLineSetting.w);
                    float lineType = _DivideLineSetting.x;

                    if (lineType > 0.5)
                    {
                        // 纯色分割线模式（带边缘抗锯齿与羽化）
                        float lineFactor = 1.0 - smoothstep(halfThickness, halfThickness + feather, abs(dist));
                        float side = step(0.0, dist);
                        fixed4 sceneCol = lerp(col0, col1, side);
                        finalColor = lerp(sceneCol, _DivideLineColor, lineFactor * _DivideLineColor.a);
                    }
                    else
                    {
                        // 渐变过渡羽化模式 (Fade)：融合半厚度与抗锯齿羽化带，确保超高分辨率与斜切无锯齿撕裂
                        float fadeSpan = max(feather, halfThickness);
                        float blendFactor = smoothstep(-fadeSpan, fadeSpan, dist);
                        finalColor = lerp(col0, col1, blendFactor);
                    }
                }

                // 叠加整屏透明度平滑淡入淡出（FadeValue 从 0 到 1）
                return lerp(col0, finalColor, fade);
            }
            ENDCG
        }
    }
    Fallback Off
}
