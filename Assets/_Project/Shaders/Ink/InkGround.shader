// 水墨地面:宣纸底 + 淡墨晕斑 + 细纸纹(Built-in RP)
// 用法:配合 Editor 菜单「Shuimo > 2.5D > Apply Ink Bamboo Materials」自动套用。
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
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow
        #pragma target 3.0

        fixed4 _PaperColor;
        fixed4 _InkColor;
        float _BlotScale;
        float _BlotStrength;
        float _FineScale;
        float _FineStrength;

        struct Input
        {
            float3 worldPos;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
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

        void surf(Input IN, inout SurfaceOutput o)
        {
            float2 p = IN.worldPos.xz * 0.25;

            // 大尺度淡墨晕斑
            float blot = vnoise(p * _BlotScale);
            // 小尺度细纸纹
            float fine = vnoise(p * _FineScale * 0.1);

            fixed3 col = _PaperColor.rgb;
            col = lerp(col, _InkColor.rgb, blot * _BlotStrength);
            col = lerp(col, _InkColor.rgb * 0.9, fine * _FineStrength);

            o.Albedo = col;
            o.Alpha = 1.0;
            o.Specular = 0.0;
            o.Gloss = 0.0;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
