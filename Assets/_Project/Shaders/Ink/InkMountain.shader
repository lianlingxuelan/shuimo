// 远山剪影：一个 Quad 面片，半透明水墨色，垂直方向下淡上浓（山尖实、山脚没入雾）。
// 配合 AtmosphereLayer 在竹林子区外围远处叠几层，建立空间纵深。
// Unlit / Built-in，支持雾（multi_compile_fog），不写深度避免遮挡近景。
Shader "Xianxia/Ink/InkMountain"
{
    Properties
    {
        _InkColor    ("墨色", Color) = (0.32, 0.36, 0.40, 1)
        _TopAlpha    ("山顶不透明度", Range(0.0, 1.0)) = 0.82
        _BottomAlpha ("山脚不透明度", Range(0.0, 1.0)) = 0.04
        _FadeTop     ("顶部淡出宽度", Range(0.0, 1.0)) = 0.18
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #pragma multi_compile_fog

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            float4 _InkColor;
            float _TopAlpha;
            float _BottomAlpha;
            float _FadeTop;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // uv.y: 0 底 1 顶。山尖（顶）实、山脚（底）淡入雾。
                float a = lerp(_BottomAlpha, _TopAlpha, i.uv.y);
                // 顶部再轻轻淡出，模拟山尖没入天空
                a *= 1.0 - smoothstep(1.0 - _FadeTop, 1.0, i.uv.y);
                // 底部软化硬边
                a *= smoothstep(0.0, 0.08, i.uv.y) * 0.5 + 0.5;

                fixed4 col = _InkColor;
                col.a *= a;
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}
