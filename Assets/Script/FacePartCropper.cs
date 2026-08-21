using UnityEngine;
using UnityEngine.UI;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;


[DefaultExecutionOrder(700)]
public class FacePartCropper : MonoBehaviour
{
    [Header("Landmarker Direct Tracking")]
    [Tooltip("ON: each new Landmarker crop is applied immediately. OFF uses high-response render-rate interpolation to remove sample-and-hold flicker.")]
    public bool strictLandmarkerTracking = false;


    // =========================================================
    // MediaPipe
    // =========================================================

    [Header("MediaPipe")]

    public FaceLandmarkerRunner runner;
    public RawImage sourceImage;


    // =========================================================
    // Output
    // =========================================================

    [Header("Output")]

    public RawImage leftEyeImage;
    public RawImage rightEyeImage;
    public RawImage mouthImage;


    // =========================================================
    // Camera
    // =========================================================

    [Header("Camera")]

    public bool mirrorX = true;

    // 左右の目が逆ならON
    public bool swapEyes = true;


    // =========================================================
    // Render
    // =========================================================

    [Header("Render Interpolation")]

    public bool request120Fps = true;

    [Range(60, 240)]
    public int targetRenderFrameRate = 120;


    // =========================================================
    // Eye Crop
    // =========================================================

    [Header("Eye Fixed Anchor Crop")]

    [Range(1.0f, 3.0f)]
    public float eyeWidthScale = 1.45f;

    [Range(0.2f, 1.5f)]
    public float eyeHeightToWidth = 0.58f;

    [Range(0f, 0.1f)]
    public float eyePaddingX = 0.010f;

    [Range(0f, 0.1f)]
    public float eyePaddingY = 0.008f;

    [Tooltip("Keeps each eye centered when its crop extends beyond a camera edge. Out-of-range pixels are made transparent by the mask shader.")]
    public bool preserveEyeCenterAtTextureEdges = true;


    // =========================================================
    // Mouth Crop
    // =========================================================

    [Header("Mouth Fixed Anchor Crop")]

    [Range(1.0f, 3.0f)]
    public float mouthWidthScale = 1.35f;

    [Range(0.2f, 1.5f)]
    public float mouthHeightToWidth = 0.55f;

    [Range(0f, 0.1f)]
    public float mouthPaddingX = 0.012f;

    [Range(0f, 0.1f)]
    public float mouthPaddingY = 0.012f;

    [Header("Mouth Contour Safe Crop")]

    [Tooltip("Centers the crop on the complete outer lip contour and expands it when the mouth opens.")]
    public bool useMouthContourSafeCrop = true;

    [Tooltip("Extra horizontal clearance on each side, relative to mouth-corner width.")]
    [Range(0f, 0.8f)]
    public float mouthContourSafetyX = 0.10f;

    [Tooltip("Extra vertical clearance above and below, relative to aspect-corrected mouth-corner width.")]
    [Range(0f, 0.8f)]
    public float mouthContourSafetyY = 0.14f;

    [Tooltip("Keeps the mouth centered when its crop extends beyond a camera edge. Out-of-range pixels are made transparent by the mask shader.")]
    public bool preserveMouthCenterAtTextureEdges = true;

    [Header("Mouth Edge Diagnostics")]

    [SerializeField]
    private Vector4 debugMouthUvOverscan = Vector4.zero;


    // =========================================================
    // ★ MediaPipe Sample Stabilizer
    //
    // FaceLandmarkListに近づける上で一番重要
    // =========================================================

    [Header("Sample Stabilizer")]

    [Tooltip("ほぼ静止時のMediaPipeサンプル平滑化")]
    [Range(1f, 300f)]
    public float sampleIdleResponse = 120f;

    [Tooltip("顔が動いている時。大きいほど低遅延")]
    [Range(1f, 400f)]
    public float sampleMovingResponse = 240f;

    [Tooltip("この速度でMoving Response最大")]
    [Range(0.05f, 3f)]
    public float sampleMotionFullSpeed = 0.20f;


    // =========================================================
    // ★ Soft Jitter Suppression
    //
    // DeadZoneと違い、急にカクッと動き出しにくい
    // =========================================================

    [Header("Micro Jitter Suppression")]

    [Tooltip("この距離以下はかなり強く抑える")]
    [Range(0f, 0.003f)]
    public float microJitterStart = 0.00020f;

    [Tooltip("この距離以上なら生の移動をほぼ通す")]
    [Range(0.0002f, 0.01f)]
    public float microJitterFull = 0.0012f;

    [Tooltip("非常に小さい動きも完全停止させず少しだけ通す")]
    [Range(0f, 0.5f)]
    public float microJitterMinimumGain = 0.12f;


    // =========================================================
    // Sample Size Stabilizer
    //
    // 切り抜きサイズは位置より強く平滑化
    // =========================================================

    [Header("Sample Size Stabilizer")]

    [Range(1f, 250f)]
    public float eyeSampleSizeResponse = 80f;

    [Range(1f, 250f)]
    public float mouthSampleSizeResponse = 90f;


    // =========================================================
    // Unity Render Interpolation
    //
    // Sample Filter後の位置を描画fpsで滑らかにつなぐ
    // =========================================================

    [Header("Position Interpolation")]

    [Tooltip("目の描画追従速度")]
    [Range(1f, 250f)]
    public float eyeRenderResponse = 180f;

    [Tooltip("口の描画追従速度")]
    [Range(1f, 250f)]
    public float mouthRenderResponse = 200f;


    [Header("Size Interpolation")]

    [Range(1f, 250f)]
    public float eyeRenderSizeResponse = 70f;

    [Range(1f, 250f)]
    public float mouthRenderSizeResponse = 80f;


    // =========================================================
    // Velocity
    // =========================================================

    [Header("Velocity Estimation")]

    [Tooltip("速度推定自体のプルプルを抑える")]
    [Range(1f, 250f)]
    public float velocityResponse = 120f;

    [Range(0.1f, 10f)]
    public float maxCenterVelocity = 2.5f;


    // =========================================================
    // Prediction
    //
    // FaceLandmarkList寄せではOFF推奨
    // =========================================================

    [Header("Prediction")]

    [Tooltip(
        "FaceLandmarkListの安定感に近づけるならOFF推奨。" +
        "遅延が気になる時だけON。"
    )]
    public bool enablePrediction = true;

    [Tooltip("Use the matched camera-frame submission time to compensate LandMarker inference age. This keeps the live camera texture aligned with its eye/mouth crops during translation.")]
    public bool compensateMatchedFrameAge = true;

    [Tooltip("During intentional motion, apply the compensated crop center directly. Rest motion still uses render-rate interpolation.")]
    public bool directPositionDuringMotion = true;

    [Range(0.02f, 1f)]
    public float directPositionSpeed = 0.12f;

    [Range(0f, 0.02f)]
    public float predictionLeadSeconds = 0.001f;

    [Range(0.005f, 0.12f)]
    public float maxExtrapolationSeconds = 0.090f;

    [Range(0f, 0.05f)]
    public float maxPredictionDistance = 0.004f;

