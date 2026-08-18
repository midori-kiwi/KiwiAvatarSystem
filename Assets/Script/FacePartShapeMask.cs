using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(850)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SurfaceFittedRawImage))]
public class FacePartShapeMask : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: contour and visibility jump at Landmarker cadence. OFF uses high-response render-rate interpolation to remove eye/mouth flicker.")]
    public bool strictLandmarkerTracking = false;


    // =========================================================
    // Face Part
    // =========================================================

    public enum FacePartType
    {
        Auto = 0,
        Eye = 1,
        Mouth = 2
    }


    // =========================================================
    // References
    // =========================================================

    [Header("References")]

    public FaceLandmarkerRunner runner;

    public KiwiFaceMotion faceMotion;

    public FacePartCropper cropper;

    public Material baseMaterial;


    [Header("Coherent Surface Visibility")]

    [Tooltip("Render all face parts above the head depth as one overlay, then fade the group together before it reaches the back side.")]
    public bool stabilizeSurfaceOcclusion = true;

    [Range(0f, 80f)]
    public float fullVisibilityYaw = 48f;

    [Range(1f, 90f)]
    public float hiddenVisibilityYaw = 58f;


    // =========================================================
    // Face Part
    // =========================================================

    [Header("Face Part")]

    public FacePartType facePart =
        FacePartType.Auto;


    // =========================================================
    // Contour
    // =========================================================

    [Header("Contour")]

    [Range(-0.10f, 0.50f)]
    public float eyeContourMargin =
        0.10f;


    [Range(-0.10f, 0.20f)]
    public float mouthContourMargin =
        0.020f;


    // =========================================================
    // Independent Blink
    // =========================================================

    [Header("Independent Eye Blink")]

    public bool useBlendshapeBlink =
        true;


    [Range(0f, 1f)]
    public float blinkShapeStart =
        0.15f;


    [Range(0f, 1f)]
    public float blinkFadeStart =
        0.45f;


    [Range(0f, 1f)]
    public float blinkHideThreshold =
        0.72f;


    // =========================================================
    // Geometry fallback
    // =========================================================

    [Header("Geometry Close Fallback")]

    public bool useGeometryCloseFallback =
        true;


    [Range(0.05f, 0.40f)]
    public float geometryCloseStart =
        0.18f;


    [Range(0.01f, 0.25f)]
    public float geometryCloseFull =
        0.095f;


    // =========================================================
    // Closed Eye
    // =========================================================

    [Header("Natural Closed Eye")]

    [Range(0.001f, 0.30f)]
    public float closedEyeThickness =
        0.025f;


    [Range(-0.30f, 0.20f)]
    public float closedEyeContourMargin =
        -0.050f;


    [Range(0.001f, 0.10f)]
    public float closedEyeFeather =
        0.040f;


    [Range(0.3f, 3f)]
    public float closeCurve =
        1.20f;


    [Header("Complete Close")]

    public bool hardHideWhenFullyClosed =
        true;


    [Header("Flicker-Free Eye Visibility")]

    [Tooltip("Requires coherent blink evidence before reducing eye opacity. Position and contour tracking stay immediate.")]
    public bool stabilizeEyeVisibility =
        true;


    [Tooltip("Consecutive coherent Landmarker results required to enter the closed-eye visibility state.")]
    [Range(1, 4)]
    public int eyeCloseConfirmationSamples =
        2;


    [Tooltip("Consecutive clearly-open results required to leave the closed-eye visibility state.")]
    [Range(1, 3)]
    public int eyeOpenConfirmationSamples =
        1;


    [Tooltip("Minimum opacity retained for a confirmed closed eye. The compressed contour remains visible as an eyelid line.")]
    [Range(0.10f, 1f)]
    public float closedEyeVisibilityFloor =
        0.35f;


    // =========================================================
    // Edge
    //
    // 現在の基準値 0.04
    // =========================================================

    [Header("Soft Edge")]

    [Range(0.001f, 0.10f)]
    public float feather =
        0.040f;


    // =========================================================
    // Stability
    // =========================================================

    [Header("Low Latency Stability")]

    [Range(0f, 0.005f)]
    public float microJitterDeadZone =
        0.00055f;


    [Tooltip("Render-rate contour response. 180 blends a 16-20 Hz landmark step across roughly two display frames without visible lag.")]
    [Range(30f, 400f)]
    public float contourRenderResponse =
        110f;

    [Tooltip("Keep the rendered contour in crop-local coordinates so eye/mouth masks cannot lag behind a moving UV crop.")]
    public bool lockContourToMovingCrop = true;

    [Tooltip("Minimum normalized clearance between the contour and the crop boundary.")]
    [Range(0f, 0.15f)]
    public float cropLocalSafetyMargin = 0.015f;


    [Tooltip("Seconds used to hide an eye at full blink. Applied every render frame instead of as a binary sample step.")]
    [Range(0.005f, 0.10f)]
    public float eyeHideFadeSeconds =
        0.025f;


    [Tooltip("Seconds used to restore an eye after a blink.")]
    [Range(0.005f, 0.15f)]
    public float eyeShowFadeSeconds =
        0.045f;


    // =========================================================
    // Eye Matching
    // =========================================================

    [Header("Automatic Eye Matching")]

    public bool automaticEyeMatching =
        true;


    public bool lockEyeAssignment =
        true;


    [Range(1f, 2f)]
    public float eyeSwitchHysteresis =
        1.30f;


    // =========================================================
    // ★ Mouth Height Lock
    //
    // uvRectは絶対に変更しない。
    //
    // Camera samplingだけ補正する。
    // =========================================================

    [Header("Mouth Height Lock")]

    [Tooltip(
        "正面を基準に口の表示高さを固定"
    )]
    public bool lockMouthHeight =
        false;


    [Tooltip(
        "正面基準を取得するまでの待機時間"
    )]
    [Range(0f, 2f)]
    public float mouthCalibrationDelay =
        0.40f;


    [Tooltip(
        "基準位置を平均するサンプル数"
    )]
    [Range(1, 30)]
    public int mouthCalibrationSamples =
        10;


    [Tooltip(
        "最大補正量。Crop高さに対する割合"
    )]
    [Range(0.05f, 0.80f)]
    public float maximumMouthHeightCorrection =
        0.40f;


    [Tooltip(
        "この程度の上下変化は補正しない"
    )]
    [Range(0f, 0.01f)]
    public float mouthHeightDeadZone =
        0.00015f;


    // =========================================================
    // Mouth camera-edge visibility
    // =========================================================

    [Header("Mouth Camera Edge Visibility")]

    [Tooltip("Fades the complete mouth out when the actual outer-lip contour reaches the camera image edge.")]
    public bool hideMouthOutsideTexture =
        true;


    [Tooltip("Keeps the mouth visible while a blink is in progress. Blink samples cannot advance or retain an edge-hide decision.")]
    public bool protectMouthDuringBlink =
        true;


    [Tooltip("Either eye at or above this BlendShape score activates mouth protection.")]
    [Range(0.10f, 0.90f)]
    public float mouthBlinkProtectionThreshold =
        0.35f;


    [Tooltip("Landmark fallback used only when BlendShapes are unavailable. Lower eye aspect means more closed.")]
    [Range(0.05f, 0.25f)]
    public float mouthBlinkGeometryThreshold =
        0.13f;


    [Tooltip("Hide threshold measured from the actual outer-lip contour to the nearest texture edge.")]
    [Range(0f, 0.05f)]
    public float mouthHideEdgeMargin =
        0.003f;


    [Tooltip("The mouth must return this far inside the texture before it is shown again. Keep this above the hide margin to prevent flicker.")]
    [Range(0f, 0.10f)]
    public float mouthShowEdgeMargin =
        0.015f;


    [Tooltip("Consecutive Landmarker results required before edge hiding. Two rejects a one-result edge spike without delaying normal tracking.")]
    [Range(1, 6)]
    public int mouthEdgeHideConfirmationSamples =
        3;


    [Tooltip("Minimum time an incomplete mouth must persist before it can fade out. During this grace period the last complete crop and contour are held.")]
    [Range(0f, 0.30f)]
    public float mouthEdgeHideGraceSeconds =
        0.12f;


    [Tooltip("Consecutive safe results required before a hidden or held mouth is released back to live tracking.")]
    [Range(1, 4)]
    public int mouthEdgeShowConfirmationSamples =
        2;


    [Tooltip("Seconds used to hide the mouth after it reaches a camera edge.")]
    [Range(0.005f, 0.20f)]
    public float mouthHideFadeSeconds =
        0.040f;


    [Tooltip("Seconds used to restore the mouth after the full contour is safely inside the camera image.")]
    [Range(0.005f, 0.30f)]
    public float mouthShowFadeSeconds =
        0.060f;


    // =========================================================
    // Debug
    // =========================================================

    [Header("Debug")]

    public bool logMatching =
        false;


    [SerializeField]
    private string debugSelectedEye =
        "-";


    [SerializeField]
    private float debugBlinkScore =
        0f;


    [SerializeField]
    private float debugEyeOpenness =
        0f;


    [SerializeField]
    private float debugCloseAmount =
        0f;


    [SerializeField]
    private float debugVisibility =
        1f;


    [SerializeField]
    private bool debugMouthCalibrated =
        false;


    [SerializeField]
    private float debugMouthReferenceV =
        0.5f;


    [SerializeField]
    private float debugMouthOffsetY =
        0f;


    [SerializeField]
    private float debugMouthEdgeClearance =
        1f;


    [SerializeField]
    private bool debugMouthHiddenByFrame =
        false;


    [SerializeField]
    private float debugMouthFrameVisibility =
        1f;


    [SerializeField]
    private bool debugMouthProtectedByBlink =
        false;


    // =========================================================
    // Constants
    // =========================================================

    private const int MaxPoints =
        48;


    // =========================================================
    // MediaPipe Eye A
    // =========================================================

    private static readonly int[] EyeAIndices =
    {
        362,
        398,
        384,
        385,
        386,
        387,
        388,
        466,

        263,

        249,
        390,
        373,
        374,
        380,
        381,
        382
    };


    // =========================================================
    // MediaPipe Eye B
    // =========================================================

    private static readonly int[] EyeBIndices =
    {
        33,
        246,
        161,
        160,
        159,
        158,
        157,
        173,

        133,

        155,
        154,
        153,
        145,
        144,
        163,
        7
    };


    // =========================================================
    // Mouth
    // =========================================================

    private static readonly int[] MouthIndices =
    {
        61,
        185,
        40,
        39,
        37,
        0,
        267,
        269,
        270,
        409,
        291,

        375,
        321,
        405,
        314,
        17,
        84,
        181,
        91,
        146
    };


    private const int MouthCornerA =
        61;


    private const int MouthCornerB =
        291;


    // =========================================================
    // Runtime
    // =========================================================

    private SurfaceFittedRawImage _image;


    private Material _runtimeMaterial;


    private Vector2[] _landmarks =
        new Vector2[478];


    private readonly Vector2[] _rawContour =
        new Vector2[24];


    private readonly Vector2[] _stableContour =
        new Vector2[24];


    private readonly Vector2[] _workingContour =
        new Vector2[24];


    private readonly Vector2[] _smoothContour =
        new Vector2[MaxPoints];


    private readonly Vector4[] _shaderPoints =
        new Vector4[MaxPoints];


    private readonly Vector4[] _targetShaderPoints =
        new Vector4[MaxPoints];


    private readonly Vector4[] _uploadShaderPoints =
        new Vector4[MaxPoints];


    private bool _maskPointsAreCropLocal =
        false;


    private int _renderedPointCount =
        0;


    private int _targetPointCount =
        0;


    private bool _hasRenderedContour =
        false;


    private bool _contourUploadDirty =
        false;


    private bool _hasStableContour =
        false;


    private long _lastTimestamp =
        -1;


    // =========================================================
    // Eye mapping
    // =========================================================

    private int _currentEyeSet =
        -1;


    private bool _currentMirrorX =
        false;


    private bool _currentFlipY =
        false;


    private bool _eyeAssignmentLocked =
        false;


    // =========================================================
    // Mouth orientation
    // =========================================================

    private bool _mouthOrientationResolved =
        false;


    private bool _mouthMirrorX =
        false;


    private bool _mouthFlipY =
        false;


    // =========================================================
    // Mouth Height Calibration
    // =========================================================

    private float _enableTime;


    private bool _mouthHeightCalibrated =
        false;


    private int _mouthCalibrationCount =
        0;


    private long _lastMouthCalibrationTimestamp =
        -1;


    private float _mouthReferenceVSum =
        0f;


    private float _mouthReferenceLocalV =
        0.5f;


    private float _heldMouthOffsetY =
        0f;


    // =========================================================
    // Mouth camera-edge visibility
    // =========================================================

    private float _mouthFrameVisibility =
        1f;


    private float _mouthFrameVisibilityTarget =
        1f;


    private int _mouthEdgeViolationSamples =
        0;


    private int _mouthEdgeRecoverySamples =
        0;


    private float _mouthEdgeViolationStartTime =
        -1f;


    private bool _holdMouthVisual =
        false;


    private bool _hasSafeMouthUvRect =
        false;


    private Rect _safeMouthUvRect;


    private float _eyeFrameVisibility =
        1f;


    private float _eyeFrameVisibilityTarget =
        1f;


    private int _eyeCloseEvidenceSamples =
        0;


    private int _eyeOpenEvidenceSamples =
        0;


    private bool _eyeClosureConfirmed =
        false;

    private float _lastAppliedFrameVisibility =
        float.NaN;


    private float _lastAppliedPoseVisibility =
        float.NaN;


    private Vector2 _lastAppliedVisibleScale =
        new Vector2(float.NaN, float.NaN);


    // =========================================================
    // Shader IDs
    // =========================================================

    private static readonly int MaskPointsId =
        Shader.PropertyToID(
            "_MaskPoints"
        );


    private static readonly int MaskPointCountId =
        Shader.PropertyToID(
            "_MaskPointCount"
        );


    private static readonly int FeatherId =
        Shader.PropertyToID(
            "_Feather"
        );


    private static readonly int VisibilityId =
        Shader.PropertyToID(
            "_MaskVisibility"
        );


    private static readonly int PoseVisibilityId =
        Shader.PropertyToID(
            "_PoseVisibility"
        );


    private static readonly int SampleOffsetId =
        Shader.PropertyToID(
            "_SampleOffset"
        );


    private static readonly int SampleScaleId =
        Shader.PropertyToID("_SampleScale");


    private static readonly int SampleScaleXYId =
        Shader.PropertyToID("_SampleScaleXY");


    private static readonly int SamplePivotId =
        Shader.PropertyToID("_SamplePivot");


    private static readonly int SampleRotationRadId =
        Shader.PropertyToID("_SampleRotationRad");


    private static readonly int SourceAspectId =
        Shader.PropertyToID("_SourceAspect");


    // =========================================================
    // Rendered contour bounds
    // =========================================================

    public bool TryGetRenderedContourScreenRect(
        Camera camera,
        out Rect screenRect)
    {
        Vector2 currentScale = _runtimeMaterial != null
            ? (Vector2)_runtimeMaterial.GetVector(SampleScaleXYId)
            : Vector2.one;

        return TryGetRenderedContourScreenRect(
            camera,
            currentScale,
            out screenRect
        );
    }


    public bool IsRenderedContourInsideSurface(
        Vector2 sampleScaleXY,
        float safetyMargin)
    {
        if (
            !_hasRenderedContour ||
            _renderedPointCount < 3 ||
            _image == null ||
            _runtimeMaterial == null
        )
        {
            return true;
        }


        sampleScaleXY.x = Mathf.Max(0.01f, sampleScaleXY.x);
        sampleScaleXY.y = Mathf.Max(0.01f, sampleScaleXY.y);
        float uniformScale = Mathf.Max(
            0.01f,
            _runtimeMaterial.GetFloat(SampleScaleId)
        );
        Vector2 pivot = _runtimeMaterial.GetVector(SamplePivotId);
        Vector2 offset = _runtimeMaterial.GetVector(SampleOffsetId);
        float rotation = _runtimeMaterial.GetFloat(SampleRotationRadId);
        float aspect = Mathf.Max(
            0.0001f,
            _runtimeMaterial.GetFloat(SourceAspectId)
        );
        Rect sourceRect = _image.uvRect;
        if (
            Mathf.Abs(sourceRect.width) < 0.000001f ||
            Mathf.Abs(sourceRect.height) < 0.000001f
        )
        {
            return true;
        }


        for (int i = 0; i < _renderedPointCount; i++)
        {
            Vector2 sampleUv =
                _maskPointsAreCropLocal
                    ? KiwiFacePartMaskCoherenceMath.FromCropLocal(
                        _shaderPoints[i],
                        sourceRect
                    )
                    : (Vector2)_shaderPoints[i];
            Vector2 normalizedSurface =
                KiwiFacePartSurfaceSafetyMath.CalculateNormalizedSurface(
                    sampleUv,
                    sourceRect,
                    pivot,
                    offset,
                    uniformScale,
                    sampleScaleXY,
                    rotation,
                    aspect,
                    new Vector2(_image.VisualZoomX, _image.VisualZoomY)
                );


            if (
                !KiwiFacePartSurfaceSafetyMath.IsInsideSurface(
                    normalizedSurface,
                    safetyMargin
                )
            )
            {
                return false;
            }
        }


        return true;
    }


    public void SetVisibleScale(float scaleX, float scaleY)
    {
        if (_runtimeMaterial == null)
        {
            return;
        }


        Vector2 visibleScale = new Vector2(
            Mathf.Clamp(scaleX, 0.50f, 3.00f),
            Mathf.Clamp(scaleY, 0.50f, 3.00f)
        );


        if (
            Mathf.Abs(visibleScale.x - _lastAppliedVisibleScale.x) < 0.0001f &&
            Mathf.Abs(visibleScale.y - _lastAppliedVisibleScale.y) < 0.0001f
        )
        {
            return;
        }


        _runtimeMaterial.SetFloat(SampleScaleId, 1f);
        _runtimeMaterial.SetVector(
            SampleScaleXYId,
            new Vector4(
                1f / visibleScale.x,
                1f / visibleScale.y,
                0f,
                0f
            )
        );


        _lastAppliedVisibleScale = visibleScale;
    }


    public bool TryGetRenderedContourScreenRect(
        Camera camera,
        Vector2 sampleScaleXY,
        out Rect screenRect)
    {
        screenRect = default;
        if (
            !_hasRenderedContour ||
            _renderedPointCount < 3 ||
            _image == null ||
            _runtimeMaterial == null
        )
        {
            return false;
        }

        sampleScaleXY.x = Mathf.Max(0.01f, sampleScaleXY.x);
        sampleScaleXY.y = Mathf.Max(0.01f, sampleScaleXY.y);
        float uniformScale = Mathf.Max(
            0.01f,
            _runtimeMaterial.GetFloat(SampleScaleId)
        );
        Vector2 pivot = _runtimeMaterial.GetVector(SamplePivotId);
        Vector2 offset = _runtimeMaterial.GetVector(SampleOffsetId);
        float rotation = _runtimeMaterial.GetFloat(SampleRotationRadId);
        float aspect = Mathf.Max(
            0.0001f,
            _runtimeMaterial.GetFloat(SourceAspectId)
        );
        Rect sourceRect = _image.uvRect;
        if (
            Mathf.Abs(sourceRect.width) < 0.000001f ||
            Mathf.Abs(sourceRect.height) < 0.000001f
        )
        {
            return false;
        }

        bool initialized = false;
        float minX = 0f;
        float minY = 0f;
        float maxX = 0f;
        float maxY = 0f;

        for (int i = 0; i < _renderedPointCount; i++)
        {
            Vector2 sampleUv =
                _maskPointsAreCropLocal
                    ? KiwiFacePartMaskCoherenceMath.FromCropLocal(
                        _shaderPoints[i],
                        sourceRect
                    )
                    : (Vector2)_shaderPoints[i];
            Vector2 normalizedSurface =
                KiwiFacePartSurfaceSafetyMath.CalculateNormalizedSurface(
                    sampleUv,
                    sourceRect,
                    pivot,
                    offset,
                    uniformScale,
                    sampleScaleXY,
                    rotation,
                    aspect,
                    new Vector2(_image.VisualZoomX, _image.VisualZoomY)
                );

            if (!_image.TryGetSurfaceLocalPosition(normalizedSurface, out Vector3 local))
            {
                continue;
            }

            Vector3 world = _image.rectTransform.TransformPoint(local);
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(camera, world);
            if (!initialized)
            {
                minX = maxX = screen.x;
                minY = maxY = screen.y;
                initialized = true;
            }
            else
            {
                minX = Mathf.Min(minX, screen.x);
                minY = Mathf.Min(minY, screen.y);
                maxX = Mathf.Max(maxX, screen.x);
                maxY = Mathf.Max(maxY, screen.y);
            }
        }

        if (!initialized)
        {
            return false;
        }

        screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }


    // =========================================================
    // Enable
    // =========================================================

    private void OnEnable()
    {
        Application.onBeforeRender +=
            RefreshPoseVisibility;


        _enableTime =
            Time.unscaledTime;


        _image =
            GetComponent<
                SurfaceFittedRawImage
            >();


        CreateMaterial();


        ResetContour();
    }


    // =========================================================
    // Start
    // =========================================================

    private void Start()
    {
        if (runner == null)
        {
            runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner
                >();
        }


        if (faceMotion == null)
        {
            faceMotion =
                FindFirstObjectByType<
                    KiwiFaceMotion
                >();
        }


        if (cropper == null)
        {
            cropper =
                FindFirstObjectByType<
                    FacePartCropper
                >();
        }


        CreateMaterial();


        ResetContour();
    }


    // =========================================================
    // LateUpdate
    // =========================================================

    private void LateUpdate()
    {
        if (
            runner == null ||
            _image == null
        )
        {
            return;
        }


        CreateMaterial();


        if (_runtimeMaterial == null)
        {
            return;
        }


        FacePartType resolvedPart =
            ResolveFacePart();


        int landmarkCount =
            0;


        long timestamp =
            0;


        bool valid =
            runner.TryGetLatestLandmarksIfChanged(
                ref _landmarks,
                _lastTimestamp,
                out landmarkCount,
                out timestamp,
                out _
            );


        if (!valid)
        {
            RenderFrameState(resolvedPart);
            return;
        }


        FaceExpressionData expression =
            default;


        long expressionTimestamp =
            0;


        bool hasExpression =
            runner.TryGetLatestExpressionData(
                out expression,
                out expressionTimestamp
            );


        hasExpression =
            hasExpression &&
            expression.isValid;


        FacePartType part =
            resolvedPart;


        Rect uvRect =
            _image.uvRect;


        Rect contourReferenceRect =
            uvRect;


        bool useCropLocalContour =
            lockContourToMovingCrop &&
            cropper != null &&
            cropper.TryGetSampleRect(
                _image,
                out contourReferenceRect
            );


        int[] contourIndices;


        bool mirrorX;

        bool flipY;


        int selectedEye =
            -1;


        // =====================================================
        // Eye
        // =====================================================

        if (
            part ==
            FacePartType.Eye
        )
        {
            ResolveEye(
                uvRect,
                landmarkCount,
                out contourIndices,
                out mirrorX,
                out flipY,
                out selectedEye
            );


            // EyeにはSampling補正なし。
            SetSampleOffset(
                0f,
                0f
            );
        }

        // =====================================================
        // Mouth
        // =====================================================

        else
        {
            contourIndices =
                MouthIndices;


            ResolveMouthOrientation(
                uvRect,
                landmarkCount,
                out mirrorX,
                out flipY
            );


            bool mouthProtectedByBlink =
                IsMouthBlinkProtectionActive(
                    landmarkCount,
                    hasExpression,
                    expression
                );


            UpdateMouthFrameVisibilityTarget(
                landmarkCount,
                mirrorX,
                flipY,
                mouthProtectedByBlink
            );


            // FacePartCropper updates first. While an edge sample is being
            // confirmed, restore the last complete crop here and keep the
            // matching contour unchanged. This prevents transparent overscan
            // from flashing without adding any filter to normal mouth motion.
            if (
                _holdMouthVisual &&
                _hasSafeMouthUvRect
            )
            {
                _image.uvRect =
                    _safeMouthUvRect;
            }


            // ★口の高さ補正
            if (strictLandmarkerTracking || !lockMouthHeight)
            {
                _heldMouthOffsetY = 0f;
                SetSampleOffset(0f, 0f);
            }
            else
            {
                UpdateMouthHeightLock(
                    uvRect,
                    mirrorX,
                    flipY,
                    timestamp
                );
            }
        }


        if (
            contourIndices == null ||
            contourIndices.Length <
            3
        )
        {
            return;
        }


        if (
            part == FacePartType.Mouth &&
            _holdMouthVisual &&
            _hasRenderedContour
        )
        {
            _lastTimestamp =
                timestamp;


            RenderFrameState(part);
            return;
        }


        int rawCount =
            Mathf.Min(
                contourIndices.Length,
                _rawContour.Length
            );


        // =====================================================
        // Read contour
        // =====================================================

        for (
            int i = 0;
            i < rawCount;
            i++
        )
        {
            int index =
                contourIndices[i];


            if (
                index < 0 ||
                index >=
                landmarkCount
            )
            {
                return;
            }


            Vector2 p =
                _landmarks[index];


            _rawContour[i] =
                ApplyOrientation(
                    p,
                    mirrorX,
                    flipY
                );
        }


        // =====================================================
        // Zero-latency micro jitter hold
        // =====================================================

        if (strictLandmarkerTracking || !_hasStableContour)
        {
            for (int i = 0; i < rawCount; i++)
            {
                _stableContour[i] = _rawContour[i];
            }

            _hasStableContour = true;
        }
        else if (
            KiwiFacePartContourStabilityMath.ShouldUpdateContour(
                _stableContour,
                _rawContour,
                rawCount,
                microJitterDeadZone,
                microJitterDeadZone * 0.80f
            )
        )
        {
            for (int i = 0; i < rawCount; i++)
            {
                _stableContour[i] = _rawContour[i];
            }
        }


        for (
            int i = 0;
            i < rawCount;
            i++
        )
        {
            _workingContour[i] =
                _stableContour[i];
        }


        Vector2 center =
            CalculateContourCenter(
                _workingContour,
                rawCount
            );


        float finalFeather =
            feather;


        float visibility =
            1f;


        bool hardClosed =
            false;


        // =====================================================
        // Eye
        // =====================================================

        if (
            part ==
            FacePartType.Eye
        )
        {
            ProcessEyeClosing(
                selectedEye,
                hasExpression,
                expression,
                _workingContour,
                rawCount,
                center,
                ref finalFeather,
                ref visibility,
                ref hardClosed
            );


            _eyeFrameVisibilityTarget =
                visibility;
        }

        // =====================================================
        // Mouth
        // =====================================================

        else
        {
            ApplyContourMargin(
                _workingContour,
                rawCount,
                center,
                mouthContourMargin
            );


            visibility =
                _mouthFrameVisibility;


            hardClosed =
                false;


            debugSelectedEye =
                "-";


            debugBlinkScore =
                0f;


            debugEyeOpenness =
                0f;


            debugCloseAmount =
                0f;


            debugVisibility =
                visibility;
        }


        // =====================================================
        // Spatial smoothing
        // =====================================================

        int smoothCount =
            BuildChaikinContour(
                _workingContour,
                rawCount,
                _smoothContour
            );


        // =====================================================
        // Upload
        // =====================================================

        SetContourTarget(
            smoothCount,
            contourReferenceRect,
            useCropLocalContour
        );


        // Canvas alpha remains continuous. Complete blinks now fade through the
        // material at render cadence instead of switching the whole eye on/off
        // only when a new Landmarker sample arrives.
        _image.canvasRenderer.SetAlpha(1f);


        // =====================================================
        // Feather
        // =====================================================

        float cropSize =
            Mathf.Max(
                0.0001f,
                Mathf.Min(
                    Mathf.Abs(
                        uvRect.width
                    ),
                    Mathf.Abs(
                        uvRect.height
                    )
                )
            );


        _runtimeMaterial.SetFloat(
            FeatherId,
            finalFeather *
            cropSize
        );


        _lastTimestamp =
            timestamp;


        RenderFrameState(part);
    }


    private void SetContourTarget(
        int count,
        Rect referenceRect,
        bool cropLocal)
    {
        bool coordinateModeChanged =
            _maskPointsAreCropLocal != cropLocal;


        _maskPointsAreCropLocal =
            cropLocal;


        _targetPointCount =
            Mathf.Clamp(count, 0, MaxPoints);


        for (int i = 0; i < _targetPointCount; i++)
        {
            Vector2 point =
                cropLocal
                    ? KiwiFacePartMaskCoherenceMath.ToCropLocal(
                        _smoothContour[i],
                        referenceRect,
                        cropLocalSafetyMargin
                    )
                    : _smoothContour[i];


            _targetShaderPoints[i] =
                new Vector4(point.x, point.y, 0f, 0f);
        }


        for (int i = _targetPointCount; i < MaxPoints; i++)
        {
            _targetShaderPoints[i] =
                Vector4.zero;
        }


        if (
            strictLandmarkerTracking ||
            !_hasRenderedContour ||
            coordinateModeChanged ||
            _renderedPointCount != _targetPointCount
        )
        {
            CopyTargetContourToRendered();
        }
    }


    private void CopyTargetContourToRendered()
    {
        for (int i = 0; i < MaxPoints; i++)
        {
            _shaderPoints[i] =
                _targetShaderPoints[i];
        }


        _renderedPointCount =
            _targetPointCount;


        _hasRenderedContour =
            _renderedPointCount > 0;


        _contourUploadDirty =
            _hasRenderedContour;
    }


    private void RenderFrameState(
        FacePartType part)
    {
        AdvanceRenderedContour();
        AdvanceFrameVisibility(part);
        RefreshPoseVisibility();


        if (
            _hasRenderedContour &&
            (_contourUploadDirty || _maskPointsAreCropLocal)
        )
        {
            Rect renderedRect =
                _image != null
                    ? _image.uvRect
                    : new Rect(0f, 0f, 1f, 1f);


            for (int i = 0; i < MaxPoints; i++)
            {
                Vector2 point =
                    _shaderPoints[i];


                if (
                    _maskPointsAreCropLocal &&
                    i < _renderedPointCount
                )
                {
                    point =
                        KiwiFacePartMaskCoherenceMath.FromCropLocal(
                            point,
                            renderedRect
                        );
                }


                _uploadShaderPoints[i] =
                    new Vector4(
                        point.x,
                        point.y,
                        0f,
                        0f
                    );
            }


            _runtimeMaterial.SetVectorArray(
                MaskPointsId,
                _uploadShaderPoints
            );


            _runtimeMaterial.SetFloat(
                MaskPointCountId,
                _renderedPointCount
            );


            _contourUploadDirty =
                false;
        }
    }


    private void RefreshPoseVisibility()
    {
        if (_runtimeMaterial == null)
        {
            return;
        }


        float poseVisibility =
            !stabilizeSurfaceOcclusion || faceMotion == null
                ? 1f
                : KiwiFacePartVisibilityMath.CalculatePoseVisibility(
                    faceMotion.RenderedYawDegrees,
                    fullVisibilityYaw,
                    hiddenVisibilityYaw
                );


        if (
            float.IsNaN(_lastAppliedPoseVisibility) ||
            Mathf.Abs(
                _lastAppliedPoseVisibility - poseVisibility
            ) > 0.0001f
        )
        {
            _runtimeMaterial.SetFloat(
                PoseVisibilityId,
                poseVisibility
            );


            _lastAppliedPoseVisibility =
                poseVisibility;
        }
    }


    private void AdvanceRenderedContour()
    {
        if (!_hasRenderedContour)
        {
            return;
        }


        if (
            strictLandmarkerTracking ||
            _renderedPointCount != _targetPointCount
        )
        {
            CopyTargetContourToRendered();
            return;
        }


        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f
            );


        float responseT =
            1f -
            Mathf.Exp(
                -Mathf.Max(1f, contourRenderResponse) *
                dt
            );


        bool changed =
            false;


        for (int i = 0; i < _renderedPointCount; i++)
        {
            Vector4 delta =
                _targetShaderPoints[i] -
                _shaderPoints[i];


            if (delta.sqrMagnitude <= 0.0000000001f)
            {
                changed |=
                    delta.sqrMagnitude > 0f;


                _shaderPoints[i] =
                    _targetShaderPoints[i];
                continue;
            }


            _shaderPoints[i] +=
                delta * responseT;


            changed =
                true;
        }


        _contourUploadDirty |=
            changed;
    }


    private void AdvanceFrameVisibility(
        FacePartType part)
    {
        float dt =
            Mathf.Min(
                Time.unscaledDeltaTime,
                0.05f
            );


        float visibility;


        if (part == FacePartType.Eye)
        {
            _eyeFrameVisibility =
                strictLandmarkerTracking
                    ? _eyeFrameVisibilityTarget
                    : KiwiFacePartVisibilityMath.MoveVisibility(
                        _eyeFrameVisibility,
                        _eyeFrameVisibilityTarget,
                        dt,
                        eyeHideFadeSeconds,
                        eyeShowFadeSeconds
                    );


            visibility =
                _eyeFrameVisibility;


            debugVisibility =
                visibility;
        }
        else
        {
            AdvanceMouthFrameVisibility();


            visibility =
                _mouthFrameVisibility;
        }


        if (
            float.IsNaN(_lastAppliedFrameVisibility) ||
            Mathf.Abs(
                _lastAppliedFrameVisibility - visibility
            ) > 0.0001f
        )
        {
            _runtimeMaterial.SetFloat(
                VisibilityId,
                visibility
            );


            _lastAppliedFrameVisibility =
                visibility;
        }
    }


    // =========================================================
    // ★Mouth Height Lock
    // =========================================================

    private void UpdateMouthHeightLock(
        Rect uvRect,
        bool mirrorX,
        bool flipY,
        long timestamp)
    {
        if (!lockMouthHeight)
        {
            _heldMouthOffsetY =
                0f;


            SetSampleOffset(
                0f,
                0f
            );


            return;
        }


        if (
            Mathf.Abs(
                uvRect.height
            )
            <
            0.000001f
        )
        {
            return;
        }


        Vector2 anchor =
            GetMouthAnchor(
                mirrorX,
                flipY
            );


        // =====================================================
        // 現在の口角中点が
        // Crop内の何%の高さに存在するか
        // =====================================================

        float currentLocalV =
            (
                anchor.y -
                uvRect.y
            )
            /
            uvRect.height;


        // =====================================================
        // Front calibration
        // =====================================================

        if (!_mouthHeightCalibrated)
        {
            if (
                Time.unscaledTime -
                _enableTime
                <
                mouthCalibrationDelay
            )
            {
                SetSampleOffset(
                    0f,
                    0f
                );


                return;
            }


            if (
                timestamp ==
                _lastMouthCalibrationTimestamp
            )
            {
                return;
            }


            // 異常値除外
            if (
                currentLocalV <
                -0.5f ||
                currentLocalV >
                1.5f
            )
            {
                return;
            }


            _mouthReferenceVSum +=
                currentLocalV;


            _mouthCalibrationCount++;


            _lastMouthCalibrationTimestamp =
                timestamp;


            if (
                _mouthCalibrationCount >=
                Mathf.Max(
                    1,
                    mouthCalibrationSamples
                )
            )
            {
                _mouthReferenceLocalV =
                    _mouthReferenceVSum /
                    _mouthCalibrationCount;


                _mouthHeightCalibrated =
                    true;


                debugMouthCalibrated =
                    true;


                debugMouthReferenceV =
                    _mouthReferenceLocalV;


                _heldMouthOffsetY =
                    0f;
            }


            SetSampleOffset(
                0f,
                0f
            );


            return;
        }


        // =====================================================
        // 正面時と同じLocal V位置へ
        // 口角中点を配置。
        //
        // このUV位置に本来あるべき口角：
        // uvRect.y + referenceV * height
        //
        // 実際：
        // anchor.y
        //
        // 差分だけSamplingを移動。
        // =====================================================

        float expectedAnchorY =
            uvRect.y
            +
            _mouthReferenceLocalV *
            uvRect.height;


        float targetOffsetY =
            anchor.y -
            expectedAnchorY;


        // =====================================================
        // 極端な補正を防止
        // =====================================================

        float maxCorrection =
            Mathf.Abs(
                uvRect.height
            )
            *
            maximumMouthHeightCorrection;


        targetOffsetY =
            Mathf.Clamp(
                targetOffsetY,
                -maxCorrection,
                maxCorrection
            );


        // =====================================================
        // Zero latency dead-zone
        //
        // Lerpなし。
        // =====================================================

        if (
            Mathf.Abs(
                targetOffsetY -
                _heldMouthOffsetY
            )
            >
            mouthHeightDeadZone
        )
        {
            _heldMouthOffsetY =
                targetOffsetY;
        }


        debugMouthOffsetY =
            _heldMouthOffsetY;


        SetSampleOffset(
            0f,
            _heldMouthOffsetY
        );
    }


    // =========================================================
    // Mouth Anchor
    //
    // 外唇全体の平均を使わない。
    //
    // 61 / 291 の口角中点。
    // =========================================================

    private Vector2 GetMouthAnchor(
        bool mirrorX,
        bool flipY)
    {
        Vector2 a =
            ApplyOrientation(
                _landmarks[
                    MouthCornerA
                ],
                mirrorX,
                flipY
            );


        Vector2 b =
            ApplyOrientation(
                _landmarks[
                    MouthCornerB
                ],
                mirrorX,
                flipY
            );


        return
            (
                a +
                b
            )
            *
            0.5f;
    }


    // =========================================================
    // Sample Offset
    // =========================================================

    private void SetSampleOffset(
        float x,
        float y)
    {
        if (_runtimeMaterial == null)
        {
            return;
        }


        _runtimeMaterial.SetVector(
            SampleOffsetId,
            new Vector4(
                x,
                y,
                0f,
                0f
            )
        );
    }


    // =========================================================
    // Eye Closing
    // =========================================================

    private void ProcessEyeClosing(
        int selectedEye,
        bool hasExpression,
        FaceExpressionData expression,
        Vector2[] contour,
        int count,
        Vector2 center,
        ref float finalFeather,
        ref float visibility,
        ref bool hardClosed)
    {
        float openness =
            CalculateEyeOpenness(
                contour,
                count
            );


        debugEyeOpenness =
            openness;


        float blinkScore =
            0f;


        if (
            useBlendshapeBlink &&
            hasExpression
        )
        {
            if (selectedEye == 0)
            {
                blinkScore =
                    expression.eyeBlinkLeft;


                debugSelectedEye =
                    "Left 362/263";
            }
            else
            {
                blinkScore =
                    expression.eyeBlinkRight;


                debugSelectedEye =
                    "Right 33/133";
            }
        }


        blinkScore =
            Mathf.Clamp01(
                blinkScore
            );


        debugBlinkScore =
            blinkScore;


        // =====================================================
        // Blendshape close
        // =====================================================

        float blendClose =
            0f;


        if (
            useBlendshapeBlink &&
            hasExpression
        )
        {
            blendClose =
                Mathf.InverseLerp(
                    blinkShapeStart,
                    blinkHideThreshold,
                    blinkScore
                );


            blendClose =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    blendClose
                );
        }


        // =====================================================
        // Geometry close
        // =====================================================

        float geometryClose =
            0f;


        if (useGeometryCloseFallback)
        {
            geometryClose =
                1f
                -
                Mathf.InverseLerp(
                    geometryCloseFull,
                    geometryCloseStart,
                    openness
                );


            geometryClose =
                Mathf.Clamp01(
                    geometryClose
                );


            geometryClose =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    geometryClose
                );
        }


        // =====================================================
        // Confidence fusion
        //
        // Blendshape reacts quickly, while geometry provides an independent
        // plausibility check. A disagreement may still shape the eyelid, but
        // it must never make the complete eye disappear by itself.
        // =====================================================

        bool hasBlendSignal =
            useBlendshapeBlink &&
            hasExpression;


        bool hasGeometrySignal =
            useGeometryCloseFallback;


        float closeAmount =
            stabilizeEyeVisibility
                ? KiwiFacePartVisibilityMath.FuseEyeCloseAmount(
                    blendClose,
                    geometryClose,
                    hasBlendSignal,
                    hasGeometrySignal
                )
                : Mathf.Max(
                    blendClose,
                    geometryClose
                );


        closeAmount =
            Mathf.Pow(
                closeAmount,
                closeCurve
            );


        closeAmount =
            Mathf.Clamp01(
                closeAmount
            );


        debugCloseAmount =
            closeAmount;


        // =====================================================
        // Margin
        // =====================================================

        float dynamicMargin =
            Mathf.Lerp(
                eyeContourMargin,
                closedEyeContourMargin,
                closeAmount
            );


        ApplyContourMargin(
            contour,
            count,
            center,
            dynamicMargin
        );


        // =====================================================
        // Collapse
        // =====================================================

        float verticalScale =
            Mathf.Lerp(
                1f,
                closedEyeThickness,
                closeAmount
            );


        ApplyVerticalCompression(
            contour,
            count,
            center,
            verticalScale
        );


        // =====================================================
        // Feather
        // =====================================================

        finalFeather =
            Mathf.Lerp(
                feather,
                closedEyeFeather,
                closeAmount
            );


        // =====================================================
        // Visibility
        // =====================================================

        float blendVisibility =
            1f;


        if (
            useBlendshapeBlink &&
            hasExpression
        )
        {
            float hide =
                Mathf.InverseLerp(
                    blinkFadeStart,
                    blinkHideThreshold,
                    blinkScore
                );


            hide =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    hide
                );


            blendVisibility =
                1f -
                hide;
        }


        float geometryVisibility =
            1f;


        if (useGeometryCloseFallback)
        {
            geometryVisibility =
                Mathf.InverseLerp(
                    geometryCloseFull,
                    geometryCloseStart,
                    openness
                );


            geometryVisibility =
                Mathf.Clamp01(
                    geometryVisibility
                );


            geometryVisibility =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    geometryVisibility
                );
        }


        bool blinkClosed =
            hasBlendSignal &&
            blinkScore >=
            blinkHideThreshold;


        bool geometryClosed =
            hasGeometrySignal &&
            openness <=
            geometryCloseFull;


        if (stabilizeEyeVisibility)
        {
            UpdateEyeVisibilityState(
                hasBlendSignal,
                hasGeometrySignal,
                blinkClosed,
                geometryClosed,
                blinkScore,
                openness
            );


            hardClosed =
                _eyeClosureConfirmed;


            // Opacity is state-driven rather than sample-driven. Contour
            // compression above still follows the current sample immediately,
            // so a real blink stays responsive without alpha chatter.
            visibility =
                _eyeClosureConfirmed
                    ? Mathf.Clamp(
                        closedEyeVisibilityFloor,
                        0.10f,
                        1f
                    )
                    : 1f;
        }
        else
        {
            visibility =
                Mathf.Min(
                    blendVisibility,
                    geometryVisibility
                );


            hardClosed =
                blinkClosed ||
                geometryClosed;


            if (
                hardHideWhenFullyClosed &&
                hardClosed
            )
            {
                visibility =
                    0f;
            }
        }


        debugVisibility =
            visibility;
    }


    private void UpdateEyeVisibilityState(
        bool hasBlendSignal,
        bool hasGeometrySignal,
        bool blinkClosed,
        bool geometryClosed,
        float blinkScore,
        float openness)
    {
        bool coherentClosed =
            KiwiFacePartVisibilityMath.HasCoherentEyeClosure(
                hasBlendSignal,
                hasGeometrySignal,
                blinkClosed,
                geometryClosed
            );


        bool clearlyOpen =
            (!hasBlendSignal || blinkScore < blinkFadeStart) &&
            (!hasGeometrySignal || openness > geometryCloseStart);


        if (!_eyeClosureConfirmed)
        {
            _eyeOpenEvidenceSamples =
                0;


            _eyeCloseEvidenceSamples =
                KiwiFacePartVisibilityMath.AdvanceEvidenceCounter(
                    _eyeCloseEvidenceSamples,
                    coherentClosed,
                    eyeCloseConfirmationSamples
                );


            if (
                _eyeCloseEvidenceSamples >=
                Mathf.Max(1, eyeCloseConfirmationSamples)
            )
            {
                _eyeClosureConfirmed =
                    true;


                _eyeOpenEvidenceSamples =
                    0;
            }


            return;
        }


        _eyeCloseEvidenceSamples =
            0;


        _eyeOpenEvidenceSamples =
            KiwiFacePartVisibilityMath.AdvanceEvidenceCounter(
                _eyeOpenEvidenceSamples,
                clearlyOpen,
                eyeOpenConfirmationSamples
            );


        if (
            _eyeOpenEvidenceSamples >=
            Mathf.Max(1, eyeOpenConfirmationSamples)
        )
        {
            _eyeClosureConfirmed =
                false;


            _eyeOpenEvidenceSamples =
                0;
        }
    }


    // =========================================================
    // Resolve Eye
    // =========================================================

    private void ResolveEye(
        Rect uvRect,
        int landmarkCount,
        out int[] bestIndices,
        out bool bestMirrorX,
        out bool bestFlipY,
        out int selectedEye)
    {
        if (
            lockEyeAssignment &&
            _eyeAssignmentLocked &&
            _currentEyeSet >= 0
        )
        {
            selectedEye =
                _currentEyeSet;


            bestIndices =
                selectedEye == 0
                ?
                EyeAIndices
                :
                EyeBIndices;


            bestMirrorX =
                _currentMirrorX;


            bestFlipY =
                _currentFlipY;


            return;
        }


        Vector2 cropCenter =
            RectCenter(
                uvRect
            );


        float bestDistance =
            float.MaxValue;


        int bestEye =
            0;


        bool chosenMirror =
            false;


        bool chosenFlip =
            false;


        for (
            int eye = 0;
            eye < 2;
            eye++
        )
        {
            int[] indices =
                eye == 0
                ?
                EyeAIndices
                :
                EyeBIndices;


            if (
                !AreIndicesValid(
                    indices,
                    landmarkCount
                )
            )
            {
                continue;
            }


            Vector2 rawCenter =
                CalculateLandmarkCenter(
                    indices
                );


            for (
                int mx = 0;
                mx < 2;
                mx++
            )
            {
                for (
                    int fy = 0;
                    fy < 2;
                    fy++
                )
                {
                    bool mirror =
                        mx == 1;


                    bool flip =
                        fy == 1;


                    Vector2 candidate =
                        ApplyOrientation(
                            rawCenter,
                            mirror,
                            flip
                        );


                    float distance =
                        Vector2.SqrMagnitude(
                            candidate -
                            cropCenter
                        );


                    if (
                        eye ==
                        _currentEyeSet &&
                        mirror ==
                        _currentMirrorX &&
                        flip ==
                        _currentFlipY
                    )
                    {
                        distance /=
                            Mathf.Max(
                                1f,
                                eyeSwitchHysteresis
                            );
                    }


                    if (
                        distance <
                        bestDistance
                    )
                    {
                        bestDistance =
                            distance;


                        bestEye =
                            eye;


                        chosenMirror =
                            mirror;


                        chosenFlip =
                            flip;
                    }
                }
            }
        }


        bool changed =
            bestEye !=
            _currentEyeSet ||
            chosenMirror !=
            _currentMirrorX ||
            chosenFlip !=
            _currentFlipY;


        if (changed)
        {
            _hasStableContour =
                false;
        }


        _currentEyeSet =
            bestEye;


        _currentMirrorX =
            chosenMirror;


        _currentFlipY =
            chosenFlip;


        if (lockEyeAssignment)
        {
            _eyeAssignmentLocked =
                true;
        }


        selectedEye =
            bestEye;


        bestIndices =
            bestEye == 0
            ?
            EyeAIndices
            :
            EyeBIndices;


        bestMirrorX =
            chosenMirror;


        bestFlipY =
            chosenFlip;


        if (
            changed &&
            logMatching
        )
        {
            Debug.Log(
                "[FacePartShapeMask] "
                +
                gameObject.name
                +
                " Eye="
                +
                bestEye
                +
                " MirrorX="
                +
                chosenMirror
                +
                " FlipY="
                +
                chosenFlip,
                this
            );
        }
    }


    // =========================================================
    // Mouth orientation
    // =========================================================

    private void ResolveMouthOrientation(
        Rect uvRect,
        int landmarkCount,
        out bool mirrorX,
        out bool flipY)
    {
        if (_mouthOrientationResolved)
        {
            mirrorX =
                _mouthMirrorX;


            flipY =
                _mouthFlipY;


            return;
        }


        ResolveBestOrientation(
            MouthIndices,
            uvRect,
            landmarkCount,
            out _mouthMirrorX,
            out _mouthFlipY
        );


        _mouthOrientationResolved =
            true;


        mirrorX =
            _mouthMirrorX;


        flipY =
            _mouthFlipY;
    }


    // =========================================================
    // Mouth camera-edge visibility
    // =========================================================

    private bool IsMouthBlinkProtectionActive(
        int landmarkCount,
        bool hasExpression,
        FaceExpressionData expression)
    {
        if (!protectMouthDuringBlink)
        {
            debugMouthProtectedByBlink =
                false;


            return false;
        }


        float eyeAOpen =
            -1f;


        float eyeBOpen =
            -1f;


        if (!hasExpression)
        {
            eyeAOpen =
                CalculateRawEyeAspect(
                    362,
                    263,
                    386,
                    374,
                    landmarkCount
                );


            eyeBOpen =
                CalculateRawEyeAspect(
                    33,
                    133,
                    159,
                    145,
                    landmarkCount
                );
        }


        bool active =
            KiwiFacePartVisibilityMath.IsMouthBlinkProtectionActive(
                protectMouthDuringBlink,
                hasExpression,
                expression.eyeBlinkLeft,
                expression.eyeBlinkRight,
                mouthBlinkProtectionThreshold,
                eyeAOpen,
                eyeBOpen,
                mouthBlinkGeometryThreshold
            );


        debugMouthProtectedByBlink =
            active;


        return active;
    }


    private float CalculateRawEyeAspect(
        int cornerA,
        int cornerB,
        int upper,
        int lower,
        int landmarkCount)
    {
        if (
            cornerA < 0 ||
            cornerB < 0 ||
            upper < 0 ||
            lower < 0 ||
            cornerA >= landmarkCount ||
            cornerB >= landmarkCount ||
            upper >= landmarkCount ||
            lower >= landmarkCount ||
            cornerA >= _landmarks.Length ||
            cornerB >= _landmarks.Length ||
            upper >= _landmarks.Length ||
            lower >= _landmarks.Length
        )
        {
            return -1f;
        }


        return KiwiFacePartVisibilityMath.CalculateEyeAspect(
            _landmarks[cornerA],
            _landmarks[cornerB],
            _landmarks[upper],
            _landmarks[lower]
        );
    }

    private void UpdateMouthFrameVisibilityTarget(
        int landmarkCount,
        bool mirrorX,
        bool flipY,
        bool protectedByBlink)
    {
        if (!hideMouthOutsideTexture)
        {
            _mouthFrameVisibilityTarget =
                1f;


            debugMouthEdgeClearance =
                1f;


            debugMouthHiddenByFrame =
                false;


            _mouthEdgeViolationSamples =
                0;


            _mouthEdgeRecoverySamples =
                0;


            _mouthEdgeViolationStartTime =
                -1f;


            _holdMouthVisual =
                false;


            CacheSafeMouthUvRect();


            return;
        }


        float minX =
            float.MaxValue;


        float minY =
            float.MaxValue;


        float maxX =
            float.MinValue;


        float maxY =
            float.MinValue;


        for (int i = 0; i < MouthIndices.Length; i++)
        {
            int index =
                MouthIndices[i];


            if (
                index < 0 ||
                index >= landmarkCount ||
                index >= _landmarks.Length
            )
            {
                debugMouthEdgeClearance =
                    -1f;


                if (protectedByBlink)
                {
                    ProtectMouthVisibilityDuringBlink(
                        false
                    );


                    return;
                }


                ConfirmMouthEdgeViolation(
                    Time.unscaledTime
                );


                return;
            }


            Vector2 point =
                ApplyOrientation(
                    _landmarks[index],
                    mirrorX,
                    flipY
                );


            minX = Mathf.Min(minX, point.x);
            minY = Mathf.Min(minY, point.y);
            maxX = Mathf.Max(maxX, point.x);
            maxY = Mathf.Max(maxY, point.y);
        }


        float clearance =
            KiwiFacePartVisibilityMath.CalculateTextureEdgeClearance(
                minX,
                minY,
                maxX,
                maxY
            );


        if (protectedByBlink)
        {
            ProtectMouthVisibilityDuringBlink(
                clearance >=
                Mathf.Max(
                    0f,
                    mouthHideEdgeMargin
                )
            );


            debugMouthEdgeClearance =
                clearance;


            return;
        }


        bool safelyInside =
            clearance >=
            (
                _holdMouthVisual ||
                _mouthFrameVisibilityTarget < 0.5f
                    ? Mathf.Max(
                        mouthHideEdgeMargin,
                        mouthShowEdgeMargin
                    )
                    : Mathf.Max(
                        0f,
                        mouthHideEdgeMargin
                    )
            );


        if (safelyInside)
        {
            _mouthEdgeViolationSamples =
                0;


            _mouthEdgeViolationStartTime =
                -1f;


            if (
                _holdMouthVisual ||
                _mouthFrameVisibilityTarget < 0.5f
            )
            {
                _mouthEdgeRecoverySamples =
                    KiwiFacePartVisibilityMath.AdvanceEvidenceCounter(
                        _mouthEdgeRecoverySamples,
                        true,
                        mouthEdgeShowConfirmationSamples
                    );


                if (
                    _mouthEdgeRecoverySamples >=
                    Mathf.Max(
                        1,
                        mouthEdgeShowConfirmationSamples
                    )
                )
                {
                    _holdMouthVisual =
                        false;


                    _mouthFrameVisibilityTarget =
                        1f;


                    _mouthEdgeRecoverySamples =
                        0;


                    CacheSafeMouthUvRect();
                }
            }
            else
            {
                _mouthEdgeRecoverySamples =
                    0;


                _mouthFrameVisibilityTarget =
                    1f;


                CacheSafeMouthUvRect();
            }
        }
        else
        {
            _mouthEdgeRecoverySamples =
                0;


            ConfirmMouthEdgeViolation(
                Time.unscaledTime
            );
        }


        debugMouthEdgeClearance =
            clearance;


        debugMouthHiddenByFrame =
            _mouthFrameVisibilityTarget < 0.5f;
    }


    private void ProtectMouthVisibilityDuringBlink(
        bool currentCropIsComplete)
    {
        // A blink must never inherit an in-progress mouth fade. Restore both
        // current and target visibility in the same Landmarker result.
        _mouthFrameVisibility =
            1f;


        _mouthFrameVisibilityTarget =
            1f;


        _mouthEdgeViolationSamples =
            0;


        _mouthEdgeRecoverySamples =
            0;


        _mouthEdgeViolationStartTime =
            -1f;


        if (currentCropIsComplete)
        {
            _holdMouthVisual =
                false;


            CacheSafeMouthUvRect();
        }
        else
        {
            _holdMouthVisual =
                _hasSafeMouthUvRect;
        }


        debugMouthHiddenByFrame =
            false;
    }


    private void CacheSafeMouthUvRect()
    {
        if (_image == null)
        {
            return;
        }


        _safeMouthUvRect =
            _image.uvRect;


        _hasSafeMouthUvRect =
            true;
    }


    private void ConfirmMouthEdgeViolation(
        float sampleTime)
    {
        int requiredSamples =
            Mathf.Max(
                1,
                mouthEdgeHideConfirmationSamples
            );


        if (_mouthEdgeViolationSamples <= 0)
        {
            _mouthEdgeViolationStartTime =
                sampleTime;
        }


        _mouthEdgeViolationSamples =
            Mathf.Min(
                _mouthEdgeViolationSamples + 1,
                requiredSamples
            );


        // Hold the last complete visual from the first suspect result. This is
        // a zero-order hold only for invalid edge samples; valid tracking is
        // never smoothed or delayed.
        _holdMouthVisual =
            _hasSafeMouthUvRect;


        float violationDuration =
            _mouthEdgeViolationStartTime >= 0f
                ? Mathf.Max(
                    0f,
                    sampleTime -
                    _mouthEdgeViolationStartTime
                )
                : 0f;


        if (
            KiwiFacePartVisibilityMath.ShouldConfirmVisibilityLoss(
                _mouthEdgeViolationSamples,
                requiredSamples,
                violationDuration,
                mouthEdgeHideGraceSeconds
            )
        )
        {
            _mouthFrameVisibilityTarget =
                0f;
        }


        debugMouthHiddenByFrame =
            _mouthFrameVisibilityTarget < 0.5f;
    }


    private void AdvanceMouthFrameVisibility()
    {
        if (!hideMouthOutsideTexture)
        {
            _mouthFrameVisibilityTarget =
                1f;
        }


        float dt =
            Mathf.Min(
                Time.unscaledDeltaTime,
                0.05f
            );


        _mouthFrameVisibility =
            KiwiFacePartVisibilityMath.MoveVisibility(
                _mouthFrameVisibility,
                _mouthFrameVisibilityTarget,
                dt,
                mouthHideFadeSeconds,
                mouthShowFadeSeconds
            );


        debugMouthFrameVisibility =
            _mouthFrameVisibility;


        debugVisibility =
            _mouthFrameVisibility;
    }


    // =========================================================
    // Generic orientation
    // =========================================================

    private void ResolveBestOrientation(
        int[] indices,
        Rect uvRect,
        int landmarkCount,
        out bool bestMirrorX,
        out bool bestFlipY)
    {
        bestMirrorX =
            false;


        bestFlipY =
            false;


        if (
            !AreIndicesValid(
                indices,
                landmarkCount
            )
        )
        {
            return;
        }


        Vector2 center =
            CalculateLandmarkCenter(
                indices
            );


        Vector2 cropCenter =
            RectCenter(
                uvRect
            );


        float bestDistance =
            float.MaxValue;


        for (
            int mx = 0;
            mx < 2;
            mx++
        )
        {
            for (
                int fy = 0;
                fy < 2;
                fy++
            )
            {
                bool mirror =
                    mx == 1;


                bool flip =
                    fy == 1;


                Vector2 candidate =
                    ApplyOrientation(
                        center,
                        mirror,
                        flip
                    );


                float distance =
                    Vector2.SqrMagnitude(
                        candidate -
                        cropCenter
                    );


                if (
                    distance <
                    bestDistance
                )
                {
                    bestDistance =
                        distance;


                    bestMirrorX =
                        mirror;


                    bestFlipY =
                        flip;
                }
            }
        }
    }


    // =========================================================
    // Eye openness
    // =========================================================

    private float CalculateEyeOpenness(
        Vector2[] contour,
        int count)
    {
        float minX =
            float.MaxValue;


        float maxX =
            float.MinValue;


        float minY =
            float.MaxValue;


        float maxY =
            float.MinValue;


        for (
            int i = 0;
            i < count;
            i++
        )
        {
            Vector2 p =
                contour[i];


            minX =
                Mathf.Min(
                    minX,
                    p.x
                );


            maxX =
                Mathf.Max(
                    maxX,
                    p.x
                );


            minY =
                Mathf.Min(
                    minY,
                    p.y
                );


            maxY =
                Mathf.Max(
                    maxY,
                    p.y
                );
        }


        float width =
            Mathf.Max(
                0.000001f,
                maxX -
                minX
            );


        float height =
            Mathf.Max(
                0f,
                maxY -
                minY
            );


        return
            height /
            width;
    }


    // =========================================================
    // Vertical compression
    // =========================================================

    private void ApplyVerticalCompression(
        Vector2[] contour,
        int count,
        Vector2 center,
        float scale)
    {
        scale =
            Mathf.Clamp(
                scale,
                0.001f,
                1f
            );


        for (
            int i = 0;
            i < count;
            i++
        )
        {
            Vector2 p =
                contour[i];


            p.y =
                center.y
                +
                (
                    p.y -
                    center.y
                )
                *
                scale;


            contour[i] =
                p;
        }
    }


    // =========================================================
    // Margin
    // =========================================================

    private void ApplyContourMargin(
        Vector2[] contour,
        int count,
        Vector2 center,
        float margin)
    {
        float scale =
            Mathf.Clamp(
                1f +
                margin,
                0.50f,
                1.50f
            );


        for (
            int i = 0;
            i < count;
            i++
        )
        {
            contour[i] =
                center
                +
                (
                    contour[i] -
                    center
                )
                *
                scale;
        }
    }


    // =========================================================
    // Contour center
    // =========================================================

    private Vector2 CalculateContourCenter(
        Vector2[] contour,
        int count)
    {
        Vector2 center =
            Vector2.zero;


        for (
            int i = 0;
            i < count;
            i++
        )
        {
            center +=
                contour[i];
        }


        return
            center /
            Mathf.Max(
                1,
                count
            );
    }


    // =========================================================
    // Landmark center
    // =========================================================

    private Vector2 CalculateLandmarkCenter(
        int[] indices)
    {
        Vector2 center =
            Vector2.zero;


        for (
            int i = 0;
            i < indices.Length;
            i++
        )
        {
            center +=
                _landmarks[
                    indices[i]
                ];
        }


        return
            center /
            Mathf.Max(
                1,
                indices.Length
            );
    }


    // =========================================================
    // Validation
    // =========================================================

    private bool AreIndicesValid(
        int[] indices,
        int landmarkCount)
    {
        if (indices == null)
        {
            return false;
        }


        for (
            int i = 0;
            i < indices.Length;
            i++
        )
        {
            int index =
                indices[i];


            if (
                index < 0 ||
                index >=
                landmarkCount
            )
            {
                return false;
            }
        }


        return true;
    }


    // =========================================================
    // Orientation
    // =========================================================

    private Vector2 ApplyOrientation(
        Vector2 point,
        bool mirrorX,
        bool flipY)
    {
        if (mirrorX)
        {
            point.x =
                1f -
                point.x;
        }


        if (flipY)
        {
            point.y =
                1f -
                point.y;
        }


        return point;
    }


    // =========================================================
    // Rect center
    // =========================================================

    private Vector2 RectCenter(
        Rect rect)
    {
        return new Vector2(
            rect.x +
            rect.width *
            0.5f,

            rect.y +
            rect.height *
            0.5f
        );
    }


    // =========================================================
    // Chaikin
    // =========================================================

    private int BuildChaikinContour(
        Vector2[] source,
        int sourceCount,
        Vector2[] destination)
    {
        if (
            source == null ||
            destination == null ||
            sourceCount < 3
        )
        {
            return 0;
        }


        int outputLimit =
            Mathf.Min(
                sourceCount * 2,
                destination.Length
            );


        int write =
            0;


        for (
            int i = 0;
            i < sourceCount;
            i++
        )
        {
            int next =
                i + 1;


            if (
                next >=
                sourceCount
            )
            {
                next =
                    0;
            }


            Vector2 a =
                source[i];


            Vector2 b =
                source[next];


            Vector2 q =
                a * 0.75f +
                b * 0.25f;


            Vector2 r =
                a * 0.25f +
                b * 0.75f;


            if (
                write <
                outputLimit
            )
            {
                destination[
                    write++
                ] =
                    q;
            }


            if (
                write <
                outputLimit
            )
            {
                destination[
                    write++
                ] =
                    r;
            }
        }


        return write;
    }


    // =========================================================
    // Resolve part
    // =========================================================

    private FacePartType ResolveFacePart()
    {
        if (
            facePart !=
            FacePartType.Auto
        )
        {
            return facePart;
        }


        string objectName =
            gameObject.name
            .ToLowerInvariant();


        if (
            objectName.Contains(
                "mouth"
            )
        )
        {
            return FacePartType.Mouth;
        }


        return FacePartType.Eye;
    }


    // =========================================================
    // Material
    // =========================================================

    private void CreateMaterial()
    {
        if (_image == null)
        {
            _image =
                GetComponent<
                    SurfaceFittedRawImage
                >();
        }


        if (_runtimeMaterial != null)
        {
            if (
                _image.material !=
                _runtimeMaterial
            )
            {
                _image.material =
                    _runtimeMaterial;


                _image.SetMaterialDirty();
            }


            return;
        }


        Shader shader =
            Shader.Find(
                "UI/FacePartSoftMask"
            );


        if (shader == null)
        {
            Debug.LogError(
                "[FacePartShapeMask] "
                +
                "UI/FacePartSoftMask が見つかりません。",
                this
            );


            return;
        }


        if (
            baseMaterial != null &&
            baseMaterial.shader ==
            shader
        )
        {
            _runtimeMaterial =
                new Material(
                    baseMaterial
                );
        }
        else
        {
            _runtimeMaterial =
                new Material(
                    shader
                );
        }


        _runtimeMaterial.name =
            gameObject.name +
            " Optimized Mask";


        _runtimeMaterial.hideFlags =
            HideFlags.HideAndDontSave;


        _runtimeMaterial.SetFloat(
            VisibilityId,
            1f
        );


        _runtimeMaterial.SetFloat(
            PoseVisibilityId,
            1f
        );


        _lastAppliedFrameVisibility =
            1f;


        _lastAppliedPoseVisibility =
            1f;


        _lastAppliedVisibleScale =
            new Vector2(float.NaN, float.NaN);


        SetSampleOffset(
            0f,
            0f
        );


        _image.canvasRenderer.SetAlpha(
            1f
        );


        _image.material =
            _runtimeMaterial;


        _image.SetMaterialDirty();
    }


    // =========================================================
    // Recalibrate mouth
    // =========================================================

    [ContextMenu("Recalibrate Mouth Height")]
    public void RecalibrateMouthHeight()
    {
        _enableTime =
            Time.unscaledTime;


        _mouthHeightCalibrated =
            false;


        _mouthCalibrationCount =
            0;


        _lastMouthCalibrationTimestamp =
            -1;


        _mouthReferenceVSum =
            0f;


        _mouthReferenceLocalV =
            0.5f;


        _heldMouthOffsetY =
            0f;


        debugMouthCalibrated =
            false;


        debugMouthReferenceV =
            0.5f;


        debugMouthOffsetY =
            0f;


        SetSampleOffset(
            0f,
            0f
        );
    }


    // =========================================================
    // Reset
    // =========================================================

    [ContextMenu("Reset Contour And Eye Assignment")]
    public void ResetContour()
    {
        _hasStableContour =
            false;


        _lastTimestamp =
            -1;


        _currentEyeSet =
            -1;


        _currentMirrorX =
            false;


        _currentFlipY =
            false;


        _eyeAssignmentLocked =
            false;


        _mouthOrientationResolved =
            false;


        debugSelectedEye =
            "-";


        debugBlinkScore =
            0f;


        debugEyeOpenness =
            0f;


        debugCloseAmount =
            0f;


        debugVisibility =
            1f;


        _mouthFrameVisibility =
            1f;


        _mouthFrameVisibilityTarget =
            1f;


        _mouthEdgeViolationSamples =
            0;


        _mouthEdgeRecoverySamples =
            0;


        _mouthEdgeViolationStartTime =
            -1f;


        _holdMouthVisual =
            false;


        _hasSafeMouthUvRect =
            false;


        _eyeFrameVisibility =
            1f;


        _eyeFrameVisibilityTarget =
            1f;


        _eyeCloseEvidenceSamples =
            0;


        _eyeOpenEvidenceSamples =
            0;


        _eyeClosureConfirmed =
            false;


        _renderedPointCount =
            0;


        _targetPointCount =
            0;


        _hasRenderedContour =
            false;


        _contourUploadDirty =
            false;


        _maskPointsAreCropLocal =
            false;


        debugMouthEdgeClearance =
            1f;


        debugMouthHiddenByFrame =
            false;


        debugMouthFrameVisibility =
            1f;


        debugMouthProtectedByBlink =
            false;


        if (_runtimeMaterial != null)
        {
            _runtimeMaterial.SetFloat(
                VisibilityId,
                1f
            );


            _runtimeMaterial.SetFloat(
                PoseVisibilityId,
                1f
            );


            _lastAppliedFrameVisibility =
                1f;


            _lastAppliedPoseVisibility =
                1f;


            SetSampleOffset(
                0f,
                0f
            );
        }


        if (_image != null)
        {
            _image.canvasRenderer.SetAlpha(
                1f
            );
        }


        RecalibrateMouthHeight();
    }


    // =========================================================
    // Destroy
    // =========================================================

    private void OnDisable()
    {
        Application.onBeforeRender -=
            RefreshPoseVisibility;


        if (_image != null)
        {
            _image.canvasRenderer.SetAlpha(
                1f
            );
        }


        DestroyRuntimeMaterial();
    }


    private void OnDestroy()
    {
        DestroyRuntimeMaterial();
    }


    private void DestroyRuntimeMaterial()
    {
        if (_runtimeMaterial == null)
        {
            return;
        }


        if (
            _image != null &&
            _image.material ==
            _runtimeMaterial
        )
        {
            _image.material =
                null;


            _image.SetMaterialDirty();
        }


        if (Application.isPlaying)
        {
            Destroy(
                _runtimeMaterial
            );
        }
        else
        {
            DestroyImmediate(
                _runtimeMaterial
            );
        }


        _runtimeMaterial =
            null;


        _lastAppliedFrameVisibility =
            float.NaN;


        _lastAppliedPoseVisibility =
            float.NaN;
    }
}


