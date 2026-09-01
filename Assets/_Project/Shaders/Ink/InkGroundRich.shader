// 水墨地表（增强版）:宣纸底 + 淡墨晕染 + 毛笔笔触 + 细纸纹(Built-in RP, Unlit)
// 与 InkGround.shader 同源，但更强调"水墨味"，用于正式美术地表。
// 不修改原 InkGround，作为可切换的增强变体（用户已验收的纯色地面不受影响）。
Shader "Xianxia/Ink/InkGroundRich"
{
    Properties
    {
        _PaperColor    ("宣纸色", Color) = (0.92, 0.90, 0.84, 1)
        _InkColor      ("淡墨色", Color) = (0.42, 0.39, 0.33, 1)
        _InkDeep       ("浓墨色(笔触)", Color) = (0.12, 0.11, 0.09, 1)
        _BlotScale     ("墨晕粒度", Range(0.5, 20)) = 2.0
        _BlotStrength  ("墨晕强度", Range(0, 0.95)) = 0.85
        _StrokeAngle   ("笔触方向(度)", Range(0, 180)) = 35.0
        _StrokeScale   ("笔触拉伸", Range(1, 12)) = 2.5
        _StrokeStrength("笔触强度", Range(0, 0.9)) = 0.65
        _FineScale     ("细纸纹粒度", Range(1, 160)) = 90.0
        _FineStrength  ("细纸纹强度", Range(0, 0.3)) = 0.14
        _TintColor     ("远景冷调", Color) = (0.84, 0.86, 0.92, 1)
        _TintStrength  ("冷调强度", Range(0, 0.2)) = 0.05
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _PaperColor;
            fixed4 _InkColor;
            fixed4 _InkDeep;
            float _BlotScale;
            float _BlotStrength;
            float _StrokeAngle;
            float _StrokeScale;
            float _StrokeStrength;
            float _FineScale;
            float _FineStrength;
            fixed4 _TintColor;
            float _TintStrength;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float4 wp = mul(unity_ObjectToWorld, v.vertex);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = wp.xyz;
                return o;
            }

            float hash21(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float vnoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 多倍频叠加，模拟自然晕染层次
            float fbm(float2 p)
            {
                float s = 0.0;
                float a = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    s += a * vnoise(p);
                    p *= 2.03;
                    a *= 0.5;
                }
                return s;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 0.18 让单块墨晕覆盖更大范围，避免地面被细碎噪声切成"花布"。
                // 本工程的玩法地面在 XY 平面（Z 是伪深度）。若采样 XZ，整张
                // 地图在纵向会读到同一个 Z，水墨纹理就退化成接近纯色的条纹。
                // 世界单位是像素级（单屏数百单位），0.18 会把噪声压到远高于
                // 屏幕像素的频率，采样后又被平均成一片白。降低到大笔触尺度，
                // 才能看出宣纸上的墨晕而不是“有 shader 的纯色”。
                float2 p = i.wpos.xy * 0.006;

                // 大尺度淡墨晕染（多频）
                float blot = fbm(p * _BlotScale);

                // 毛笔笔触：沿方向拉伸的各向异性噪声
                float ang = _StrokeAngle * 3.1415926 / 180.0;
                float2 dir = float2(cos(ang), sin(ang));
                float2 perp = float2(-sin(ang), cos(ang));
                float along = dot(p, dir);
                float across = dot(p, perp);
                float2 sp = float2(along, across / _StrokeScale);
                float stroke = vnoise(sp * 5.0);

                // 细纸纹
                float fine = vnoise(p * _FineScale * 0.15);

                // 让墨晕/笔触边缘更锐利，形成明显的水墨斑块（否则太淡会像纯色纸）
                float blotMark = smoothstep(0.26, 0.60, blot) * _BlotStrength;
                float blotCore = smoothstep(0.52, 0.78, blot) * _BlotStrength * 0.70;
                float strokeMark = smoothstep(0.22, 0.50, stroke) * _StrokeStrength;

                fixed3 col = _PaperColor.rgb;
                col = lerp(col, _InkColor.rgb, blotMark);
                col = lerp(col, _InkDeep.rgb, saturate(strokeMark + blotCore));
                col = lerp(col, _InkColor.rgb * 0.90, fine * _FineStrength);
                // 整体略压暗，让宣纸底托住浓墨而不发飘。
                col = lerp(col, col * 0.92, 0.12);
                col = lerp(col, _TintColor.rgb, _TintStrength * fbm(p * 0.4));

                // 地面就是水墨画布本身，不能被远景白雾洗回纯白；雾只用于
                // 远处环境层，画布始终保留清晰的墨晕与纸纹。
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