    [SerializeField]
    private float debugMatchedFrameAgeMs = 0f;


    [Header("Coherent Vertical Face Motion")]

    [Tooltip("During coherent up/down head translation, gives both eyes and the mouth one shared Y delta and velocity. Local blink, speech and pose deformation still bypass this grouping.")]
    public bool stabilizeCoherentVerticalMotion = true;

    [Range(0.005f, 0.50f)]
    public float coherentVerticalMotionMinSpeed = 0.025f;

    [Range(0.0005f, 0.02f)]
    public float coherentVerticalDeltaTolerance = 0.0030f;

    [Tooltip("Uses one shared Y prediction and render phase for both eyes and the mouth. Local blink and speech crop changes remain independent before the final vertical phase lock.")]
    public bool phaseLockVerticalPrediction = true;

    [Range(30f, 250f)]
    public float coherentVerticalRenderResponse = 200f;


    // =========================================================
    // Rest Stabilization
    // =========================================================

    [Header("Rest Stabilization")]

    [Tooltip("これ以下なら静止付近")]
    [Range(0f, 0.1f)]
    public float restSpeed = 0.020f;

    [Tooltip("静止時の微小な動きをさらに抑える")]
    [Range(0f, 0.005f)]
    public float restJitterThreshold = 0.00075f;

    [Tooltip("Holds sub-pixel crop width/height noise while the part center is at rest. Real mouth and blink deformation releases from the accumulated raw size change.")]
    [Range(0f, 0.01f)]
    public float restSizeJitterThreshold = 0.00120f;


    // =========================================================
    // Tracking Lost
    // =========================================================

    [Header("Tracking Lost")]

    [Range(0.05f, 2f)]
    public float lostTrackingResetTime = 0.20f;

    [Tooltip("ON hides all parts after a sustained tracking loss. OFF freezes the last valid crops, preventing brief MediaPipe dropouts from flashing the face off.")]
    public bool hidePartsWhenLost = false;


    [Header("Isolated Part Outlier Guard")]

    [Tooltip("Rejects a mouth crop that jumps independently from both eyes for only one Landmarker result.")]
    public bool rejectIsolatedMouthOutliers = true;

    [Range(0.01f, 0.20f)]
    public float mouthOutlierAbsoluteTolerance = 0.045f;

    [Range(0.5f, 4f)]
    public float mouthOutlierEyeSpanMultiplier = 1.25f;


    [SerializeField]
    private int debugRejectedMouthSamples = 0;


    // =========================================================
    // MediaPipe Landmark IDs
    // =========================================================

    // 左目の左右端
    private const int LEFT_EYE_A = 362;
    private const int LEFT_EYE_B = 263;

    // 右目の左右端
    private const int RIGHT_EYE_A = 33;
    private const int RIGHT_EYE_B = 133;

    // 口の左右端
    private const int MOUTH_LEFT = 61;
    private const int MOUTH_RIGHT = 291;

    private static readonly int[] MOUTH_OUTER_CONTOUR =
    {
        61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291,
        375, 321, 405, 314, 17, 84, 181, 91, 146
    };


    // =========================================================
    // Landmark Buffer
    // =========================================================

    private Vector2[] _landmarkBuffer;

    private long _lastProcessedTimestamp = -1;


    // =========================================================
    // Tracking State
    // =========================================================

    private float _lastTrackingTime = -100f;

    private bool _trackingLost = true;

    private bool _statesResetForLoss = true;

    private float _matchedFrameAgeSeconds = -1f;


    // =========================================================
    // Part State
    // =========================================================

    private class PartState
    {
        public bool initialized;

        // 最新の安定化済みMediaPipeサンプル
        public UnityEngine.Rect sampleRect;

        // 現在画面に表示しているRect
        public UnityEngine.Rect displayRect;

        // 前回の生Anchor中心
        public Vector2 lastRawCenter;

        // 安定化後の移動速度
        public Vector2 centerVelocity;

        public long sampleTimestamp = -1;

        public float sampleArrivalTime;
    }


    private readonly PartState _leftEyeState =
        new PartState();

    private readonly PartState _rightEyeState =
        new PartState();

    private readonly PartState _mouthState =
        new PartState();


    private bool _hasCoherentRawHistory;
    private Vector2 _lastCoherentLeftCenter;
    private Vector2 _lastCoherentRightCenter;
    private Vector2 _lastCoherentMouthCenter;
    private Vector2 _coherentOutputLeftCenter;
    private Vector2 _coherentOutputRightCenter;
    private Vector2 _coherentOutputMouthCenter;
    private long _lastCoherentTimestamp = -1;
    private bool _coherentVerticalApplied;
    private bool _hasSharedVerticalVelocity;
    private float _sharedVerticalVelocity;


    public bool TryGetSampleRect(
        RawImage image,
        out UnityEngine.Rect sampleRect)
    {
        sampleRect = default;

        PartState state =
            image == leftEyeImage
                ? _leftEyeState
                : image == rightEyeImage
                    ? _rightEyeState
                    : image == mouthImage
                        ? _mouthState
                        : null;

        if (
            state == null ||
            !state.initialized ||
            state.sampleRect.width <= 0.000001f ||
            state.sampleRect.height <= 0.000001f
        )
        {
            return false;
        }

        sampleRect = state.sampleRect;
        return true;
    }


    // =========================================================
    // Start
    // =========================================================

    // KIWI_PRESENTATION_FPS_OWNER_V3_7
    // Global presentation cadence is owned by
    // KiwiTrackingQuality10Controller. FacePartCropper follows render time.
    private void Start()
    {
    }


    // =========================================================
    // Update
    // =========================================================

    private void LateUpdate()
    {
        if (runner == null)
            return;

        if (sourceImage == null)
            return;

        if (sourceImage.texture == null)
            return;

        if (
            leftEyeImage == null ||
            rightEyeImage == null ||
            mouthImage == null
        )
        {
            return;
        }


        // =====================================================
        // 高画質Webカメラ映像
        // =====================================================

        leftEyeImage.texture =
            sourceImage.texture;

        rightEyeImage.texture =
            sourceImage.texture;

        mouthImage.texture =
            sourceImage.texture;


        // =====================================================
        // 最新MediaPipeランドマーク
        // =====================================================

        bool hasNewLandmarks =
            runner.TryGetLatestLandmarksIfChanged(
                ref _landmarkBuffer,
                _lastProcessedTimestamp,
                out int landmarkCount,
                out long timestamp,
                out bool hasFace
            );


        // KIWI_V4_8_SEMANTIC_FRESHNESS_GATE
        // A just-arrived ML result can still describe an old
        // camera frame. Hold the previous trusted crop instead
        // of replacing it with source-age-expired geometry.
        bool semanticSampleFresh =
            !hasNewLandmarks ||
            KiwiCommercialFacePartPolicy.IsSemanticSampleAdoptable(
                runner,
                timestamp);

        if (
            hasFace &&
            hasNewLandmarks &&
            semanticSampleFresh &&
            _landmarkBuffer != null &&
            landmarkCount > 0
        )
        {
            if (_trackingLost)
            {
                if (_statesResetForLoss)
                {
                    ResetStates();
                }

                SetPartsVisible(true);
            }


            ProcessSample(
                landmarkCount,
                timestamp
            );


            _lastProcessedTimestamp =
                timestamp;


            _lastTrackingTime =
                Time.unscaledTime;


            _trackingLost =
                false;


            _statesResetForLoss =
                false;
        }
        else if (!hasFace)
        {
            HandleTrackingLost();
        }


        UpdateMatchedFrameAge();


        // =====================================================
        // Unity描画fpsで毎フレーム補間
        // =====================================================

        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f
            );