public static class KiwiFacePartContourStabilityMath
{
    public static bool ShouldUpdateContour(
        Vector2[] stable,
        Vector2[] raw,
        int count,
        float translationDeadZone,
        float shapeDeadZone)
    {
        if (
            stable == null ||
            raw == null ||
            count <= 0 ||
            count > stable.Length ||
            count > raw.Length
        )
        {
            return true;
        }


        Vector2 stableCenter = Vector2.zero;
        Vector2 rawCenter = Vector2.zero;


        for (int i = 0; i < count; i++)
        {
            stableCenter += stable[i];
            rawCenter += raw[i];
        }


        float inverseCount = 1f / count;
        stableCenter *= inverseCount;
        rawCenter *= inverseCount;


        if (
            Vector2.Distance(stableCenter, rawCenter) >
            Mathf.Max(0f, translationDeadZone)
        )
        {
            return true;
        }


        Vector2 coherentTranslation = rawCenter - stableCenter;
        float residualSum = 0f;


        for (int i = 0; i < count; i++)
        {
            Vector2 residual =
                raw[i] - stable[i] - coherentTranslation;


            residualSum += residual.sqrMagnitude;
        }


        float rmsShapeChange = Mathf.Sqrt(
            residualSum * inverseCount
        );


        return
            rmsShapeChange >
            Mathf.Max(0f, shapeDeadZone);
    }
}


