using UnityEngine;
using UnityEngine.UI;


[AddComponentMenu("UI/Surface Fitted Raw Image")]
public class SurfaceFittedRawImage : RawImage
{
    // =========================================================
    // Surface Mesh
    // =========================================================

    [Header("Surface Mesh")]

    [Range(2, 40)]
    public int horizontalSegments = 20;

    [Range(2, 30)]
    public int verticalSegments = 8;


    [Header("Surface Offset")]

    [Range(0f, 0.02f)]
    public float surfaceOffset = 0.002f;


    // =========================================================
    // Compatibility
    // =========================================================

    [HideInInspector]
    public SkinnedMeshRenderer targetRenderer;


    [HideInInspector]
    public bool autoFindRenderer = true;


    [HideInInspector]
    public bool fitOnStart = false;


    // =========================================================
    // Fitted Mesh
    // =========================================================

    private Vector3[] _fittedLocalPositions;

    private bool _hasSurfaceFit = false;


    // =========================================================
    // Visual UV Zoom
    //
    // Geometryは変更しない。
    // =========================================================

    private float _visualZoomX = 1f;

    private float _visualZoomY = 1f;


    public int XSegments
    {
        get
        {
            return Mathf.Max(
                2,
                horizontalSegments
            );
        }
    }


    public int YSegments
    {
        get
        {
            return Mathf.Max(
                2,
                verticalSegments
            );
        }
    }


    public int SurfaceVertexCount
    {
        get
        {
            return
                (XSegments + 1)
                *
                (YSegments + 1);
        }
    }


    public bool HasSurfaceFit
    {
        get
        {
            return _hasSurfaceFit;
        }
    }


    public float VisualZoomX => _visualZoomX;

    public float VisualZoomY => _visualZoomY;


    // =========================================================
    // Visual Zoom API
    // =========================================================

    public void SetVisualZoom(
        float zoomX,
        float zoomY)
    {
        zoomX =
            Mathf.Clamp(
                zoomX,
                1f,
                3f
            );


        zoomY =
            Mathf.Clamp(
                zoomY,
                1f,
                3f
            );


        if (
            Mathf.Abs(
                _visualZoomX -
                zoomX
            )
            <
            0.0001f
            &&
            Mathf.Abs(
                _visualZoomY -
                zoomY
            )
            <
            0.0001f
        )
        {
            return;
        }


        _visualZoomX =
            zoomX;


        _visualZoomY =
            zoomY;


        SetVerticesDirty();
    }


    public void ResetVisualZoom()
    {
        SetVisualZoom(
            1f,
            1f
        );
    }


    // =========================================================
    // Surface Helpers
    // =========================================================

    public Rect GetSurfaceRect()
    {
        return GetPixelAdjustedRect();
    }


    public Vector3 GetFlatLocalPosition(
        int x,
        int y)
    {
        Rect rect =
            GetPixelAdjustedRect();


        float u =
            Mathf.Clamp01(
                x /
                (float)XSegments
            );


        float v =
            Mathf.Clamp01(
                y /
                (float)YSegments
            );


        return new Vector3(
            Mathf.Lerp(
                rect.xMin,
                rect.xMax,
                u
            ),
            Mathf.Lerp(
                rect.yMin,
                rect.yMax,
                v
            ),
            0f
        );
    }


    public Vector3 GetFlatLocalCenter()
    {
        Rect rect =
            GetPixelAdjustedRect();


        return new Vector3(
            rect.center.x,
            rect.center.y,
            0f
        );
    }


    public bool TryGetSurfaceLocalPosition(
        Vector2 normalizedPosition,
        out Vector3 localPosition)
    {
        float u = Mathf.Clamp01(normalizedPosition.x);
        float v = Mathf.Clamp01(normalizedPosition.y);
        int xCount = XSegments;
        int yCount = YSegments;

        float gridX = u * xCount;
        float gridY = v * yCount;
        int x0 = Mathf.Clamp(Mathf.FloorToInt(gridX), 0, xCount);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(gridY), 0, yCount);
        int x1 = Mathf.Min(x0 + 1, xCount);
        int y1 = Mathf.Min(y0 + 1, yCount);
        float tx = gridX - x0;
        float ty = gridY - y0;

        Vector3 p00 = GetSurfaceGridPosition(x0, y0);
        Vector3 p10 = GetSurfaceGridPosition(x1, y0);
        Vector3 p01 = GetSurfaceGridPosition(x0, y1);
        Vector3 p11 = GetSurfaceGridPosition(x1, y1);