        RenderPart(
            leftEyeImage,
            _leftEyeState,
            true,
            dt
        );


        RenderPart(
            rightEyeImage,
            _rightEyeState,
            true,
            dt
        );


        RenderPart(
            mouthImage,
            _mouthState,
            false,
            dt
        );
    }


    // =========================================================
    // Process MediaPipe Sample
    // =========================================================

    private void ProcessSample(
        int landmarkCount,
        long timestamp)
    {
        float sourceAspect =
            sourceImage.texture.width /
            (float)Mathf.Max(
                1,
                sourceImage.texture.height
            );


        UnityEngine.Rect leftRect;
        UnityEngine.Rect rightRect;
        UnityEngine.Rect mouthRect;


        bool leftOK =
            BuildEyeRect(
                LEFT_EYE_A,
                LEFT_EYE_B,
                landmarkCount,
                sourceAspect,
                out leftRect
            );


        bool rightOK =
            BuildEyeRect(
                RIGHT_EYE_A,
                RIGHT_EYE_B,
                landmarkCount,
                sourceAspect,
                out rightRect
            );


        bool mouthOK =
            BuildMouthRect(
                landmarkCount,
                sourceAspect,
                out mouthRect
            );


        if (
            mouthOK &&
            leftOK &&
            rightOK &&
            rejectIsolatedMouthOutliers &&
            _leftEyeState.initialized &&
            _rightEyeState.initialized &&
            _mouthState.initialized &&
            !KiwiFacePartContinuityMath.IsMouthSamplePlausible(
                _leftEyeState.sampleRect.center,
                _rightEyeState.sampleRect.center,
                _mouthState.sampleRect.center,
                leftRect.center,
                rightRect.center,
                mouthRect.center,
                mouthOutlierAbsoluteTolerance,
                mouthOutlierEyeSpanMultiplier
            )
        )
        {
            // Preserve the previous mouth crop only for the isolated outlier.
            // Both eyes still update now, so normal head motion stays immediate.
            mouthOK = false;
            debugRejectedMouthSamples++;
        }


        // KIWI_V4_9_ISOLATED_EYE_CROP_GUARD
        // The dense v4.8 recording contained a one-eye source
        // crop jump while the companion eye and mouth remained
        // coherent. Reject only that catastrophic isolated eye;
        // shared translation/yaw/roll remains untouched.
        if (
            leftOK &&
            rightOK &&
            mouthOK &&
            _leftEyeState.initialized &&
            _rightEyeState.initialized &&
            _mouthState.initialized
        )
        {
            Rect previousLandmarkLeft =
                swapEyes
                    ? _rightEyeState.sampleRect
                    : _leftEyeState.sampleRect;

            Rect previousLandmarkRight =
                swapEyes
                    ? _leftEyeState.sampleRect
                    : _rightEyeState.sampleRect;

            KiwiCommercialFacePartPolicy.
                ResolveIsolatedEyeCropOutliers(
                    previousLandmarkLeft,
                    previousLandmarkRight,
                    _mouthState.sampleRect,
                    leftRect,
                    rightRect,
                    mouthRect,
                    ref leftOK,
                    ref rightOK);
        }

        _coherentVerticalApplied = false;

        if (leftOK && rightOK && mouthOK)
        {
            StabilizeCoherentVerticalMotion(
                ref leftRect,
                ref rightRect,
                ref mouthRect,
                timestamp
            );
        }


        // KIWI_V4_9_PART_TRANSACTION_REPORT
        // Report decisions in output-image space. ShapeMask
        // runs later and must hold exactly the part whose crop
        // was held for this semantic timestamp.
        bool outputLeftEyeAccepted =
            swapEyes
                ? rightOK
                : leftOK;

        bool outputRightEyeAccepted =
            swapEyes
                ? leftOK
                : rightOK;

        KiwiCommercialFacePartPolicy.ReportPartSampleDecision(
            timestamp,
            outputLeftEyeAccepted,
            outputRightEyeAccepted,
            mouthOK);

        if (swapEyes)
        {
            if (leftOK)
            {
                UpdateSample(
                    rightEyeImage,
                    _rightEyeState,
                    leftRect,
                    timestamp,
                    true
                );
            }


            if (rightOK)
            {
                UpdateSample(
                    leftEyeImage,
                    _leftEyeState,
                    rightRect,
                    timestamp,
                    true
                );
            }
        }
        else
        {
            if (leftOK)
            {
                UpdateSample(
                    leftEyeImage,
                    _leftEyeState,
                    leftRect,
                    timestamp,
                    true
                );
            }


            if (rightOK)
            {
                UpdateSample(
                    rightEyeImage,
                    _rightEyeState,
                    rightRect,
                    timestamp,
                    true
                );
            }
        }


        if (mouthOK)
        {
            UpdateSample(
                mouthImage,
                _mouthState,
                mouthRect,
                timestamp,
                false
            );
        }


        if (
            _coherentVerticalApplied &&
            !phaseLockVerticalPrediction
        )
        {
            SynchronizeCoherentVerticalVelocities();
        }


        RefreshSharedVerticalVelocity();
    }


    private void StabilizeCoherentVerticalMotion(
        ref UnityEngine.Rect leftRect,
        ref UnityEngine.Rect rightRect,
        ref UnityEngine.Rect mouthRect,
        long timestamp)
    {
        Vector2 rawLeftCenter = leftRect.center;
        Vector2 rawRightCenter = rightRect.center;
        Vector2 rawMouthCenter = mouthRect.center;

        if (!stabilizeCoherentVerticalMotion)
        {
            _hasCoherentRawHistory = false;
            _lastCoherentTimestamp = -1;
            return;
        }

        if (!_hasCoherentRawHistory || timestamp <= _lastCoherentTimestamp)
        {
            _hasCoherentRawHistory = true;
            _lastCoherentLeftCenter = rawLeftCenter;
            _lastCoherentRightCenter = rawRightCenter;
            _lastCoherentMouthCenter = rawMouthCenter;
            _coherentOutputLeftCenter = rawLeftCenter;
            _coherentOutputRightCenter = rawRightCenter;
            _coherentOutputMouthCenter = rawMouthCenter;
            _lastCoherentTimestamp = timestamp;
            return;
        }

        float sampleDt = Mathf.Clamp(
            (timestamp - _lastCoherentTimestamp) / 1000f,
            1f / 240f,
            0.10f
        );

        float rawLeftDelta =
            rawLeftCenter.y - _lastCoherentLeftCenter.y;
        float rawRightDelta =
            rawRightCenter.y - _lastCoherentRightCenter.y;
        float rawMouthDelta =
            rawMouthCenter.y - _lastCoherentMouthCenter.y;

        bool coherent;
        float resolvedLeftDelta;
        float resolvedRightDelta;
        float resolvedMouthDelta;

        if (phaseLockVerticalPrediction)
        {
            coherent = KiwiFacePartCoherentMotionMath.TryResolvePhaseLockedVerticalDeltas(
                rawLeftDelta,
                rawRightDelta,
                rawMouthDelta,
                sampleDt,
                coherentVerticalMotionMinSpeed,
                coherentVerticalDeltaTolerance,
                out resolvedLeftDelta,
                out resolvedRightDelta,
                out resolvedMouthDelta,
                out _
            );
        }
        else
        {
            coherent = KiwiFacePartCoherentMotionMath.TryResolveSharedVerticalDelta(
                rawLeftDelta,
                rawRightDelta,
                rawMouthDelta,
                sampleDt,
                coherentVerticalMotionMinSpeed,
                coherentVerticalDeltaTolerance,
                out float sharedDelta,
                out _
            );
            resolvedLeftDelta = sharedDelta;
            resolvedRightDelta = sharedDelta;
            resolvedMouthDelta = sharedDelta;
        }

        if (coherent)
        {
            _coherentOutputLeftCenter.y += resolvedLeftDelta;
            _coherentOutputRightCenter.y += resolvedRightDelta;
            _coherentOutputMouthCenter.y += resolvedMouthDelta;

            Vector2 center = leftRect.center;
            center.y = _coherentOutputLeftCenter.y;
            leftRect.center = center;

            center = rightRect.center;
            center.y = _coherentOutputRightCenter.y;
            rightRect.center = center;

            center = mouthRect.center;
            center.y = _coherentOutputMouthCenter.y;
            mouthRect.center = center;

            _coherentVerticalApplied = true;
        }
        else
        {
            // A blink, speech deformation, or pose change is local motion.
            // Rebase immediately so expression centers are never rigidly held.
            _coherentOutputLeftCenter = rawLeftCenter;
            _coherentOutputRightCenter = rawRightCenter;
            _coherentOutputMouthCenter = rawMouthCenter;
        }

        _lastCoherentLeftCenter = rawLeftCenter;
        _lastCoherentRightCenter = rawRightCenter;
        _lastCoherentMouthCenter = rawMouthCenter;
        _lastCoherentTimestamp = timestamp;
    }


    private void SynchronizeCoherentVerticalVelocities()
    {
        float sharedVelocity = KiwiFacePartCoherentMotionMath.Median3(
            _leftEyeState.centerVelocity.y,
            _rightEyeState.centerVelocity.y,
            _mouthState.centerVelocity.y
        );

        Vector2 velocity = _leftEyeState.centerVelocity;
        velocity.y = sharedVelocity;
        _leftEyeState.centerVelocity = velocity;

        velocity = _rightEyeState.centerVelocity;
        velocity.y = sharedVelocity;
        _rightEyeState.centerVelocity = velocity;

        velocity = _mouthState.centerVelocity;
        velocity.y = sharedVelocity;
        _mouthState.centerVelocity = velocity;
    }


    private void RefreshSharedVerticalVelocity()
    {
        if (
            !_leftEyeState.initialized ||
            !_rightEyeState.initialized ||
            !_mouthState.initialized
        )
        {
            _hasSharedVerticalVelocity = false;
            _sharedVerticalVelocity = 0f;
            return;
        }

        _sharedVerticalVelocity = Mathf.Clamp(
            KiwiFacePartCoherentMotionMath.Median3(
                _leftEyeState.centerVelocity.y,
                _rightEyeState.centerVelocity.y,
                _mouthState.centerVelocity.y
            ),
            -Mathf.Max(0.01f, maxCenterVelocity),
            Mathf.Max(0.01f, maxCenterVelocity)
        );
        _hasSharedVerticalVelocity = true;
    }


    // =========================================================
    // Eye Rect
    //
    // min/maxは使わない。
    // 固定した目尻・目頭のみを使用。
    // =========================================================

    private bool BuildEyeRect(
        int indexA,
        int indexB,
        int landmarkCount,
        float sourceAspect,
        out UnityEngine.Rect rect)
    {
        rect =
            default;


        if (
            !TryGetLandmark(
                indexA,
                landmarkCount,
                out Vector2 a
            )
        )
        {
            return false;
        }


        if (
            !TryGetLandmark(
                indexB,
                landmarkCount,
                out Vector2 b
            )
        )
        {
            return false;
        }


        Vector2 center =
            (a + b) *
            0.5f;


        float baseWidth =
            GetAnchorDistanceAsWidth(
                a,
                b,
                sourceAspect
            );


        float width =
            baseWidth *
            eyeWidthScale +
            eyePaddingX * 2f;


        float height =
            baseWidth *
            sourceAspect *
            eyeHeightToWidth +
            eyePaddingY * 2f;


        rect =
            MakeUvRect(
                center,
                width,
                height,
                preserveEyeCenterAtTextureEdges
            );


        return true;
    }


    // =========================================================
    // Mouth Rect
    //
    // 口の上下点をサイズ計算に使わない。
    //
    // これが口を開閉した時の
    // サイズプルプル低減にかなり効く。
    // =========================================================

    private bool BuildMouthRect(
        int landmarkCount,
        float sourceAspect,
        out UnityEngine.Rect rect)
    {
        rect =
            default;


        if (
            !TryGetLandmark(
                MOUTH_LEFT,
                landmarkCount,
                out Vector2 left
            )
        )
        {
            return false;
        }


        if (
            !TryGetLandmark(
                MOUTH_RIGHT,
                landmarkCount,
                out Vector2 right
            )
        )
        {
            return false;
        }


        Vector2 center =
            (left + right) *
            0.5f;


        float baseWidth =
            GetAnchorDistanceAsWidth(
                left,
                right,
                sourceAspect
            );


        float width =
            baseWidth *
            mouthWidthScale +
            mouthPaddingX * 2f;


        float height =
            baseWidth *
            sourceAspect *
            mouthHeightToWidth +
            mouthPaddingY * 2f;


        if (
            useMouthContourSafeCrop &&
            TryGetMouthContourBounds(
                landmarkCount,
                out Vector2 contourMin,
                out Vector2 contourMax
            )
        )
        {
            UnityEngine.Rect safeRect =
                KiwiMouthCropMath.CalculateSafeRect(
                    left,
                    right,
                    contourMin,
                    contourMax,
                    sourceAspect,
                    mouthWidthScale,
                    mouthHeightToWidth,
                    mouthPaddingX,
                    mouthPaddingY,
                    mouthContourSafetyX,
                    mouthContourSafetyY
                );


            center = safeRect.center;
            width = safeRect.width;
            height = safeRect.height;
        }


        rect =
            MakeUvRect(
                center,
                width,
                height,
                preserveMouthCenterAtTextureEdges
            );


        debugMouthUvOverscan = new Vector4(
            Mathf.Max(0f, -rect.xMin),
            Mathf.Max(0f, -rect.yMin),
            Mathf.Max(0f, rect.xMax - 1f),
            Mathf.Max(0f, rect.yMax - 1f)
        );


        return true;
    }


    private bool TryGetMouthContourBounds(
        int landmarkCount,
        out Vector2 minimum,
        out Vector2 maximum)
    {
        minimum = new Vector2(float.MaxValue, float.MaxValue);
        maximum = new Vector2(float.MinValue, float.MinValue);


        for (int i = 0; i < MOUTH_OUTER_CONTOUR.Length; i++)
        {
            if (
                !TryGetLandmark(
                    MOUTH_OUTER_CONTOUR[i],
                    landmarkCount,
                    out Vector2 point
                )
            )
            {
                return false;
            }


            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }


        return
            maximum.x > minimum.x &&
            maximum.y > minimum.y;
    }


    // =========================================================
    // Update Sample
    //
    // ★ここが今回の中心
    // =========================================================

    private void UpdateSample(
        RawImage image,
        PartState state,
        UnityEngine.Rect rawRect,
        long timestamp,
        bool isEye)
    {
        if (!state.initialized)
        {
            state.initialized =
                true;


            state.sampleRect =
                rawRect;


            state.displayRect =
                rawRect;


            state.lastRawCenter =
                rawRect.center;


            state.centerVelocity =
                Vector2.zero;


            state.sampleTimestamp =
                timestamp;


            state.sampleArrivalTime =
                Time.unscaledTime;


            image.uvRect =
                rawRect;


            return;
        }


        if (strictLandmarkerTracking)
        {
            state.sampleRect = rawRect;
            state.displayRect = rawRect;
            state.lastRawCenter = rawRect.center;
            state.centerVelocity = Vector2.zero;
            state.sampleTimestamp = timestamp;
            state.sampleArrivalTime = Time.unscaledTime;
            image.uvRect = rawRect;
            return;
        }


        // =====================================================
        // MediaPipe dt
        // =====================================================

        float sampleDt =
            1f / 30f;


        if (
            state.sampleTimestamp >= 0 &&
            timestamp >
            state.sampleTimestamp
        )
        {
            sampleDt =
                (
                    timestamp -
                    state.sampleTimestamp
                )
                /
                1000f;
        }


        sampleDt =
            Mathf.Clamp(
                sampleDt,
                1f / 240f,
                0.10f
            );


        // =====================================================
        // 生の移動速度
        // =====================================================

        Vector2 rawCenter =
            rawRect.center;


        Vector2 rawDelta =
            rawCenter -
            state.lastRawCenter;


        float rawSpeed =
            rawDelta.magnitude /
            Mathf.Max(
                sampleDt,
                0.0001f
            );


        state.lastRawCenter =
            rawCenter;


        // =====================================================
        // ★ Soft Micro Jitter Suppression
        //
        // 小さなブレ
        // → 強く抑える
        //
        // 本当に動いた
        // → ほぼそのまま通す
        // =====================================================

        Vector2 previousCenter =
            state.sampleRect.center;


        Vector2 targetCenter =
            ApplySoftJitterSuppression(
                previousCenter,
                rawCenter
            );


        // =====================================================
        // Adaptive Sample Response
        //
        // 静止
        // → 強い平滑化
        //
        // 移動
        // → 高速追従
        // =====================================================

        float motionFactor =
            Mathf.InverseLerp(
                0.01f,
                sampleMotionFullSpeed,
                rawSpeed
            );


        float sampleResponse =
            Mathf.Lerp(
                sampleIdleResponse,
                sampleMovingResponse,
                motionFactor
            );


        float sampleT =
            1f -
            Mathf.Exp(
                -sampleResponse *
                sampleDt
            );


        Vector2 filteredCenter =
            Vector2.Lerp(
                previousCenter,
                targetCenter,
                sampleT
            );


        // =====================================================
        // Size
        //
        // サイズは位置よりかなり強く平滑化
        // =====================================================

        Vector2 currentSize =
            state.sampleRect.size;


        Vector2 rawSize =
            rawRect.size;


        if (
            KiwiFacePartRectStabilityMath.ShouldHoldSize(
                currentSize,
                rawSize,
                rawSpeed,
                restSpeed,
                restSizeJitterThreshold
            )
        )
        {
            rawSize = currentSize;
        }


        float sizeResponse =
            isEye
                ? eyeSampleSizeResponse
                : mouthSampleSizeResponse;


        float sizeT =
            1f -
            Mathf.Exp(
                -sizeResponse *
                sampleDt
            );


        Vector2 filteredSize =
            Vector2.Lerp(
                currentSize,
                rawSize,
                sizeT
            );


        // =====================================================
        // Filter後の速度
        // =====================================================

        Vector2 instantaneousVelocity =
            (
                filteredCenter -
                previousCenter
            )
            /
            Mathf.Max(
                sampleDt,
                0.0001f
            );


        if (
            instantaneousVelocity.magnitude >
            maxCenterVelocity
        )
        {
            instantaneousVelocity =
                instantaneousVelocity.normalized *
                maxCenterVelocity;
        }


        float velocityT =
            1f -
            Mathf.Exp(
                -velocityResponse *
                sampleDt
            );


        state.centerVelocity =
            Vector2.Lerp(
                state.centerVelocity,
                instantaneousVelocity,
                velocityT
            );


        // =====================================================
        // Save
        // =====================================================

        state.sampleRect =
            MakeRectFromCenter(
                filteredCenter,
                filteredSize
            );


        state.sampleTimestamp =
            timestamp;


        state.sampleArrivalTime =
            Time.unscaledTime;
    }


    // =========================================================
    // Soft Jitter Suppression
    // =========================================================

    private Vector2 ApplySoftJitterSuppression(
        Vector2 previous,
        Vector2 target)
    {
        Vector2 delta =
            target -
            previous;


        float distance =
            delta.magnitude;


        if (distance <= 0f)
        {
            return previous;
        }


        float factor =
            Mathf.InverseLerp(
                microJitterStart,
                microJitterFull,
                distance
            );


        factor =
            Mathf.SmoothStep(
                0f,
                1f,
                factor
            );


        float gain =
            Mathf.Lerp(
                microJitterMinimumGain,
                1f,
                factor
            );


        return previous +
            delta *
            gain;
    }


    // =========================================================
    // RenderPart
    //
    // MediaPipe結果が来ていないフレームも
    // Unity側で滑らかにつなぐ
    // =========================================================

    private void RenderPart(
        RawImage image,
        PartState state,
        bool isEye,
        float dt)
    {
        if (
            image == null ||
            !state.initialized
        )
        {
            return;
        }


        if (strictLandmarkerTracking)
        {
            state.displayRect = state.sampleRect;
            image.uvRect = state.sampleRect;
            return;
        }


        Vector2 targetCenter =
            state.sampleRect.center;


        float speed =
            state.centerVelocity.magnitude;


        bool phaseLockVertical =
            phaseLockVerticalPrediction &&
            _hasSharedVerticalVelocity;


        float sharedVerticalSpeed =
            phaseLockVertical
                ? Mathf.Abs(_sharedVerticalVelocity)
                : 0f;


        // =====================================================
        // Prediction
        //
        // 初期値はOFF。
        // FaceLandmarkList寄せならOFFが安定。
        // =====================================================

        if (
            enablePrediction &&
            (
                speed > restSpeed ||
                sharedVerticalSpeed > restSpeed
            )
        )
        {
            float elapsed = Mathf.Max(
                0f,
                Time.unscaledTime - state.sampleArrivalTime
            );

            float predictionTime =
                KiwiFacePartPredictionMath.CalculatePredictionTime(
                    compensateMatchedFrameAge,
                    _matchedFrameAgeSeconds,
                    elapsed,
                    predictionLeadSeconds,
                    maxExtrapolationSeconds
                );


            targetCenter =
                phaseLockVertical
                    ? KiwiFacePartPredictionMath.PredictCenterPhaseLocked(
                        targetCenter,
                        state.centerVelocity.x,
                        _sharedVerticalVelocity,
                        predictionTime,
                        maxPredictionDistance
                    )
                    : KiwiFacePartPredictionMath.PredictCenter(
                        targetCenter,
                        state.centerVelocity,
                        predictionTime,
                        maxPredictionDistance
                    );
        }


        // =====================================================
        // Rest Stabilization
        // =====================================================

        Vector2 displayCenter =
            state.displayRect.center;


        if (Mathf.Max(speed, sharedVerticalSpeed) < restSpeed)
        {
            float distance =
                Vector2.Distance(
                    displayCenter,
                    targetCenter
                );


            if (
                distance <
                restJitterThreshold
            )
            {
                targetCenter =
                    displayCenter;
            }
        }


        // =====================================================
        // Position interpolation
        // =====================================================

        float renderResponse =
            isEye
                ? eyeRenderResponse
                : mouthRenderResponse;


        float horizontalPositionT =
            directPositionDuringMotion &&
            enablePrediction &&
            speed >= Mathf.Max(0.02f, directPositionSpeed)
                ? 1f
                : 1f -
                    Mathf.Exp(
                        -renderResponse *
                        dt
                    );


        float verticalSpeed =
            phaseLockVertical
                ? sharedVerticalSpeed
                : speed;


        float verticalResponse =
            phaseLockVertical
                ? coherentVerticalRenderResponse
                : renderResponse;


        float verticalPositionT =
            directPositionDuringMotion &&
            enablePrediction &&
            verticalSpeed >= Mathf.Max(0.02f, directPositionSpeed)
                ? 1f
                : 1f - Mathf.Exp(-verticalResponse * dt);


        // X remains part-local. Y uses the same prediction velocity, direct-motion
        // decision and response for both eyes and the mouth, preventing relative
        // vertical phase shifts while the head is translating.
        Vector2 newCenter =
            new Vector2(
                Mathf.Lerp(displayCenter.x, targetCenter.x, horizontalPositionT),
                Mathf.Lerp(displayCenter.y, targetCenter.y, verticalPositionT)
            );


        // =====================================================
        // Size interpolation
        // =====================================================

        Vector2 currentSize =
            state.displayRect.size;


        Vector2 targetSize =
            state.sampleRect.size;


        float sizeResponse =
            isEye
                ? eyeRenderSizeResponse
                : mouthRenderSizeResponse;


        float sizeT =
            1f -
            Mathf.Exp(
                -sizeResponse *
                dt
            );


        Vector2 newSize =
            Vector2.Lerp(
                currentSize,
                targetSize,
                sizeT
            );


        UnityEngine.Rect output =
            MakeRectFromCenter(
                newCenter,
                newSize
            );


        output =
            ClampUvRect(
                output,
                isEye
                    ? !preserveEyeCenterAtTextureEdges
                    : !preserveMouthCenterAtTextureEdges
            );


        state.displayRect =
            output;


        image.uvRect =
            output;
    }


    private void UpdateMatchedFrameAge()
    {
        _matchedFrameAgeSeconds = -1f;
        debugMatchedFrameAgeMs = 0f;

        if (
            !compensateMatchedFrameAge ||
            runner == null ||
            _lastProcessedTimestamp < 0 ||
            !runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData precision
            ) ||
            !precision.hasMatchedSubmissionTiming ||
            precision.timestamp != _lastProcessedTimestamp ||
            precision.submissionHostTicks <= 0L
        )
        {
            return;
        }

        long nowTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        if (nowTicks <= precision.submissionHostTicks)
        {
            return;
        }

        double age =
            KiwiPrecisionTrackingMath.HostTicksToSeconds(
                nowTicks - precision.submissionHostTicks
            );

        if (
            double.IsNaN(age) ||
            double.IsInfinity(age)
        )
        {
            return;
        }

        // KIWI_V4_8_MATCHED_AGE_DIAGNOSTIC_RANGE
        // Keep the real source age long enough for the
        // prediction lifetime gate to observe a semantic
        // stall. Extrapolation itself remains separately
        // capped by maxExtrapolationSeconds.
        _matchedFrameAgeSeconds = Mathf.Clamp(
            (float)age,
            0f,
            KiwiCommercialFacePartPolicy.
                MaximumSemanticSourceAgeSeconds * 2f
        );

        debugMatchedFrameAgeMs =
            _matchedFrameAgeSeconds * 1000f;
    }


    // =========================================================
    // Landmark
    // =========================================================

    private bool TryGetLandmark(
        int index,
        int landmarkCount,
        out Vector2 point)
    {
        point =
            Vector2.zero;


        if (_landmarkBuffer == null)
            return false;


        if (
            index < 0 ||
            index >= landmarkCount ||
            index >= _landmarkBuffer.Length
        )
        {
            return false;
        }


        point =
            _landmarkBuffer[index];


        return true;
    }


    // =========================================================
    // Anchor Distance
    // =========================================================

    private float GetAnchorDistanceAsWidth(
        Vector2 a,
        Vector2 b,
        float sourceAspect)
    {
        float dx =
            b.x -
            a.x;


        float dy =
            (
                b.y -
                a.y
            )
            /
            Mathf.Max(
                0.01f,
                sourceAspect
            );


        return Mathf.Sqrt(
            dx * dx +
            dy * dy
        );
    }


    // =========================================================
    // Landmark -> UV Rect
    // =========================================================

    private UnityEngine.Rect MakeUvRect(
        Vector2 landmarkCenter,
        float width,
        float height,
        bool preserveCenterAtEdges = false)
    {
        width =
            Mathf.Clamp(
                width,
                0.001f,
                1f
            );


        height =
            Mathf.Clamp(
                height,
                0.001f,
                1f
            );


        float centerX =
            mirrorX
                ? 1f - landmarkCenter.x
                : landmarkCenter.x;


        float centerY =
            1f -
            landmarkCenter.y;


        UnityEngine.Rect rect =
            KiwiMouthCropMath.CalculateCenteredUvRect(
                new Vector2(
                    centerX,
                    centerY
                ),
                width,
                height
            );


        return ClampUvRect(
            rect,
            !preserveCenterAtEdges
        );
    }


    // =========================================================
    // Rect from Center
    // =========================================================

    private UnityEngine.Rect MakeRectFromCenter(
        Vector2 center,
        Vector2 size)
    {
        return new UnityEngine.Rect(
            center.x -
            size.x * 0.5f,

            center.y -
            size.y * 0.5f,

            size.x,

            size.y
        );
    }


    // =========================================================
    // Clamp
    // =========================================================

    private UnityEngine.Rect ClampUvRect(
        UnityEngine.Rect rect,
        bool clampPosition = true)
    {
        rect.width =
            Mathf.Clamp(
                rect.width,
                0.001f,
                1f
            );


        rect.height =
            Mathf.Clamp(
                rect.height,
                0.001f,
                1f
            );


        if (!clampPosition)
        {
            return rect;
        }


        rect.x =
            Mathf.Clamp(
                rect.x,
                0f,
                Mathf.Max(
                    0f,
                    1f -
                    rect.width
                )
            );


        rect.y =
            Mathf.Clamp(
                rect.y,
                0f,
                Mathf.Max(
                    0f,
                    1f -
                    rect.height
                )
            );


        return rect;
    }


    // =========================================================
    // Tracking Lost
    // =========================================================

    private void HandleTrackingLost()
    {
        if (_trackingLost)
            return;


        if (
            Time.unscaledTime -
            _lastTrackingTime
            <
            lostTrackingResetTime
        )
        {
            return;
        }


        if (hidePartsWhenLost)
        {
            ResetStates();


            _lastProcessedTimestamp =
                -1;


            SetPartsVisible(
                false
            );


            _statesResetForLoss =
                true;
        }
        else
        {
            // Freeze the last valid state. Empty asynchronous results are a
            // tracking signal, not a request to blank the rendered face.
            _statesResetForLoss =
                false;
        }


        _trackingLost =
            true;
    }


    // =========================================================
    // Reset States
    // =========================================================

    private void ResetStates()
    {
        ResetState(
            _leftEyeState
        );

        ResetState(
            _rightEyeState
        );

        ResetState(
            _mouthState
        );


        _hasCoherentRawHistory = false;
        _lastCoherentTimestamp = -1;
        _coherentVerticalApplied = false;
        _hasSharedVerticalVelocity = false;
        _sharedVerticalVelocity = 0f;
    }


    private void ResetState(
        PartState state)
    {
        state.initialized =
            false;


        state.sampleRect =
            default;


        state.displayRect =
            default;


        state.lastRawCenter =
            Vector2.zero;


        state.centerVelocity =
            Vector2.zero;


        state.sampleTimestamp =
            -1;


        state.sampleArrivalTime =
            0f;
    }


    // =========================================================
    // Visibility
    // =========================================================

    private void SetPartsVisible(
        bool visible)
    {
        if (leftEyeImage != null)
        {
            leftEyeImage.enabled =
                visible;
        }


        if (rightEyeImage != null)
        {
            rightEyeImage.enabled =
                visible;
        }


        if (mouthImage != null)
        {
            mouthImage.enabled =
                visible;
        }
    }


    // =========================================================
    // Manual Reset
    // =========================================================

    public void ResetTracking()
    {
        ResetStates();


        _lastProcessedTimestamp =
            -1;


        _trackingLost =
            true;


        _statesResetForLoss =
            true;
    }
}


