Shader "Hidden/Kiwi/LiveCameraMetadataCorrectionV44_29"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            float4 frag(v2f_img i) : SV_Target
            {
                // Input is the frozen Native RGB result:
                // limited-Y expansion + BT.709 + saturate().
                //
                // Camera metadata says:
                // full-range Y + BT.601.
                //
                // This inverse/re-forward is diagnostic-only because values
                // already destroyed by Native saturate() cannot be recovered.

                float3 rgb709Limited =
                    tex2D(_MainTex, i.uv).rgb;

                float y709Limited =
                    dot(
                        rgb709Limited,
                        float3(
                            0.2126,
                            0.7152,
                            0.0722));

                float u =
                    (rgb709Limited.b -
                     y709Limited) /
                    1.8556;

                float v =
                    (rgb709Limited.r -
                     y709Limited) /
                    1.5748;

                float yFull =
                    (
                        y709Limited *
                        219.0 +
                        16.0
                    ) /
                    255.0;

                float3 rgb601Full;

                rgb601Full.r =
                    yFull +
                    1.4020 * v;

                rgb601Full.g =
                    yFull -
                    0.344136 * u -
                    0.714136 * v;

                rgb601Full.b =
                    yFull +
                    1.7720 * u;

                return float4(
                    saturate(rgb601Full),
                    1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