public static class KiwiFacePartMaskCoherenceMath
{
    public static Vector2 ToCropLocal(
        Vector2 point,
        Rect cropRect,
        float safetyMargin)
    {
        float safeWidth =
            Mathf.Max(0.000001f, Mathf.Abs(cropRect.width));

        float safeHeight =
            Mathf.Max(0.000001f, Mathf.Abs(cropRect.height));

        Vector2 local =
            new Vector2(
                (point.x - cropRect.xMin) / safeWidth,
                (point.y - cropRect.yMin) / safeHeight
            );

        float margin =
            Mathf.Clamp(safetyMargin, 0f, 0.49f);

        local.x =
            Mathf.Clamp(local.x, margin, 1f - margin);

        local.y =
            Mathf.Clamp(local.y, margin, 1f - margin);

        return local;
    }


    public static Vector2 FromCropLocal(
        Vector2 localPoint,
        Rect cropRect)
    {
        return new Vector2(
            cropRect.xMin + localPoint.x * cropRect.width,
            cropRect.yMin + localPoint.y * cropRect.height
        );
    }
}


public static class KiwiFacePartSurfaceSafetyMath
{
    public static Vector2 CalculateNormalizedSurface(
        Vector2 sampleUv,
        Rect sourceRect,
        Vector2 pivot,
        Vector2 offset,
        float uniformScale,
        Vector2 sampleScaleXY,
        float rotationRadians,
        float sourceAspect,
        Vector2 visualZoom)
    {
        float aspect = Mathf.Max(0.0001f, sourceAspect);
        float scaleX = Mathf.Max(0.01f, uniformScale * sampleScaleXY.x);
        float scaleY = Mathf.Max(0.01f, uniformScale * sampleScaleXY.y);
        Vector2 rotated = sampleUv - offset - pivot;
        rotated.x *= aspect;


        float sine = Mathf.Sin(rotationRadians);
        float cosine = Mathf.Cos(rotationRadians);
        Vector2 unrotated = new Vector2(
            cosine * rotated.x + sine * rotated.y,
            -sine * rotated.x + cosine * rotated.y
        );
        unrotated.x /= scaleX * aspect;
        unrotated.y /= scaleY;


        Vector2 inputUv = pivot + unrotated;
        float width = Mathf.Max(0.000001f, Mathf.Abs(sourceRect.width));
        float height = Mathf.Max(0.000001f, Mathf.Abs(sourceRect.height));
        return new Vector2(
            0.5f +
                (inputUv.x - sourceRect.center.x) /
                width * Mathf.Max(0.01f, visualZoom.x),
            0.5f +
                (inputUv.y - sourceRect.center.y) /
                height * Mathf.Max(0.01f, visualZoom.y)
        );
    }