public static class KiwiFacePartCoherentMotionMath
{
    public static bool TryResolveSharedVerticalDelta(
        float leftDelta,
        float rightDelta,
        float mouthDelta,
        float sampleDt,
        float minimumSpeed,
        float deltaTolerance,
        out float sharedDelta,
        out float sharedSpeed)
    {
        sharedDelta = Median3(leftDelta, rightDelta, mouthDelta);
        sharedSpeed = Mathf.Abs(sharedDelta) / Mathf.Max(0.0001f, sampleDt);

        float maximumResidual = Mathf.Max(
            Mathf.Abs(leftDelta - sharedDelta),
            Mathf.Abs(rightDelta - sharedDelta),
            Mathf.Abs(mouthDelta - sharedDelta)
        );

        return
            IsFinite(leftDelta) &&
            IsFinite(rightDelta) &&
            IsFinite(mouthDelta) &&
            sharedSpeed >= Mathf.Max(0f, minimumSpeed) &&
            maximumResidual <= Mathf.Max(0f, deltaTolerance);
    }


    public static bool TryResolvePhaseLockedVerticalDeltas(
        float leftDelta,
        float rightDelta,
        float mouthDelta,
        float sampleDt,
        float minimumSpeed,
        float residualTolerance,
        out float resolvedLeftDelta,
        out float resolvedRightDelta,
        out float resolvedMouthDelta,
        out float sharedSpeed)
    {
        resolvedLeftDelta = leftDelta;
        resolvedRightDelta = rightDelta;
        resolvedMouthDelta = mouthDelta;
        sharedSpeed = 0f;

        if (
            !IsFinite(leftDelta) ||
            !IsFinite(rightDelta) ||
            !IsFinite(mouthDelta)
        )
        {
            return false;
        }

        float sharedDelta = Median3(leftDelta, rightDelta, mouthDelta);
        sharedSpeed = Mathf.Abs(sharedDelta) / Mathf.Max(0.0001f, sampleDt);
        if (sharedSpeed < Mathf.Max(0f, minimumSpeed))
        {
            return false;
        }

        float tolerance = Mathf.Max(0.000001f, residualTolerance);
        resolvedLeftDelta = sharedDelta + FilterLocalResidual(
            leftDelta - sharedDelta,
            tolerance
        );
        resolvedRightDelta = sharedDelta + FilterLocalResidual(
            rightDelta - sharedDelta,
            tolerance
        );
        resolvedMouthDelta = sharedDelta + FilterLocalResidual(
            mouthDelta - sharedDelta,
            tolerance
        );
        return true;
    }


