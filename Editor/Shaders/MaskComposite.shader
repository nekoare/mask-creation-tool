Shader "Hidden/Nekoare/MaskToolCompositeEditor" {
    Properties {
        _MainTex ("MainTex", 2D) = "white" {}
        _MaskTex ("MaskTex", 2D) = "white" {}
        _UVMap ("UVMap", 2D) = "black" {}
        _TextureScale ("TextureScale", Float) = 1
        _Offset ("Offset", Vector) = (0, 0, 0, 0)
        _UVMapLineColor ("UVMap Line Color", Color) = (1, 1, 1, 1)
        _MaskColor ("Mask Display Color", Color) = (0, 0, 0, 0)
        [Toggle]
        _ApplyGammaCorrection ("Apply Gamma Correction", Float) = 1
    }
    SubShader {
        Tags { "RenderType" = "Opaque" }
        Pass {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _MaskTex;
            sampler2D _UVMap;
            float _TextureScale;
            float4 _Offset;
            fixed4 _UVMapLineColor;
            fixed4 _MaskColor;
            float _ApplyGammaCorrection;

            fixed4 frag(v2f_img i) : SV_Target
            {
                // MeshDeleterWithTexture方式のUV変換（ズーム・スクロール対応）
                float2 uv = (float2(0.5, 0.5) * (1-_TextureScale) + _Offset.xy * 0.5) + i.uv * _TextureScale;

                fixed4 bg = tex2D(_MainTex, uv);
                fixed4 m = tex2D(_MaskTex, uv);

                // ガンマ補正を適用（MeshDeleterWithTexture方式）
                if (_ApplyGammaCorrection)
                    bg = pow(bg, 1/2.2);

                fixed4 col;
                if (_MaskColor.a > 0.5)
                {
                    // 色付きモード: mask.r=0で色が不透明、mask.r=1で背景そのまま
                    col = fixed4(lerp(_MaskColor.rgb, bg.rgb, m.r), 1.0);
                }
                else
                {
                    // グレーモード（従来）: マスクのRチャンネルで背景を乗算
                    col = fixed4(bg.rgb * m.r, 1.0);
                }

                // UVマップをオーバーレイ（MeshDeleterWithTexture方式）
                float uvMapAlpha = tex2D(_UVMap, uv).r;
                col.rgb = lerp(col.rgb, _UVMapLineColor.rgb, uvMapAlpha * 0.7);

                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}