    public static bool IsInsideSurface(
        Vector2 normalizedSurface,
        float safetyMargin)
    {
        float margin = Mathf.Clamp(safetyMargin, 0f, 0.49f);
        return
            !float.IsNaN(normalizedSurface.x) &&
            !float.IsInfinity(normalizedSurface.x) &&
            !float.IsNaN(normalizedSurface.y) &&
            !float.IsInfinity(normalizedSurface.y) &&
            normalizedSurface.x >= margin &&
            normalizedSurface.x <= 1f - margin &&
            normalizedSurface.y >= margin &&
            normalizedSurface.y <= 1f - margin;
    }
}


public static class KiwiFacePartVisibilityMath
{
    public static float CalculatePoseVisibility(
        float yawDegrees,
        float fullVisibilityYaw,
        float hiddenVisibilityYaw)
    {
        float full = Mathf.Max(0f, fullVisibilityYaw);
        float hidden = Mathf.Max(full + 0.1f, hiddenVisibilityYaw);
        float transition = Mathf.InverseLerp(
            full,
            hidden,
            Mathf.Abs(yawDegrees)
        );

        transition = transition * transition * (3f - 2f * transition);
        return 1f - transition;
    }


    public static bool IsMouthBlinkProtectionActive(
        bool enabled,
        bool hasExpression,
        float blinkLeft,
        float blinkRight,
        float blendshapeThreshold,
        float eyeAAspect,
        float eyeBAspect,
        float geometryThreshold)
    {
        if (!enabled)
        {
            return false;
        }


        if (hasExpression)
        {
            return
                Mathf.Max(
                    blinkLeft,
                    blinkRight
                ) >=
                Mathf.Clamp01(
                    blendshapeThreshold
                );
        }


        return
            eyeAAspect >= 0f &&
            eyeBAspect >= 0f &&
            Mathf.Min(
                eyeAAspect,
                eyeBAspect
            ) <=
            Mathf.Max(
                0f,
                geometryThreshold
            );
    }


