Shader "Xianxia/Ink/ChromaKeySprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _GreenThreshold ("Green Key Threshold", Range(0, 1)) = 0.32
        _DespillStrength ("Edge Green Despill", Range(0, 1)) = 0.9
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float _GreenThreshold;
            float _DespillStrength;

            float ChromaSignal(fixed3 rgb)
            {
                float greenDominance = rgb.g - max(rgb.r, rgb.b);
                return smoothstep(_GreenThreshold, _GreenThreshold + 0.08, greenDominance)
                    * smoothstep(0.55, 0.75, rgb.g);
            }

            v2f vert(appdata_t input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.texcoord = TRANSFORM_TEX(input.texcoord, _MainTex);
                output.color = input.color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.texcoord) * input.color;
                float greenDominance = color.g - max(color.r, color.b);
                // 两段平滑阈值：纯绿背景彻底剔除，发丝/袖口的半透明边缘则柔和过渡，
                // 避免硬 step 在水墨线稿外留一圈锯齿或绿色光边。
                float isKey = ChromaSignal(color.rgb);
                color.a *= 1.0 - isKey;

                // 邻域分支清掉 1~2px 外轮廓绿边；全局弱阈值分支处理生成图把绿幕
                // 反光画进黑发和半透明衣摆的较宽色污染。二者取较强值，避免攻击帧
                // 在浅色竹林上出现整片荧光绿发丝。
                float2 px = _MainTex_TexelSize.xy;
                float nearbyKey = 0.0;
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2( px.x, 0.0)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(-px.x, 0.0)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(0.0,  px.y)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(0.0, -px.y)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2( 2.0 * px.x, 0.0)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(-2.0 * px.x, 0.0)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(0.0,  2.0 * px.y)).rgb * input.color.rgb));
                nearbyKey = max(nearbyKey, ChromaSignal(tex2D(_MainTex, input.texcoord + float2(0.0, -2.0 * px.y)).rgb * input.color.rgb));
                float edgeSpill = nearbyKey * smoothstep(0.02, _GreenThreshold, greenDominance);
                float globalSpill = smoothstep(0.005, 0.10, greenDominance)
                    * smoothstep(0.18, 0.45, color.g);
                float spill = max(edgeSpill, globalSpill) * _DespillStrength;
                float neutralGreen = (color.r + color.b) * 0.5 + 0.004;
                color.g = lerp(color.g, min(color.g, neutralGreen), spill);
                return color;
            }
            ENDCG
        }
    }
}