        localPosition = Vector3.Lerp(
            Vector3.Lerp(p00, p10, tx),
            Vector3.Lerp(p01, p11, tx),
            ty
        );
        return true;
    }


    private Vector3 GetSurfaceGridPosition(int x, int y)
    {
        int expectedCount = (XSegments + 1) * (YSegments + 1);
        if (
            _hasSurfaceFit &&
            _fittedLocalPositions != null &&
            _fittedLocalPositions.Length == expectedCount
        )
        {
            return _fittedLocalPositions[y * (XSegments + 1) + x];
        }

        return GetFlatLocalPosition(x, y);
    }


    // =========================================================
    // Surface Fit
    // =========================================================

    public void ApplySurfaceFit(
        Vector3[] localPositions)
    {
        if (
            localPositions == null
            ||
            localPositions.Length !=
            SurfaceVertexCount
        )
        {
            Debug.LogWarning(
                name +
                ": Invalid surface fit data."
            );


            ClearSurfaceFit();

            return;
        }


        if (
            _fittedLocalPositions == null
            ||
            _fittedLocalPositions.Length !=
            localPositions.Length
        )
        {
            _fittedLocalPositions =
                new Vector3[
                    localPositions.Length
                ];
        }


        System.Array.Copy(
            localPositions,
            _fittedLocalPositions,
            localPositions.Length
        );


        _hasSurfaceFit =
            true;


        SetVerticesDirty();
    }


    public void ClearSurfaceFit()
    {
        _hasSurfaceFit =
            false;


        _fittedLocalPositions =
            null;


        SetVerticesDirty();
    }


    [ContextMenu("Refit Surface")]
    public void RefitSurface()
    {
        KiwiSurfaceFitter fitter =
            GetComponentInParent
            <
                KiwiSurfaceFitter
            >();


        if (fitter == null)
        {
            Transform root =
                transform.root;


            if (root != null)
            {
                fitter =
                    root.GetComponentInChildren
                    <
                        KiwiSurfaceFitter
                    >(
                        true
                    );
            }
        }


        if (fitter != null)
        {
            fitter.FitAllNow();
        }
        else
        {
            Debug.LogWarning(
                name +
                ": KiwiSurfaceFitter が見つかりません。"
            );
        }
    }


    // =========================================================
    // Mesh
    // =========================================================

    protected override void OnPopulateMesh(
        VertexHelper vh)
    {
        vh.Clear();


        Rect rect =
            GetPixelAdjustedRect();


        int xCount =
            XSegments;


        int yCount =
            YSegments;


        int expectedCount =
            (xCount + 1)
            *
            (yCount + 1);


        bool useSurface =
            _hasSurfaceFit
            &&
            _fittedLocalPositions != null
            &&
            _fittedLocalPositions.Length ==
            expectedCount;


        int index =
            0;


        for (
            int y = 0;
            y <= yCount;
            y++
        )
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


            for (
                int x = 0;
                x <= xCount;
                x++
            )
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


                Vector3 position =
                    useSurface
                        ?
                        _fittedLocalPositions[
                            index
                        ]
                        :
                        new Vector3(
                            localX,
                            localY,
                            0f
                        );


                // =============================================
                // Expression UV Zoom
                // =============================================

                float sampleU =
                    0.5f
                    +
                    (
                        u -
                        0.5f
                    )
                    /
                    _visualZoomX;


                float sampleV =
                    0.5f
                    +
                    (
                        v -
                        0.5f
                    )
                    /
                    _visualZoomY;


                Vector2 textureUV =
                    new Vector2(
                        Mathf.Lerp(
                            uvRect.xMin,
                            uvRect.xMax,
                            sampleU
                        ),
                        Mathf.Lerp(
                            uvRect.yMin,
                            uvRect.yMax,
                            sampleV
                        )
                    );


                Vector2 maskUV =
                    new Vector2(
                        u,
                        v
                    );


                UIVertex vertex =
                    UIVertex.simpleVert;


                vertex.position =
                    position;


                vertex.color =
                    color;


                vertex.uv0 =
                    textureUV;


                vertex.uv1 =
                    maskUV;


                vh.AddVert(
                    vertex
                );


                index++;
            }
        }


        int rowSize =
            xCount + 1;


        for (
            int y = 0;
            y < yCount;
            y++
        )
        {
            for (
                int x = 0;
                x < xCount;
                x++
            )
            {
                int i0 =
                    y *
                    rowSize
                    +
                    x;


                int i1 =
                    i0 + 1;


                int i2 =
                    i0 +
                    rowSize;


                int i3 =
                    i2 + 1;


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


#if UNITY_EDITOR

    protected override void OnValidate()
    {
        base.OnValidate();


        if (
            _fittedLocalPositions != null
            &&
            _fittedLocalPositions.Length !=
            SurfaceVertexCount
        )
        {
            _hasSurfaceFit =
                false;


            _fittedLocalPositions =
                null;
        }


        SetVerticesDirty();
    }

#endif
}