    private static float FilterLocalResidual(
        float residual,
        float tolerance)
    {
        float magnitude = Mathf.Abs(residual);
        if (magnitude <= tolerance)
        {
            return 0f;
        }

        float release = Mathf.InverseLerp(
            tolerance,
            tolerance * 4f,
            magnitude
        );
        release = Mathf.SmoothStep(0f, 1f, release);
        return residual * release;
    }


    public static float Median3(float a, float b, float c)
    {
        return a + b + c - Mathf.Min(a, Mathf.Min(b, c)) - Mathf.Max(a, Mathf.Max(b, c));
    }


    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}


public static class KiwiFacePartRectStabilityMath
{
    public static bool ShouldHoldSize(
        Vector2 currentSize,
        Vector2 rawSize,
        float centerSpeed,
        float restSpeed,
        float sizeDeadZone)
    {
        if (
            !IsFinite(currentSize) ||
            !IsFinite(rawSize)
        )
        {
            return false;
        }


        Vector2 accumulatedChange = rawSize - currentSize;


        return
            Mathf.Max(0f, centerSpeed) < Mathf.Max(0f, restSpeed) &&
            Mathf.Abs(accumulatedChange.x) <= Mathf.Max(0f, sizeDeadZone) &&
            Mathf.Abs(accumulatedChange.y) <= Mathf.Max(0f, sizeDeadZone);
    }


