using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v4.1 dynamic containment envelope.
///
/// Source ROI and semantic mask are deliberately different responsibilities:
/// - Source ROI expands with measured motion/latency uncertainty.
/// - Semantic contour stays tight.
/// - Live GPU local tracking handles the residual translation.
///
/// This is the commercial-filter pattern "generous source crop + tight semantic
/// mask", but the overscan is adaptive instead of permanently wasting pixels.
/// </summary>
[DefaultExecutionOrder(32600)]
[DisallowMultipleComponent]
public sealed class KiwiFacePartAdaptiveContainment : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Adaptive Face-Part Containment";

    [Header("Eye source ROI")]
    [Range(1f, 3f)]
    public float eyeBaseWidthScale = 1.62f;

    [Range(1f, 3f)]
    public float eyeMaximumWidthScale = 2.20f;

    [Range(0.2f, 1.5f)]
    public float eyeBaseHeightToWidth = 0.65f;

    [Range(0.2f, 1.5f)]
    public float eyeMaximumHeightToWidth = 0.82f;

    [Range(0f, 0.10f)]
    public float eyeBasePaddingX = 0.017f;

    [Range(0f, 0.10f)]
    public float eyeMaximumPaddingX = 0.031f;

    [Range(0f, 0.10f)]
    public float eyeBasePaddingY = 0.015f;

    [Range(0f, 0.10f)]
    public float eyeMaximumPaddingY = 0.028f;

    [Header("Mouth source ROI")]
    [Range(1f, 3f)]
    public float mouthBaseWidthScale = 1.52f;

    [Range(1f, 3f)]
    public float mouthMaximumWidthScale = 2.10f;

    [Range(0.2f, 1.5f)]
    public float mouthBaseHeightToWidth = 0.66f;

    [Range(0.2f, 1.5f)]
    public float mouthMaximumHeightToWidth = 0.88f;

    [Range(0f, 0.10f)]
    public float mouthBasePaddingX = 0.018f;

    [Range(0f, 0.10f)]
    public float mouthMaximumPaddingX = 0.034f;

    [Range(0f, 0.10f)]
    public float mouthBasePaddingY = 0.020f;

    [Range(0f, 0.10f)]
    public float mouthMaximumPaddingY = 0.038f;

    [Range(0f, 0.8f)]
    public float mouthBaseContourSafetyX = 0.20f;

    [Range(0f, 0.8f)]
    public float mouthMaximumContourSafetyX = 0.34f;

    [Range(0f, 0.8f)]
    public float mouthBaseContourSafetyY = 0.24f;

    [Range(0f, 0.8f)]
    public float mouthMaximumContourSafetyY = 0.42f;

    [Header("Semantic mask")]
    [Range(-0.10f, 0.50f)]
    public float eyeBaseContourMargin = 0.095f;

    [Range(-0.10f, 0.50f)]
    public float eyeMaximumContourMargin = 0.112f;

    [Range(-0.10f, 0.20f)]
    public float mouthBaseContourMargin = 0.012f;

    [Range(-0.10f, 0.20f)]
    public float mouthMaximumContourMargin = 0.022f;

    [Header("Prediction envelope")]
    [Range(0.002f, 0.05f)]
    public float minimumPredictionDistance = 0.0045f;

    [Range(0.005f, 0.08f)]
    public float maximumPredictionDistance = 0.032f;

    [Range(0.01f, 0.30f)]
    public float ageUsedForRiskSeconds = 0.15f;

    [Header("Risk normalization")]
    [Range(0.05f, 0.60f)]
    public float fullTranslationRiskFaceWidth = 0.22f;

    [Range(0.03f, 0.40f)]
    public float fullScaleChange = 0.12f;

    [Range(5f, 60f)]
    public float fullRotationDegrees = 18f;

    [Range(0.05f, 1f)]
    public float fullCadenceJitterRatio = 0.35f;

    [Range(1f, 30f)]
    public float riskAttackResponse = 13f;

    [Range(1f, 20f)]
    public float riskReleaseResponse = 6f;

    [Header("Diagnostics")]
    [SerializeField] private float debugMotionRisk;
    [SerializeField] private float debugSourceAgeMs;
    [SerializeField] private float debugCenterSpeed;
    [SerializeField] private float debugScaleSpeed;
    [SerializeField] private float debugAngularSpeed;
    [SerializeField] private float debugExpectedTranslationUv;
    [SerializeField] private float debugEyeWidthScale;
    [SerializeField] private float debugMouthWidthScale;
    [SerializeField] private float debugPredictionDistance;

    private FaceLandmarkerRunner _runner;
    private FacePartCropper _cropper;
    private KiwiFacePartQualityCoordinator _coordinator;
    private KiwiTrackingContinuityState _continuity;

    private FacePartShapeMask[] _shapeMasks =
        System.Array.Empty<
            FacePartShapeMask>();

    private ulong _lastFrameId;
    private long _lastSampleHostTicks;

    private Vector2 _lastCenter;
    private float _lastFaceSize;
    private Quaternion _lastRotation =
        Quaternion.identity;

    private float _centerSpeed;
    private float _scaleSpeed;
    private float _angularSpeed;

    private float _motionRisk;

    public float MotionRisk =>
        _motionRisk;

    public float SourceAgeSeconds { get; private set; }

    public float ExpectedTranslationUv =>
        debugExpectedTranslationUv;

    public float AppliedEyeWidthScale =>
        debugEyeWidthScale;

    public float AppliedMouthWidthScale =>
        debugMouthWidthScale;

    public float AppliedPredictionDistance =>
        debugPredictionDistance;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiFacePartAdaptiveContainment>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(
            host);

        host.AddComponent<
            KiwiFacePartAdaptiveContainment>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _cropper = null;
        _coordinator = null;
        _continuity = null;

        _shapeMasks =
            System.Array.Empty<
                FacePartShapeMask>();

        ResetHistory();
        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        RefreshReferences(false);

        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.05f);

        ObserveTracking();

        float targetRisk =
            CalculateRisk();

        float response =
            targetRisk >
                _motionRisk
                ? riskAttackResponse
                : riskReleaseResponse;

        _motionRisk =
            Mathf.Lerp(
                _motionRisk,
                targetRisk,
                1f -
                Mathf.Exp(
                    -response *
                    dt));

        ApplyEnvelope(
            _motionRisk);

        UpdateDiagnostics();
    }

    private void ObserveTracking()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid
        )
        {
            SourceAgeSeconds =
                0f;

            return;
        }

        long now =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        long sourceTicks =
            data.submissionHostTicks >
                0L
                ? data.submissionHostTicks
                : data.arrivalHostTicks;

        if (
            sourceTicks >
                0L &&
            now >
                sourceTicks
        )
        {
            SourceAgeSeconds =
                Mathf.Clamp(
                    (float)
                    KiwiPrecisionTrackingMath
                        .HostTicksToSeconds(
                            now -
                            sourceTicks),
                    0f,
                    0.5f);
        }
        else
        {
            SourceAgeSeconds =
                0f;
        }

        if (
            data.frameId ==
                0UL ||
            data.frameId ==
                _lastFrameId
        )
        {
            return;
        }

        float faceSize =
            Mathf.Max(
                0.005f,
                data.faceWidth2D >
                    0.001f
                    ? data.faceWidth2D
                    : data.eyeSpan2D *
                        2.1f);

        if (
            _lastFrameId >
                0UL &&
            sourceTicks >
                _lastSampleHostTicks &&
            _lastSampleHostTicks >
                0L
        )
        {
            float sampleDt =
                Mathf.Clamp(
                    (float)
                    KiwiPrecisionTrackingMath
                        .HostTicksToSeconds(
                            sourceTicks -
                            _lastSampleHostTicks),
                    1f / 240f,
                    0.25f);

            float rawCenterSpeed =
                Vector2.Distance(
                    _lastCenter,
                    data.faceCenter) /
                Mathf.Max(
                    0.0001f,
                    sampleDt);

            float rawScaleSpeed =
                Mathf.Abs(
                    Mathf.Log(
                        faceSize /
                        Mathf.Max(
                            0.005f,
                            _lastFaceSize))) /
                Mathf.Max(
                    0.0001f,
                    sampleDt);

            float rawAngularSpeed =
                Quaternion.Angle(
                    _lastRotation,
                    data.faceRotation) /
                Mathf.Max(
                    0.0001f,
                    sampleDt);

            float velocityT =
                1f -
                Mathf.Exp(
                    -18f *
                    sampleDt);

            _centerSpeed =
                Mathf.Lerp(
                    _centerSpeed,
                    rawCenterSpeed,
                    velocityT);

            _scaleSpeed =
                Mathf.Lerp(
                    _scaleSpeed,
                    rawScaleSpeed,
                    velocityT);

            _angularSpeed =
                Mathf.Lerp(
                    _angularSpeed,
                    rawAngularSpeed,
                    velocityT);
        }

        _lastFrameId =
            data.frameId;

        _lastSampleHostTicks =
            sourceTicks;

        _lastCenter =
            data.faceCenter;

        _lastFaceSize =
            faceSize;

        _lastRotation =
            data.faceRotation;
    }

    private float CalculateRisk()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid
        )
        {
            return
                _continuity != null &&
                (
                    _continuity.State ==
                        KiwiTrackingContinuityState.ContinuityState.Holding ||
                    _continuity.State ==
                        KiwiTrackingContinuityState.ContinuityState.Lost
                )
                    ? 1f
                    : 0.35f;
        }

        float age =
            Mathf.Min(
                SourceAgeSeconds,
                ageUsedForRiskSeconds);

        float faceWidth =
            Mathf.Max(
                0.012f,
                data.faceWidth2D >
                    0.001f
                    ? data.faceWidth2D
                    : data.eyeSpan2D *
                        2.1f);

        float expectedTranslation =
            _centerSpeed *
            age;

        float translationRisk =
            expectedTranslation /
            Mathf.Max(
                0.003f,
                faceWidth *
                fullTranslationRiskFaceWidth);

        float scaleRisk =
            (
                _scaleSpeed *
                age
            ) /
            Mathf.Max(
                0.01f,
                fullScaleChange);

        float rotationRisk =
            (
                _angularSpeed *
                age
            ) /
            Mathf.Max(
                1f,
                fullRotationDegrees);

        float jitterRisk =
            _continuity != null
                ? _continuity.CadenceJitterRatio /
                    Mathf.Max(
                        0.01f,
                        fullCadenceJitterRatio)
                : 0f;

        float stateFloor =
            0f;

        if (_continuity != null)
        {
            switch (
                _continuity.State)
            {
                case KiwiTrackingContinuityState.ContinuityState.Degraded:
                    stateFloor =
                        0.45f;
                    break;

                case KiwiTrackingContinuityState.ContinuityState.Reacquiring:
                    stateFloor =
                        0.68f;
                    break;

                case KiwiTrackingContinuityState.ContinuityState.Holding:
                case KiwiTrackingContinuityState.ContinuityState.Lost:
                    stateFloor =
                        1f;
                    break;
            }
        }

        debugExpectedTranslationUv =
            expectedTranslation;

        return
            Mathf.Clamp01(
                Mathf.Max(
                    stateFloor,
                    Mathf.Max(
                        translationRisk,
                        Mathf.Max(
                            scaleRisk,
                            Mathf.Max(
                                rotationRisk,
                                jitterRisk)))));
    }

    private void ApplyEnvelope(
        float risk)
    {
        if (_cropper == null)
        {
            return;
        }

        float eyeWidth =
            Mathf.Lerp(
                eyeBaseWidthScale,
                eyeMaximumWidthScale,
                risk);

        float eyeHeight =
            Mathf.Lerp(
                eyeBaseHeightToWidth,
                eyeMaximumHeightToWidth,
                risk);

        float eyePadX =
            Mathf.Lerp(
                eyeBasePaddingX,
                eyeMaximumPaddingX,
                risk);

        float eyePadY =
            Mathf.Lerp(
                eyeBasePaddingY,
                eyeMaximumPaddingY,
                risk);

        float mouthWidth =
            Mathf.Lerp(
                mouthBaseWidthScale,
                mouthMaximumWidthScale,
                risk);

        float mouthHeight =
            Mathf.Lerp(
                mouthBaseHeightToWidth,
                mouthMaximumHeightToWidth,
                risk);

        float mouthPadX =
            Mathf.Lerp(
                mouthBasePaddingX,
                mouthMaximumPaddingX,
                risk);

        float mouthPadY =
            Mathf.Lerp(
                mouthBasePaddingY,
                mouthMaximumPaddingY,
                risk);

        float mouthSafetyX =
            Mathf.Lerp(
                mouthBaseContourSafetyX,
                mouthMaximumContourSafetyX,
                risk);

        float mouthSafetyY =
            Mathf.Lerp(
                mouthBaseContourSafetyY,
                mouthMaximumContourSafetyY,
                risk);

        float predictionDistance =
            Mathf.Clamp(
                Mathf.Lerp(
                    minimumPredictionDistance,
                    maximumPredictionDistance,
                    risk),
                minimumPredictionDistance,
                maximumPredictionDistance);

        _cropper.eyeWidthScale =
            eyeWidth;

        _cropper.eyeHeightToWidth =
            eyeHeight;

        _cropper.eyePaddingX =
            eyePadX;

        _cropper.eyePaddingY =
            eyePadY;

        _cropper.mouthWidthScale =
            mouthWidth;

        _cropper.mouthHeightToWidth =
            mouthHeight;

        _cropper.mouthPaddingX =
            mouthPadX;

        _cropper.mouthPaddingY =
            mouthPadY;

        _cropper.mouthContourSafetyX =
            mouthSafetyX;

        _cropper.mouthContourSafetyY =
            mouthSafetyY;

        _cropper.maxPredictionDistance =
            predictionDistance;

        if (_coordinator != null)
        {
            _coordinator.eyeWidthScale =
                eyeWidth;

            _coordinator.eyeHeightToWidth =
                eyeHeight;

            _coordinator.eyePaddingX =
                eyePadX;

            _coordinator.eyePaddingY =
                eyePadY;

            _coordinator.mouthWidthScale =
                mouthWidth;

            _coordinator.mouthHeightToWidth =
                mouthHeight;

            _coordinator.mouthPaddingX =
                mouthPadX;

            _coordinator.mouthPaddingY =
                mouthPadY;

            _coordinator.mouthContourSafetyX =
                mouthSafetyX;

            _coordinator.mouthContourSafetyY =
                mouthSafetyY;
        }

        float eyeMargin =
            Mathf.Lerp(
                eyeBaseContourMargin,
                eyeMaximumContourMargin,
                risk);

        float mouthMargin =
            Mathf.Lerp(
                mouthBaseContourMargin,
                mouthMaximumContourMargin,
                risk);

        if (_shapeMasks != null)
        {
            for (
                int i = 0;
                i < _shapeMasks.Length;
                i++
            )
            {
                FacePartShapeMask mask =
                    _shapeMasks[i];

                if (mask == null)
                {
                    continue;
                }

                FacePartShapeMask.FacePartType type =
                    mask.facePart;

                if (
                    type ==
                    FacePartShapeMask.FacePartType.Mouth
                )
                {
                    mask.mouthContourMargin =
                        mouthMargin;
                }
                else
                {
                    mask.eyeContourMargin =
                        eyeMargin;
                }
            }
        }

        debugEyeWidthScale =
            eyeWidth;

        debugMouthWidthScale =
            mouthWidth;

        debugPredictionDistance =
            predictionDistance;
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _runner == null
        )
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _cropper == null
        )
        {
            _cropper =
                FindFirstObjectByType<
                    FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _coordinator == null
        )
        {
            _coordinator =
                FindFirstObjectByType<
                    KiwiFacePartQualityCoordinator>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _continuity == null
        )
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _shapeMasks == null ||
            _shapeMasks.Length ==
                0
        )
        {
            _shapeMasks =
                FindObjectsByType<
                    FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }
    }

    private void ResetHistory()
    {
        _lastFrameId =
            0UL;

        _lastSampleHostTicks =
            0L;

        _lastCenter =
            Vector2.zero;

        _lastFaceSize =
            0f;

        _lastRotation =
            Quaternion.identity;

        _centerSpeed =
            0f;

        _scaleSpeed =
            0f;

        _angularSpeed =
            0f;

        _motionRisk =
            0f;

        SourceAgeSeconds =
            0f;
    }

    private void UpdateDiagnostics()
    {
        debugMotionRisk =
            _motionRisk;

        debugSourceAgeMs =
            SourceAgeSeconds *
            1000f;

        debugCenterSpeed =
            _centerSpeed;

        debugScaleSpeed =
            _scaleSpeed;

        debugAngularSpeed =
            _angularSpeed;
    }
}
