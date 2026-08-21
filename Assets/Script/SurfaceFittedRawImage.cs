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


    // KIWI_V5_0_1_HEAD_LOCAL_SAMPLE_FRAME
    // Camera eye/mouth crops contain the actor's rigid head Roll. The fitted
    // 3D surface already receives that rigid Roll from KiwiFaceMotion, so the
    // source texture and semantic mask must be sampled in a de-rolled local
    // frame or Roll is visually applied twice. This rotates sampling only; it
    // never changes RectTransform, fitted geometry, or Avatar Root authority.
    private float _sampleFrameRotationDegrees = 0f;


    // KIWI_SURFACE_CONSTRAINT_API_V4_0
    // Local one-way surface attachment correction.
    //
    // This changes only which already-fitted surface coordinates the face-part
    // patch samples. It never writes the avatar/root transform and it never
    // changes the source camera crop.
    private Vector2 _surfaceConstraintOffsetNormalized =
        Vector2.zero;


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

    public float SampleFrameRotationDegrees =>
        _sampleFrameRotationDegrees;

    public Vector2 SurfaceConstraintOffsetNormalized =>
        _surfaceConstraintOffsetNormalized;


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
    // Head-local camera sample frame API
    // =========================================================

    public void SetSampleFrameRotationDegrees(
        float degrees)
    {
        if (float.IsNaN(degrees) || float.IsInfinity(degrees))
        {
            degrees = 0f;
        }

        degrees =
            Mathf.Clamp(
                Mathf.DeltaAngle(0f, degrees),
                -60f,
                60f);

        if (Mathf.Abs(
                Mathf.DeltaAngle(
                    _sampleFrameRotationDegrees,
                    degrees)) <= 0.0001f)
        {
            return;
        }

        _sampleFrameRotationDegrees =
            degrees;

        SetVerticesDirty();
    }


    public void ResetSampleFrameRotation()
    {
        SetSampleFrameRotationDegrees(0f);
    }


    // =========================================================
    // Local Surface Constraint API
    // =========================================================

    public void SetSurfaceConstraintOffsetNormalized(
        Vector2 offset)
    {
        offset.x =
            Mathf.Clamp(
                offset.x,
                -0.35f,
                0.35f);

        offset.y =
            Mathf.Clamp(
                offset.y,
                -0.35f,
                0.35f);

        if (
            (
                offset -
                _surfaceConstraintOffsetNormalized
            ).sqrMagnitude <
            0.00000001f
        )
        {
            return;
        }

        _surfaceConstraintOffsetNormalized =
            offset;

        SetVerticesDirty();
    }


    public void ResetSurfaceConstraintOffset()
    {
        SetSurfaceConstraintOffsetNormalized(
            Vector2.zero);
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
        localPosition =
            GetConstrainedSurfacePosition(
                normalizedPosition.x,
                normalizedPosition.y);

        return true;
    }


    private Vector3 GetConstrainedSurfacePosition(
        float u,
        float v)
    {
        u =
            Mathf.Clamp01(
                u +
                _surfaceConstraintOffsetNormalized.x);

        v =
            Mathf.Clamp01(
                v +
                _surfaceConstraintOffsetNormalized.y);

        int xCount =
            XSegments;

        int yCount =
            YSegments;

        float gridX =
            u *
            xCount;

        float gridY =
            v *
            yCount;

        int x0 =
            Mathf.Clamp(
                Mathf.FloorToInt(
                    gridX),
                0,
                xCount);

        int y0 =
            Mathf.Clamp(
                Mathf.FloorToInt(
                    gridY),
                0,
                yCount);

        int x1 =
            Mathf.Min(
                x0 + 1,
                xCount);

        int y1 =
            Mathf.Min(
                y0 + 1,
                yCount);

        float tx =
            gridX -
            x0;

        float ty =
            gridY -
            y0;

        Vector3 p00 =
            GetSurfaceGridPosition(
                x0,
                y0);

        Vector3 p10 =
            GetSurfaceGridPosition(
                x1,
                y0);

        Vector3 p01 =
            GetSurfaceGridPosition(
                x0,
                y1);

        Vector3 p11 =
            GetSurfaceGridPosition(
                x1,
                y1);

        return
            Vector3.Lerp(
                Vector3.Lerp(
                    p00,
                    p10,
                    tx),
                Vector3.Lerp(
                    p01,
                    p11,
                    tx),
                ty);
    }


    private Vector3 GetSurfaceGridPosition(
        int x,
        int y)
    {
        int expectedCount =
            (XSegments + 1) *
            (YSegments + 1);

        if (
            _hasSurfaceFit &&
            _fittedLocalPositions != null &&
            _fittedLocalPositions.Length ==
                expectedCount
        )
        {
            return
                _fittedLocalPositions[
                    y *
                    (XSegments + 1) +
                    x
                ];
        }

        return
            GetFlatLocalPosition(
                x,
                y);
    }


    // =========================================================
    // Head-local sample-frame math
    // =========================================================

    private static Vector2 RotateCropLocalCoordinate(
        Vector2 coordinate,
        float sine,
        float cosine,
        float cropPixelAspect)
    {
        Vector2 delta =
            coordinate -
            new Vector2(0.5f, 0.5f);

        delta.x *=
            cropPixelAspect;

        Vector2 rotated =
            new Vector2(
                cosine * delta.x -
                sine * delta.y,
                sine * delta.x +
                cosine * delta.y
            );

        rotated.x /=
            cropPixelAspect;

        return
            new Vector2(0.5f, 0.5f) +
            rotated;
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


        _surfaceConstraintOffsetNormalized =
            Vector2.zero;


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


        // Precompute the head-local sample-frame transform once per mesh
        // rebuild. Do not repeat texture aspect lookup or Sin/Cos per vertex.
        bool rotateSampleFrame =
            Mathf.Abs(_sampleFrameRotationDegrees) > 0.0001f;

        float sampleFrameSine = 0f;
        float sampleFrameCosine = 1f;
        float sampleFrameCropPixelAspect = 1f;

        if (rotateSampleFrame)
        {
            float radians =
                _sampleFrameRotationDegrees *
                Mathf.Deg2Rad;

            sampleFrameSine =
                Mathf.Sin(radians);

            sampleFrameCosine =
                Mathf.Cos(radians);

            Texture sourceTexture =
                texture != null
                    ? texture
                    : mainTexture;

            float textureAspect =
                sourceTexture != null &&
                sourceTexture.width > 0 &&
                sourceTexture.height > 0
                    ? sourceTexture.width /
                        (float)sourceTexture.height
                    : 1f;

            sampleFrameCropPixelAspect =
                Mathf.Abs(uvRect.height) > 0.000001f
                    ? Mathf.Abs(uvRect.width) *
                        textureAspect /
                        Mathf.Abs(uvRect.height)
                    : 1f;

            sampleFrameCropPixelAspect =
                Mathf.Clamp(
                    sampleFrameCropPixelAspect,
                    0.05f,
                    20f);
        }


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
                        GetConstrainedSurfacePosition(
                            u,
                            v)
                        :
                        new Vector3(
                            localX,
                            localY,
                            0f
                        );


                // =============================================
                // Expression UV Zoom
                // =============================================

                Vector2 sampleLocal =
                    new Vector2(
                        0.5f +
                        (u - 0.5f) /
                        _visualZoomX,
                        0.5f +
                        (v - 0.5f) /
                        _visualZoomY
                    );


                // KIWI_V5_0_1_HEAD_LOCAL_SAMPLE_FRAME
                // Rotate both texture sampling and the mask-evaluation UV by
                // the same rigid camera-frame angle. Polygon points remain in
                // source/crop coordinates, while the fitted 3D surface owns the
                // visible head Roll exactly once.
                if (rotateSampleFrame)
                {
                    sampleLocal =
                        RotateCropLocalCoordinate(
                            sampleLocal,
                            sampleFrameSine,
                            sampleFrameCosine,
                            sampleFrameCropPixelAspect);
                }

                Vector2 textureUV =
                    new Vector2(
                        Mathf.Lerp(
                            uvRect.xMin,
                            uvRect.xMax,
                            sampleLocal.x
                        ),
                        Mathf.Lerp(
                            uvRect.yMin,
                            uvRect.yMax,
                            sampleLocal.y
                        )
                    );


                Vector2 maskUV =
                    rotateSampleFrame
                        ? RotateCropLocalCoordinate(
                            new Vector2(u, v),
                            sampleFrameSine,
                            sampleFrameCosine,
                            sampleFrameCropPixelAspect)
                        : new Vector2(u, v);


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


            _surfaceConstraintOffsetNormalized =
                Vector2.zero;
        }


        SetVerticesDirty();
    }

#endif
}
