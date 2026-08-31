Shader "Hidden/KiwiAvatar/InferenceFaceCrop"
{
    Properties
    {
        _MainTex("Source", 2D) = "black" {}
        _InputIsSRGB("Input Is sRGB", Float) = 0
    }

    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4x4 _Xform;
            float _InputIsSRGB;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float2 sourceUv =
                    mul(
                        _Xform,
                        float4(
                            input.uv,
                            0,
                            1))
                    .xy;

                // MediaPipe ImageToTensorCalculator uses BORDER_REPLICATE by
                // default. Do not rely on the runtime WebCamTexture wrap mode.
                sourceUv =
                    saturate(
                        sourceUv);

                fixed4 color =
                    tex2D(
                        _MainTex,
                        sourceUv);

                #if !defined(UNITY_COLORSPACE_GAMMA)
                // In Linear projects, sampling an sRGB source converts it to
                // linear light before this fragment. The face-landmark model
                // expects normalized image RGB values, not linearized lighting
                // values. The destination RT is deliberately Linear, so encode
                // back to the source's stored sRGB values before ToTensor().
                if (_InputIsSRGB > 0.5)
                {
                    color.rgb =
                        LinearToGammaSpace(
                            color.rgb);
                }
                #endif

                return color;
            }
            ENDCG
        }
    }
}