    public static float CalculateEyeAspect(
        Vector2 cornerA,
        Vector2 cornerB,
        Vector2 upper,
        Vector2 lower)
    {
        float width =
            Vector2.Distance(
                cornerA,
                cornerB
            );


        if (width <= 0.000001f)
        {
            return -1f;
        }


        return
            Vector2.Distance(
                upper,
                lower
            ) /
            width;
    }


    public static float FuseEyeCloseAmount(
        float blendClose,
        float geometryClose,
        bool hasBlendSignal,
        bool hasGeometrySignal)
    {
        blendClose =
            Mathf.Clamp01(blendClose);


        geometryClose =
            Mathf.Clamp01(geometryClose);


        if (hasBlendSignal && hasGeometrySignal)
        {
            float agreement =
                Mathf.Min(
                    blendClose,
                    geometryClose
                );


            float primary =
                blendClose * 0.65f +
                geometryClose * 0.35f;


            // Agreement is weighted heavily so a yaw-compressed landmark eye
            // cannot instantly collapse an otherwise open BlendShape eye.
            return Mathf.Clamp01(
                primary * 0.45f +
                agreement * 0.55f
            );
        }


        if (hasBlendSignal)
        {
            return blendClose;
        }


        if (hasGeometrySignal)
        {
            return geometryClose;
        }


        return 0f;
    }


