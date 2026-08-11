// 水墨竹叶:半透明 + 边缘晕染 + 叶脉淡墨(Built-in RP)
// 用法:配合 Editor 菜单「Shuimo > 2.5D > Apply Ink Bamboo Materials」自动套用。
Shader "Xianxia/Ink/BambooLeaf"
{
    Properties
    {
        _MainTex   ("主纹理", 2D) = "white" {}
        _LeafColor ("叶色", Color) = (0.15, 0.22, 0.13, 1)
        _Alpha     ("不透明度", Range(0, 1)) = 0.92
        _EdgeFade  ("边缘晕染", Range(0, 1)) = 0.55
        _NoiseScale ("笔触粒度", Range(1, 40)) = 16.0
        _NoiseStrength ("笔触强度", Range(0, 0.5)) = 0.25
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert alpha:blend
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _LeafColor;
        float _Alpha;
        float _EdgeFade;
        float _NoiseScale;
        float _NoiseStrength;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
        };

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
            // 以叶片中心为原点:距中心越远 alpha 越低(边缘晕染)
            float2 c = IN.uv_MainTex - 0.5;
            float d = length(c) * 2.0;
            // 叶片细长:沿长轴(uv.x)保留更多不透明,短轴(uv.y)更快淡开
            float lenShape = saturate(1.0 - abs(c.x) * 0.35);
            float alpha = (1.0 - smoothstep(0.2, 0.45 + _EdgeFade * 0.4, d)) * lenShape;

            // 笔触噪声:叶面浓淡不均,像运笔
            float n = vnoise(IN.worldPos.xz * 0.5 * _NoiseScale);
            fixed3 col = _LeafColor.rgb;
            col = lerp(col, col * 0.72, n * _NoiseStrength);

            // 叶脉:沿长轴中部一条稍暗的线
            float vein = saturate(1.0 - abs(c.y) * 2.5);
            col = lerp(col, col * 0.68, vein * 0.3 * lenShape);

            o.Albedo = col;
            o.Alpha = saturate(alpha) * _Alpha;
        }
        ENDCG
    }
    FallBack "Transparent/Diffuse"
}
