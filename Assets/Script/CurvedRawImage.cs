using UnityEngine;
using UnityEngine.UI;


public class CurvedRawImage : RawImage
{
    // =========================================================
    // Mesh Resolution
    // =========================================================

    [Header("Curved Mesh")]

    [Tooltip("横方向の分割数")]
    [Range(2, 40)]
    public int horizontalSegments = 16;

    [Tooltip("縦方向の分割数")]
    [Range(2, 30)]
    public int verticalSegments = 8;


    // =========================================================
    // Curvature
    // =========================================================

    [Header("Curvature")]

    [Tooltip(
        "横方向の湾曲量。\n" +
        "プラスで端が奥へ入る。\n" +
        "逆方向ならマイナスにする。"
    )]
    [Range(-50f, 50f)]
    public float horizontalCurveDepth = 10f;


    [Tooltip(
        "縦方向の湾曲量。\n" +
        "目は弱め、口は少し強めがおすすめ。"
    )]
    [Range(-50f, 50f)]
    public float verticalCurveDepth = 3f;


    // =========================================================
    // Extra Shape
    // =========================================================

    [Header("Shape")]

    [Tooltip(
        "中央を少し前へ膨らませる量。\n" +
        "基本は0でOK。"
    )]
    [Range(-30f, 30f)]
    public float centerBulge = 0f;


    // =========================================================
    // Populate Mesh
    // =========================================================

    protected override void OnPopulateMesh(
        VertexHelper vh)
    {
        vh.Clear();


        Rect rect =
            GetPixelAdjustedRect();


        int xCount =
            Mathf.Max(
                2,
                horizontalSegments
            );


        int yCount =
            Mathf.Max(
                2,
                verticalSegments
            );


        // =====================================================
        // Vertices
        // =====================================================

        for (int y = 0; y <= yCount; y++)
        {
            float v =
                y /
                (float)yCount;


            float localY =
                Mathf.Lerp(
                    rect.yMin,
                    rect.yMax,
                    v
                );


            // -1 ～ +1
            float normalizedY =
                v * 2f -
                1f;


            for (int x = 0; x <= xCount; x++)
            {
                float u =
                    x /
                    (float)xCount;


                float localX =
                    Mathf.Lerp(
                        rect.xMin,
                        rect.xMax,
                        u
                    );


                // -1 ～ +1
                float normalizedX =
                    u * 2f -
                    1f;


                // =============================================
                // 曲面
                //
                // 中央 = 0
                // 端   = 奥
                // =============================================

                float curveX =
                    normalizedX *
                    normalizedX;


                float curveY =
                    normalizedY *
                    normalizedY;


                float z =
                    -(
                        curveX *
                        horizontalCurveDepth
                    )
                    -
                    (
                        curveY *
                        verticalCurveDepth
                    );


                // =============================================
                // 中央の膨らみ
                // =============================================

                if (
                    Mathf.Abs(
                        centerBulge
                    ) >
                    0.0001f
                )
                {
                    float radius =
                        Mathf.Clamp01(
                            curveX +
                            curveY
                        );


                    z +=
                        (
                            1f -
                            radius
                        )
                        *
                        centerBulge;
                }


                // =============================================
                // FacePartCropperが設定したuvRectを使用
                // =============================================

                Vector2 textureUV =
                    new Vector2(
                        Mathf.Lerp(
                            uvRect.xMin,
                            uvRect.xMax,
                            u
                        ),

                        Mathf.Lerp(
                            uvRect.yMin,
                            uvRect.yMax,
                            v
                        )
                    );


                // =============================================
                // Mask用UV
                //
                // FacePartSoftMaskのTEXCOORD1へ渡す
                // =============================================

                Vector2 maskUV =
                    new Vector2(
                        u,
                        v
                    );


                UIVertex vertex =
                    UIVertex.simpleVert;


                vertex.position =
                    new Vector3(
                        localX,
                        localY,
                        z
                    );


                vertex.color =
                    color;


                vertex.uv0 =
                    textureUV;


                vertex.uv1 =
                    maskUV;


                vh.AddVert(
                    vertex
                );
            }
        }


        // =====================================================
        // Triangles
        // =====================================================

        int rowSize =
            xCount +
            1;


        for (int y = 0; y < yCount; y++)
        {
            for (int x = 0; x < xCount; x++)
            {
                int i0 =
                    y *
                    rowSize +
                    x;


                int i1 =
                    i0 +
                    1;


                int i2 =
                    i0 +
                    rowSize;


                int i3 =
                    i2 +
                    1;


                vh.AddTriangle(
                    i0,
                    i2,
                    i1
                );


                vh.AddTriangle(
                    i1,
                    i2,
                    i3
                );
            }
        }
    }


    // =========================================================
    // Inspector変更時に即更新
    // =========================================================

#if UNITY_EDITOR

    protected override void OnValidate()
    {
        base.OnValidate();

        SetVerticesDirty();
    }

#endif
}