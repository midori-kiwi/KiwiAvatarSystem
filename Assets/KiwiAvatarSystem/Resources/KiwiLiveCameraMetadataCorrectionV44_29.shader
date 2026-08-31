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

            float SrgbNonlinearToLinearChannel(float value)
            {
                return
                    value <= 0.04045
                    ? value / 12.92
                    : pow(
                        (value + 0.055) / 1.055,
                        2.4);
            }

            float3 SrgbNonlinearToLinear(float3 value)
            {
                return float3(
                    SrgbNonlinearToLinearChannel(value.r),
                    SrgbNonlinearToLinearChannel(value.g),
                    SrgbNonlinearToLinearChannel(value.b));
            }

            float4 frag(v2f_img i) : SV_Target
            {
                // Frozen Native RGB source:
                //   Y' limited expansion + BT.709 matrix + saturate().
                //
                // Camera metadata:
                //   MF_MT_VIDEO_NOMINAL_RANGE = 0..255
                //   MF_MT_YUV_MATRIX          = BT.601
                //
                // v44.28 established that range mismatch dominates.
                //
                // v44.29 performed the Y'CbCr correction but returned the
                // resulting display/nonlinear RGB values directly from this
                // fragment shader. In Unity Linear Color Space that caused
                // those nonlinear values to pass through the render/display
                // transfer path as if they were linear values. The visible
                // result was severe shadow lift: an intended 16/255 display
                // value appeared near 69/255.
                //
                // v44.30 keeps the same metadata correction, then converts the
                // intended sRGB/display-nonlinear RGB into linear-light shader
                // output. Unity's normal linear->sRGB presentation path can
                // then reproduce the intended nonlinear display value.
                //
                // This remains preview-only and diagnostic. Values destroyed
                // by the frozen Native clamp cannot be reconstructed exactly.

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

                float3 rgb601FullNonlinear;

                rgb601FullNonlinear.r =
                    yFull +
                    1.4020 * v;

                rgb601FullNonlinear.g =
                    yFull -
                    0.344136 * u -
                    0.714136 * v;

                rgb601FullNonlinear.b =
                    yFull +
                    1.7720 * u;

                rgb601FullNonlinear =
                    saturate(
                        rgb601FullNonlinear);

                float3 rgb601FullLinear =
                    SrgbNonlinearToLinear(
                        rgb601FullNonlinear);

                return float4(
                    rgb601FullLinear,
                    1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
