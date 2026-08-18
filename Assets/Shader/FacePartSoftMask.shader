Shader "UI/FacePartSoftMask"
{
    Properties
    {
        [PerRendererData]
        _MainTex ("Texture", 2D) = "white" {}

        _Color ("Tint", Color) = (1,1,1,1)


        // =====================================================
        // Mask
        // =====================================================

        _Feather (
            "Edge Feather",
            Range(0.0001, 0.1)
        ) = 0.04


        _MaskVisibility (
            "Mask Visibility",
            Range(0,1)
        ) = 1


        // =====================================================
        // Position
        //
        // FacePartShapeMask
        // =====================================================

        _SampleOffset (
            "Sample Offset",
            Vector
        ) = (0,0,0,0)


        // =====================================================
        // Legacy Uniform Scale
        //
        // 互換性維持用。
        // 通常は1.0。
        // =====================================================

        _SampleScale (
            "Sample Scale",
            Float
        ) = 1


        // =====================================================
        // ★Independent X/Y Size Lock
        //
        // x > 1 → 横方向を小さく表示
        // y > 1 → 縦方向を小さく表示
        // =====================================================

        _SampleScaleXY (
            "Sample Scale XY",
            Vector
        ) = (1,1,0,0)


        // =====================================================
        // Angle
        //
        // FacePartAngleLock
        // =====================================================

        _SamplePivot (
            "Sample Pivot",
            Vector
        ) = (0.5,0.5,0,0)


        _SampleRotationRad (
            "Sample Rotation",
            Float
        ) = 0


        _SourceAspect (
            "Source Aspect",
            Float
        ) = 1


        // =====================================================
        // Unity UI
        // =====================================================

        [HideInInspector]
        _StencilComp (
            "Stencil Comparison",
            Float
        ) = 8


        [HideInInspector]
        _Stencil (
            "Stencil ID",
            Float
        ) = 0


        [HideInInspector]
        _StencilOp (
            "Stencil Operation",
            Float
        ) = 0


        [HideInInspector]
        _StencilWriteMask (
            "Stencil Write Mask",
            Float
        ) = 255


        [HideInInspector]
        _StencilReadMask (
            "Stencil Read Mask",
            Float
        ) = 255


        [HideInInspector]
        _ColorMask (
            "Color Mask",
            Float
        ) = 15


        [Toggle(UNITY_UI_ALPHACLIP)]
        _UseUIAlphaClip (
            "Use Alpha Clip",
            Float
        ) = 0
    }


    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }


        Stencil
        {
            Ref [_Stencil]

            Comp [_StencilComp]

            Pass [_StencilOp]

            ReadMask [_StencilReadMask]

            WriteMask [_StencilWriteMask]
        }


        Cull Off

        Lighting Off

        ZWrite Off

        ZTest [unity_GUIZTestMode]


        Blend SrcAlpha OneMinusSrcAlpha


        ColorMask [_ColorMask]


        Pass
        {
            Name "FacePartMask"


            CGPROGRAM


            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0


            #include "UnityCG.cginc"
            #include "UnityUI.cginc"


            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP


            #define MAX_MASK_POINTS 48


            struct appdata_t
            {
                float4 vertex : POSITION;

                float4 color : COLOR;

                float2 texcoord : TEXCOORD0;


                UNITY_VERTEX_INPUT_INSTANCE_ID
            };


            struct v2f
            {
                float4 vertex : SV_POSITION;

                fixed4 color : COLOR;

                float2 texcoord : TEXCOORD0;

                float4 worldPosition : TEXCOORD1;


                UNITY_VERTEX_OUTPUT_STEREO
            };


            sampler2D _MainTex;


            fixed4 _Color;


            float4 _ClipRect;


            float _Feather;

            float _MaskVisibility;


            float4 _SampleOffset;


            float _SampleScale;

            float4 _SampleScaleXY;


            float4 _SamplePivot;

            float _SampleRotationRad;

            float _SourceAspect;


            float4 _MaskPoints[
                MAX_MASK_POINTS
            ];


            float _MaskPointCount;


            // =================================================
            // Vertex
            // =================================================

            v2f vert(
                appdata_t v)
            {
                v2f OUT;


                UNITY_SETUP_INSTANCE_ID(
                    v
                );


                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(
                    OUT
                );


                OUT.vertex =
                    UnityObjectToClipPos(
                        v.vertex
                    );


                OUT.worldPosition =
                    v.vertex;


                OUT.texcoord =
                    v.texcoord;


                OUT.color =
                    v.color *
                    _Color;


                return OUT;
            }


            // =================================================
            // Sampling Transform
            // =================================================

            float2 GetSampleUV(
                float2 uv)
            {
                float2 pivot =
                    _SamplePivot.xy;


                float2 p =
                    uv -
                    pivot;


                float aspect =
                    max(
                        0.0001,
                        _SourceAspect
                    );


                // =============================================
                // Pixel-equivalent coordinates
                // =============================================

                p.x *=
                    aspect;


                // =============================================
                // Uniform legacy scale
                // =============================================

                float uniformScale =
                    max(
                        0.01,
                        _SampleScale
                    );


                p *=
                    uniformScale;


                // =============================================
                // ★Independent X/Y limit
                // =============================================

                p.x *=
                    max(
                        0.01,
                        _SampleScaleXY.x
                    );


                p.y *=
                    max(
                        0.01,
                        _SampleScaleXY.y
                    );


                // =============================================
                // Angle correction
                // =============================================

                float s =
                    sin(
                        _SampleRotationRad
                    );


                float c =
                    cos(
                        _SampleRotationRad
                    );


                float2 rotated;


                rotated.x =
                    c * p.x -
                    s * p.y;


                rotated.y =
                    s * p.x +
                    c * p.y;


                // =============================================
                // Back to UV
                // =============================================

                rotated.x /=
                    aspect;


                float2 result =
                    pivot +
                    rotated;


                // =============================================
                // Position correction
                // =============================================

                result +=
                    _SampleOffset.xy;


                return saturate(
                    result
                );
            }


            // =================================================
            // Segment Distance
            // =================================================

            float SegmentDistance(
                float2 p,
                float2 a,
                float2 b)
            {
                float2 pa =
                    p -
                    a;


                float2 ba =
                    b -
                    a;


                float denominator =
                    max(
                        dot(
                            ba,
                            ba
                        ),
                        0.0000001
                    );


                float h =
                    saturate(
                        dot(
                            pa,
                            ba
                        )
                        /
                        denominator
                    );


                return length(
                    pa -
                    ba * h
                );
            }


            // =================================================
            // Polygon Mask
            // =================================================

            float PolygonMask(
                float2 p)
            {
                int count =
                    (int)clamp(
                        _MaskPointCount,
                        3.0,
                        (float)MAX_MASK_POINTS
                    );


                float inside =
                    0.0;


                float minimumDistance =
                    999.0;


                for (
                    int i = 0;
                    i < MAX_MASK_POINTS;
                    i++
                )
                {
                    if (
                        i >=
                        count
                    )
                    {
                        break;
                    }


                    int next =
                        i + 1;


                    if (
                        next >=
                        count
                    )
                    {
                        next =
                            0;
                    }


                    float2 a =
                        _MaskPoints[i].xy;


                    float2 b =
                        _MaskPoints[next].xy;


                    minimumDistance =
                        min(
                            minimumDistance,
                            SegmentDistance(
                                p,
                                a,
                                b
                            )
                        );


                    bool crosses =
                        (
                            (a.y > p.y)
                            !=
                            (b.y > p.y)
                        );


                    if (crosses)
                    {
                        float denominator =
                            b.y -
                            a.y;


                        if (
                            abs(
                                denominator
                            )
                            <
                            0.000001
                        )
                        {
                            denominator =
                                denominator >= 0.0
                                ?
                                0.000001
                                :
                                -0.000001;
                        }


                        float crossingX =
                            a.x
                            +
                            (
                                p.y -
                                a.y
                            )
                            *
                            (
                                b.x -
                                a.x
                            )
                            /
                            denominator;


                        if (
                            p.x <
                            crossingX
                        )
                        {
                            inside =
                                1.0 -
                                inside;
                        }
                    }
                }


                float automaticAA =
                    fwidth(
                        minimumDistance
                    )
                    *
                    1.25;


                float softness =
                    max(
                        _Feather,
                        automaticAA
                    );


                float edge =
                    smoothstep(
                        0.0,
                        softness,
                        minimumDistance
                    );


                return
                    inside *
                    edge;
            }


            // =================================================
            // Fragment
            // =================================================

            fixed4 frag(
                v2f IN)
                : SV_Target
            {
                float2 sampleUV =
                    GetSampleUV(
                        IN.texcoord
                    );


                fixed4 col =
                    tex2D(
                        _MainTex,
                        sampleUV
                    )
                    *
                    IN.color;


                float mask =
                    PolygonMask(
                        sampleUV
                    );


                col.a *=
                    mask *
                    saturate(
                        _MaskVisibility
                    );


                #ifdef UNITY_UI_CLIP_RECT


                col.a *=
                    UnityGet2DClipping(
                        IN.worldPosition.xy,
                        _ClipRect
                    );


                #endif


                #ifdef UNITY_UI_ALPHACLIP


                clip(
                    col.a -
                    0.001
                );


                #endif


                return col;
            }


            ENDCG
        }
    }
}