    public static bool HasCoherentEyeClosure(
        bool hasBlendSignal,
        bool hasGeometrySignal,
        bool blinkClosed,
        bool geometryClosed)
    {
        // Full opacity reduction requires two independent signals. If only one
        // source is available, contour compression still represents the blink
        // while the eye texture remains present.
        return
            hasBlendSignal &&
            hasGeometrySignal &&
            blinkClosed &&
            geometryClosed;
    }


    public static int AdvanceEvidenceCounter(
        int current,
        bool evidence,
        int required)
    {
        if (!evidence)
        {
            return 0;
        }


        int limit =
            Mathf.Max(
                1,
                required
            );


        return Mathf.Min(
            Mathf.Max(0, current) + 1,
            limit
        );
    }


    public static bool ShouldConfirmVisibilityLoss(
        int evidenceSamples,
        int requiredSamples,
        float evidenceSeconds,
        float graceSeconds)
    {
        return
            evidenceSamples >=
            Mathf.Max(1, requiredSamples) &&
            evidenceSeconds >=
            Mathf.Max(0f, graceSeconds);
    }


    public static float CalculateTextureEdgeClearance(
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        return Mathf.Min(
            Mathf.Min(minX, minY),
            Mathf.Min(1f - maxX, 1f - maxY)
        );
    }


    public static bool ResolveVisibleState(
        bool currentlyShown,
        float edgeClearance,
        float hideMargin,
        float showMargin)
    {
        float hideThreshold =
            Mathf.Max(0f, hideMargin);


        float showThreshold =
            Mathf.Max(
                hideThreshold,
                showMargin
            );


        return currentlyShown
            ? edgeClearance > hideThreshold
            : edgeClearance >= showThreshold;
    }


    public static float MoveVisibility(
        float current,
        float target,
        float deltaTime,
        float hideSeconds,
        float showSeconds)
    {
        float duration =
            target < current
                ? hideSeconds
                : showSeconds;


        if (duration <= 0.0001f)
        {
            return Mathf.Clamp01(target);
        }


        return Mathf.MoveTowards(
            Mathf.Clamp01(current),
            Mathf.Clamp01(target),
            Mathf.Max(0f, deltaTime) /
            duration
        );
    }
}
