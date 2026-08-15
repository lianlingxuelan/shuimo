// 水墨地面:宣纸底 + 淡墨晕斑 + 细纸纹(Built-in RP)
// 用法:配合 Editor 菜单「Shuimo > 2.5D > Apply Ink Bamboo Materials」自动套用。
// 2026-08-15 改为 Unlit：原 Lambert 依赖场景打光，2D 场景无地面光时整片渲染成黑
// （即用户看到的「黑站台」）。Unlit 后不受光影响，始终为宣纸白，符合「弄成全白」诉求。
Shader "Xianxia/Ink/InkGround"
{
    Properties
    {
        _PaperColor ("宣纸色", Color) = (0.95, 0.94, 0.89, 1)
        _InkColor   ("淡墨色", Color) = (0.86, 0.85, 0.80, 1)
        _BlotScale  ("墨斑粒度", Range(0.5, 20)) = 3.0
        _BlotStrength ("墨斑强度", Range(0, 0.5)) = 0.22
        _FineScale  ("细纸纹粒度", Range(1, 120)) = 60.0
        _FineStrength ("细纸纹强度", Range(0, 0.3)) = 0.08
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
            float _BlotScale;
            float _BlotStrength;
            float _FineScale;
            float _FineStrength;

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

            fixed4 frag (v2f i) : SV_Target
            {
                float2 p = i.wpos.xz * 0.25;

                // 大尺度淡墨晕斑
                float blot = vnoise(p * _BlotScale);
                // 小尺度细纸纹
                float fine = vnoise(p * _FineScale * 0.1);

                fixed3 col = _PaperColor.rgb;
                col = lerp(col, _InkColor.rgb, blot * _BlotStrength);
                col = lerp(col, _InkColor.rgb * 0.9, fine * _FineStrength);

                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
