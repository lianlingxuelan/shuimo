// 水墨渐变天空：一个大球内壁，基于视线仰角做垂直渐变。
// 配合 AtmosphereLayer 使用，作为场景背景，解决"白底悬浮"问题。
// Unlit / Built-in 渲染管线，无光照依赖。
Shader "Xianxia/Ink/InkSky"
{
    Properties
    {
        _TopColor    ("上方天空色", Color) = (0.78, 0.82, 0.86, 1)
        _BottomColor ("下方雾气色", Color) = (0.93, 0.92, 0.88, 1)
        _Exponent    ("渐变指数", Range(0.2, 4.0)) = 1.2
    }
    SubShader
    {
        Tags { "RenderType"="Background" "Queue"="Background" }
        ZWrite Off
        Cull Off
        Fog { Mode Off }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewDir : TEXCOORD0;
            };

            float4 _TopColor;
            float4 _BottomColor;
            float _Exponent;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                // 视线方向（相机->表面）的 y 分量：水平 0.5、抬头 1、低头 0
                o.viewDir = normalize(UnityWorldSpaceViewDir(worldPos));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float h = saturate(i.viewDir.y * 0.5 + 0.5);
                float t = pow(h, _Exponent);
                fixed3 col = lerp(_BottomColor.rgb, _TopColor.rgb, t);
                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }
}
