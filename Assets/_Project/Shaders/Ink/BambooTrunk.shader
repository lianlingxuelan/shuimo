// 水墨竹竿:垂直墨色渐变 + 墨晕噪声 + 竖向笔触 + 轮廓墨(水墨 Shader,Built-in RP)
// 用法:配合 Editor 菜单「Shuimo > 2.5D > Apply Ink Bamboo Materials」自动套用。
Shader "Xianxia/Ink/BambooTrunk"
{
    Properties
    {
        _InkBottom ("墨色·根", Color) = (0.05, 0.09, 0.06, 1)
        _InkMid     ("墨色·中", Color) = (0.11, 0.17, 0.10, 1)
        _InkTop     ("墨色·梢", Color) = (0.17, 0.25, 0.15, 1)
        _GradStart  ("渐变起始 Y", Float) = 0.0
        _GradEnd    ("渐变结束 Y", Float) = 10.0
        _NoiseScale ("墨晕粒度", Range(0.5, 60)) = 12.0
        _NoiseStrength ("墨晕强度", Range(0, 0.5)) = 0.20
        _StrokeScale ("笔触粒度", Range(1, 40)) = 8.0
        _StrokeStrength ("笔触强度", Range(0, 0.4)) = 0.15
        _EdgeInk    ("轮廓墨", Range(0, 0.8)) = 0.35
        _Roughness  ("粗糙度", Range(0, 1)) = 0.6
        _NodeSpacing ("竹节间距", Range(0.5, 6)) = 2.2
        _NodeWidth   ("竹节带宽", Range(0.02, 0.4)) = 0.12
        _NodeInk     ("竹节墨浓", Range(0, 0.8)) = 0.45
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow instancing
        #pragma multi_compile_instancing
        #pragma target 3.0

        fixed4 _InkBottom;
        fixed4 _InkMid;
        fixed4 _InkTop;
        float _GradStart;
        float _GradEnd;
        float _NoiseScale;
        float _NoiseStrength;
        float _StrokeScale;
        float _StrokeStrength;
        float _EdgeInk;
        float _Roughness;
        float _NodeSpacing;
        float _NodeWidth;
        float _NodeInk;

        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
            float3 viewDir;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
        }

        // 2D 值噪声(无纹理依赖,可在 Built-in RP 直接用)
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
            // 垂直三段渐变:根 -> 中 -> 梢
            float t = saturate((IN.worldPos.y - _GradStart) / max(_GradEnd - _GradStart, 1e-4));
            fixed3 col = lerp(_InkBottom.rgb, _InkMid.rgb, saturate(t * 2.5));
            col = lerp(col, _InkTop.rgb, saturate((t - 0.4) * 1.667));

            // 墨晕:细尺度噪声,向深墨拉,模拟中锋行笔的浓淡相接
            float n = vnoise(IN.worldPos.xz * 0.5 * _NoiseScale);
            col = lerp(col, _InkBottom.rgb, n * _NoiseStrength);

            // 竖向笔触:把噪声在 Y 方向拉长(变化慢),形成纵向墨痕
            float2 sp = float2(IN.worldPos.x * _StrokeScale * 0.5, IN.worldPos.y * 0.35);
            float s = vnoise(sp);
            col = lerp(col, _InkMid.rgb * 0.8, s * _StrokeStrength);

            // 轮廓墨:边缘(Fresnel)略收深,像水墨勾边
            float rim = pow(1.0 - saturate(dot(normalize(IN.viewDir), IN.worldNormal)), 1.5);
            col = lerp(col, _InkBottom.rgb, rim * _EdgeInk);

            // 竹节:沿竿规则分布的墨环(竹之特征),节点处墨色收浓、略下淌
            float nodePhase = abs(frac(IN.worldPos.y / _NodeSpacing + 0.5) - 0.5) * 2.0;
            float nodeBand = 1.0 - smoothstep(0.0, _NodeWidth, nodePhase);
            col = lerp(col, _InkBottom.rgb * 0.5, nodeBand * _NodeInk);
            // 节下淡墨晕开(似运笔后墨未干)
            float nodeBelow = smoothstep(0.0, _NodeWidth * 2.2, nodePhase) * (1.0 - smoothstep(_NodeWidth * 2.2, _NodeWidth * 5.0, nodePhase));
            col = lerp(col, _InkMid.rgb * 0.85, nodeBelow * _NodeInk * 0.4);

            o.Albedo = col;
            o.Alpha = 1.0;
            o.Specular = 0.0;
            o.Gloss = 0.0;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