    private static bool IsFinite(Vector2 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y);
    }
}


public static class KiwiFacePartPredictionMath
{
    public static float CalculatePredictionTime(
        bool useMatchedFrameAge,
        float matchedFrameAgeSeconds,
        float elapsedSinceResult,
        float leadSeconds,
        float maximumSeconds)
    {
        // KIWI_V4_8_SEMANTIC_PREDICTION_LIFETIME
        // Prediction compensates a fresh result; it is not a
        // pose that may remain extrapolated forever after the
        // semantic stream stalls.
        float elapsed =
            Mathf.Max(0f, elapsedSinceResult);

        float age =
            useMatchedFrameAge &&
            IsFinite(matchedFrameAgeSeconds) &&
            matchedFrameAgeSeconds >= 0f
                ? Mathf.Max(matchedFrameAgeSeconds, elapsed)
                : elapsed;

        if (
            age >
                KiwiCommercialFacePartPolicy.MaximumSemanticSourceAgeSeconds
        )
        {
            return 0f;
        }

        float liveCap =
            Mathf.Min(
                Mathf.Max(0f, maximumSeconds),
                0.050f);

        float freshness =
            1f -
            Mathf.InverseLerp(
                0.120f,
                KiwiCommercialFacePartPolicy.MaximumSemanticSourceAgeSeconds,
                age);

        float predictionStrength =
            Mathf.Lerp(
                0.35f,
                1f,
                Mathf.Clamp01(freshness));

        return
            Mathf.Clamp(
                age + Mathf.Max(0f, leadSeconds),
                0f,
                liveCap) *
            predictionStrength;
    }


