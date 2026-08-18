Shader "Hidden/KiwiAvatar/InferenceFaceCrop"
{
    Properties
    {
        _MainTex("Source", 2D) = "black" {}
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
                float2 sourceUv = mul(_Xform, float4(input.uv, 0, 1)).xy;
                return tex2D(_MainTex, sourceUv);
            }
            ENDCG
        }
    }
}
