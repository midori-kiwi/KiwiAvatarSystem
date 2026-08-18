using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(850)]
[DisallowMultipleComponent]
[RequireComponent(typeof(SurfaceFittedRawImage))]
public class FacePartShapeMask : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: raw contour points are used every Landmarker timestamp and mouth sample offset is not temporally locked.")]
    public bool strictLandmarkerTracking = true;


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

    public Material baseMaterial;


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
        0.00015f;


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
        true;


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


    private static readonly int SampleOffsetId =
        Shader.PropertyToID(
            "_SampleOffset"
        );


    // =========================================================
    // Enable
    // =========================================================

    private void OnEnable()
    {
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
                FindObjectOfType<
                    FaceLandmarkerRunner
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


        int landmarkCount =
            0;


        long timestamp =
            0;


        bool valid =
            runner.TryGetLatestLandmarks(
                ref _landmarks,
                out landmarkCount,
                out timestamp
            );


        if (!valid)
        {
            return;
        }


        if (
            timestamp ==
            _lastTimestamp
        )
        {
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
            ResolveFacePart();


        Rect uvRect =
            _image.uvRect;


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


            // ★口の高さ補正
            if (strictLandmarkerTracking)
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
        else
        {
            for (int i = 0; i < rawCount; i++)
            {
                float movement =
                    Vector2.Distance(
                        _stableContour[i],
                        _rawContour[i]
                    );

                if (movement > microJitterDeadZone)
                {
                    _stableContour[i] = _rawContour[i];
                }
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
                1f;


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
                1f;
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

        for (
            int i = 0;
            i < smoothCount;
            i++
        )
        {
            Vector2 p =
                _smoothContour[i];


            _shaderPoints[i] =
                new Vector4(
                    p.x,
                    p.y,
                    0f,
                    0f
                );
        }


        for (
            int i = smoothCount;
            i < MaxPoints;
            i++
        )
        {
            _shaderPoints[i] =
                Vector4.zero;
        }


        _runtimeMaterial.SetVectorArray(
            MaskPointsId,
            _shaderPoints
        );


        _runtimeMaterial.SetFloat(
            MaskPointCountId,
            smoothCount
        );


        _runtimeMaterial.SetFloat(
            VisibilityId,
            visibility
        );


        // =====================================================
        // Eye hard hide
        // =====================================================

        if (
            part ==
            FacePartType.Eye &&
            hardHideWhenFullyClosed
        )
        {
            _image.canvasRenderer.SetAlpha(
                hardClosed
                ?
                0f
                :
                1f
            );
        }
        else
        {
            _image.canvasRenderer.SetAlpha(
                1f
            );
        }


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
        // Strongest close signal
        // =====================================================

        float closeAmount =
            Mathf.Max(
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


        visibility =
            Mathf.Min(
                blendVisibility,
                geometryVisibility
            );


        bool blinkClosed =
            useBlendshapeBlink &&
            hasExpression &&
            blinkScore >=
            blinkHideThreshold;


        bool geometryClosed =
            useGeometryCloseFallback &&
            openness <=
            geometryCloseFull;


        hardClosed =
            blinkClosed ||
            geometryClosed;


        if (hardClosed)
        {
            visibility =
                0f;
        }


        debugVisibility =
            visibility;
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


        if (_runtimeMaterial != null)
        {
            _runtimeMaterial.SetFloat(
                VisibilityId,
                1f
            );


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
    }
}