    public static Vector2 PredictCenter(
        Vector2 center,
        Vector2 velocity,
        float predictionSeconds,
        float maximumDistance)
    {
        if (
            !IsFinite(center.x) ||
            !IsFinite(center.y) ||
            !IsFinite(velocity.x) ||
            !IsFinite(velocity.y) ||
            !IsFinite(predictionSeconds)
        )
        {
            return center;
        }

        Vector2 displacement =
            velocity * Mathf.Max(0f, predictionSeconds);

        float distanceLimit =
            Mathf.Max(0f, maximumDistance);

        if (
            distanceLimit > 0f &&
            displacement.sqrMagnitude > distanceLimit * distanceLimit
        )
        {
            displacement =
                displacement.normalized * distanceLimit;
        }

        return center + displacement;
    }


    public static Vector2 PredictCenterPhaseLocked(
        Vector2 center,
        float horizontalVelocity,
        float sharedVerticalVelocity,
        float predictionSeconds,
        float maximumAxisDistance)
    {
        if (
            !IsFinite(center.x) ||
            !IsFinite(center.y) ||
            !IsFinite(horizontalVelocity) ||
            !IsFinite(sharedVerticalVelocity) ||
            !IsFinite(predictionSeconds)
        )
        {
            return center;
        }

        float seconds = Mathf.Max(0f, predictionSeconds);
        float distanceLimit = Mathf.Max(0f, maximumAxisDistance);
        float horizontal = horizontalVelocity * seconds;
        float vertical = sharedVerticalVelocity * seconds;

        if (distanceLimit > 0f)
        {
            horizontal = Mathf.Clamp(horizontal, -distanceLimit, distanceLimit);
            vertical = Mathf.Clamp(vertical, -distanceLimit, distanceLimit);
        }

        return center + new Vector2(horizontal, vertical);
    }


    private static bool IsFinite(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }
}


public static class KiwiFacePartContinuityMath
{
    public static bool IsMouthSamplePlausible(
        Vector2 previousLeftEye,
        Vector2 previousRightEye,
        Vector2 previousMouth,
        Vector2 currentLeftEye,
        Vector2 currentRightEye,
        Vector2 currentMouth,
        float absoluteTolerance,
        float eyeSpanMultiplier)
    {
        Vector2 previousEyeCenter =
            (previousLeftEye + previousRightEye) * 0.5f;


        Vector2 currentEyeCenter =
            (currentLeftEye + currentRightEye) * 0.5f;


        Vector2 expectedMouth =
            previousMouth +
            (currentEyeCenter - previousEyeCenter);


        float eyeSpan =
            Mathf.Max(
                Vector2.Distance(previousLeftEye, previousRightEye),
                Vector2.Distance(currentLeftEye, currentRightEye)
            );


        float tolerance =
            Mathf.Max(
                Mathf.Max(0f, absoluteTolerance),
                eyeSpan * Mathf.Max(0f, eyeSpanMultiplier)
            );


        return Vector2.Distance(currentMouth, expectedMouth) <= tolerance;
    }
}


public static class KiwiMouthCropMath
{
    public static UnityEngine.Rect CalculateCenteredUvRect(
        Vector2 center,
        float width,
        float height)
    {
        float safeWidth = Mathf.Clamp(width, 0.001f, 1f);
        float safeHeight = Mathf.Clamp(height, 0.001f, 1f);

        return new UnityEngine.Rect(
            center.x - safeWidth * 0.5f,
            center.y - safeHeight * 0.5f,
            safeWidth,
            safeHeight
        );
    }


    public static UnityEngine.Rect CalculateSafeRect(
        Vector2 left,
        Vector2 right,
        Vector2 contourMin,
        Vector2 contourMax,
        float sourceAspect,
        float widthScale,
        float heightToWidth,
        float paddingX,
        float paddingY,
        float safetyX,
        float safetyY)
    {
        float safeAspect = Mathf.Max(0.01f, sourceAspect);
        float dx = right.x - left.x;
        float dy = (right.y - left.y) / safeAspect;
        float baseWidth = Mathf.Sqrt(dx * dx + dy * dy);

        float fixedWidth =
            baseWidth * Mathf.Max(1f, widthScale) +
            Mathf.Max(0f, paddingX) * 2f;

        float fixedHeight =
            baseWidth * safeAspect * Mathf.Max(0.01f, heightToWidth) +
            Mathf.Max(0f, paddingY) * 2f;

        float contourWidth = Mathf.Max(0f, contourMax.x - contourMin.x);
        float contourHeight = Mathf.Max(0f, contourMax.y - contourMin.y);

        float safeWidth = Mathf.Max(
            fixedWidth,
            contourWidth + baseWidth * Mathf.Max(0f, safetyX) * 2f
        );

        float safeHeight = Mathf.Max(
            fixedHeight,
            contourHeight + baseWidth * safeAspect * Mathf.Max(0f, safetyY) * 2f
        );

        Vector2 center = (contourMin + contourMax) * 0.5f;

        return new UnityEngine.Rect(
            center.x - safeWidth * 0.5f,
            center.y - safeHeight * 0.5f,
            safeWidth,
            safeHeight
        );
    }
}
