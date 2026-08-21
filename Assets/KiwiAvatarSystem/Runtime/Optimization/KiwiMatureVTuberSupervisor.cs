using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// KiwiAvatarSystem v4.5 mature VTuber control plane.
///
/// One component owns policy, one component owns temporal pose presentation,
/// and eye/mouth retain their own lighter channel-specific presentation.
/// Tracking loss, provider switches and reacquisition are represented explicitly
/// instead of changing smoothing/prediction policy from one sample to the next.
/// </summary>
[DefaultExecutionOrder(32000)]
[DisallowMultipleComponent]
public sealed class KiwiMatureVTuberSupervisor : MonoBehaviour
{
    public const string Version =
        "4.5.0-on-screen-panel-controls";

    private const string RuntimeObjectName =
        "[Kiwi] Mature VTuber Supervisor";

    [Header("Adaptive motion policy")]
    public bool enableAdaptiveMotionPolicy = true;

    [Range(0.04f, 0.50f)]
    public float profileUpdateSeconds = 0.10f;

    [Range(1f, 30f)]
    public float policyQualityRiseResponse = 4.5f;

    [Range(1f, 40f)]
    public float policyQualityFallResponse = 11f;

    [Range(0.01f, 1.0f)]
    public float positionHighIntensitySpeed = 0.22f;

    [Range(20f, 360f)]
    public float rotationHighIntensityDegreesPerSecond = 95f;

    [Header("Face-part policy")]
    public bool tuneFaceParts = true;

    [Header("Commercial profile multipliers")]
    [Tooltip("Persistent user profile multiplier. This scales the existing eye presentation owner; it does not add another filter.")]
    [Range(0.65f, 1.35f)]
    public float userEyeResponseMultiplier = 1f;

    [Tooltip("Persistent user profile multiplier for mouth presentation.")]
    [Range(0.65f, 1.35f)]
    public float userMouthResponseMultiplier = 1f;

    [Tooltip("Persistent user profile multiplier for semantic contour presentation.")]
    [Range(0.65f, 1.35f)]
    public float userContourResponseMultiplier = 1f;

    [Tooltip("Runtime resource governor multiplier for the auxiliary MediaPipe correction cadence.")]
    [Range(0.70f, 1.30f)]
    public float runtimeAuxiliaryCadenceScale = 1f;

    [Header("Model switch policy")]
    public bool tuneModelSwitch = true;

    [Header("Diagnostics")]
    [SerializeField] private string debugMode = "-";
    [SerializeField] private string debugProvider = "-";
    [SerializeField] private string debugContinuity = "-";
    [SerializeField] private float debugTrackingHz;
    [SerializeField] private float debugFreshSourceHz;
    [SerializeField] private float debugSourceAgeMs;
    [SerializeField] private float debugGeometryQuality;
    [SerializeField] private float debugMotionIntensity;
    [SerializeField] private float debugPipelineQuality;
    [SerializeField] private float debugSmoothedPolicyQuality;
    [SerializeField] private float debugPredictionAllowance;
    [SerializeField] private float debugAppliedPredictionStrength;
    [SerializeField] private float debugAppliedPredictionCapMs;
    [SerializeField] private float debugAuxiliaryMediaPipeHz;
    [SerializeField] private string debugEyeSource = "-";
    [SerializeField] private bool debugExpressionsTrusted;
    [SerializeField] private float debugDualDomainQuality;
    [SerializeField] private float debugEye2dQuality;
    [SerializeField] private float debugMouth2dQuality;
    [SerializeField] private string debugLatencyProfile = "-";
    [SerializeField] private bool debugActorCalibrated;

    private KiwiTrackingQuality10Controller _motionController;
    private KiwiTrackingContinuityState _continuity;
    private KiwiFaceChannelContinuity _faceChannels;
    private KiwiDualDomainFaceQuality _dualDomain;
    private KiwiActorFaceCalibration _actorCalibration;
    private KiwiLatencyBudgetController _latencyBudget;
    private KiwiInferenceRecoveryBootstrap _inferenceRecovery;
    private KiwiTrackingProviderHub _trackingHub;
    private FaceLandmarkerRunner _runner;
    private KiwiFaceMotion _faceMotion;
    private FacePartCropper _cropper;
    private KiwiFacePartQualityCoordinator _facePartCoordinator;
    private FacePartShapeMask[] _shapeMasks =
        Array.Empty<FacePartShapeMask>();

    private Transform _motionRoot;

    private Vector3 _previousPosition;
    private Quaternion _previousRotation =
        Quaternion.identity;

    private bool _hasPreviousPose;
    private double _lastPoseRealtime;
    private double _lastProfileRealtime;
    private double _nextProfileRealtime;
    private double _nextReferenceRefreshRealtime;

    private float _motionIntensityEma;
    private float _smoothedPolicyQuality = 0.60f;

    public string CurrentMode =>
        debugMode;

    public float CurrentPolicyQuality =>
        _smoothedPolicyQuality;

    public float CurrentAuxiliaryMediaPipeHz =>
        debugAuxiliaryMediaPipeHz;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiMatureVTuberSupervisor>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiMatureVTuberSupervisor>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _hasPreviousPose =
            false;

        _lastProfileRealtime =
            0.0;

        RefreshReferences(true);

        _nextProfileRealtime =
            0.0;
    }

    private void Start()
    {
        RefreshReferences(true);
        ApplyStableFacePartDefaults();
    }

    private void LateUpdate()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (
            now >=
            _nextReferenceRefreshRealtime
        )
        {
            _nextReferenceRefreshRealtime =
                now + 1.0;

            RefreshReferences(false);
        }

        MeasureMotionIntensity(now);

        if (
            !enableAdaptiveMotionPolicy ||
            now < _nextProfileRealtime
        )
        {
            return;
        }

        float policyDt =
            _lastProfileRealtime > 0.0
                ? Mathf.Clamp(
                    (float)(now - _lastProfileRealtime),
                    0.01f,
                    0.50f)
                : Mathf.Max(
                    0.01f,
                    profileUpdateSeconds);

        _lastProfileRealtime =
            now;

        _nextProfileRealtime =
            now +
            Mathf.Max(
                0.04f,
                profileUpdateSeconds);

        ApplyAdaptiveProfile(policyDt);
    }

    private void RefreshReferences(bool force)
    {
        if (force || _motionController == null)
        {
            _motionController =
                FindFirstObjectByType<KiwiTrackingQuality10Controller>(
                    FindObjectsInactive.Include);
        }

        if (force || _continuity == null)
        {
            _continuity =
                FindFirstObjectByType<KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (force || _faceChannels == null)
        {
            _faceChannels =
                FindFirstObjectByType<KiwiFaceChannelContinuity>(
                    FindObjectsInactive.Include);
        }

        if (force || _dualDomain == null)
        {
            _dualDomain =
                FindFirstObjectByType<KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (force || _actorCalibration == null)
        {
            _actorCalibration =
                FindFirstObjectByType<KiwiActorFaceCalibration>(
                    FindObjectsInactive.Include);
        }

        if (force || _latencyBudget == null)
        {
            _latencyBudget =
                FindFirstObjectByType<KiwiLatencyBudgetController>(
                    FindObjectsInactive.Include);
        }

        if (force || _inferenceRecovery == null)
        {
            _inferenceRecovery =
                FindFirstObjectByType<
                    KiwiInferenceRecoveryBootstrap>(
                    FindObjectsInactive.Include);
        }

        if (force || _trackingHub == null)
        {
            _trackingHub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (force || _runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (force || _faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
        }

        if (force || _facePartCoordinator == null)
        {
            _facePartCoordinator =
                FindFirstObjectByType<KiwiFacePartQualityCoordinator>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _shapeMasks == null ||
            _shapeMasks.Length == 0
        )
        {
            _shapeMasks =
                FindObjectsByType<FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
        }

        Transform nextRoot =
            _faceMotion != null
                ? _faceMotion.kiwiRoot
                : null;

        if (_motionRoot != nextRoot)
        {
            _motionRoot =
                nextRoot;

            _hasPreviousPose =
                false;
        }
    }

    private void MeasureMotionIntensity(double now)
    {
        if (_motionRoot == null)
        {
            _hasPreviousPose = false;
            return;
        }

        Vector3 position =
            _motionRoot.localPosition;

        Quaternion rotation =
            _motionRoot.localRotation;

        if (!_hasPreviousPose)
        {
            _previousPosition = position;
            _previousRotation = rotation;
            _lastPoseRealtime = now;
            _hasPreviousPose = true;
            return;
        }

        float dt =
            Mathf.Max(
                0.0001f,
                (float)(now - _lastPoseRealtime));

        float positionSpeed =
            Vector3.Distance(
                _previousPosition,
                position) /
            dt;

        float rotationSpeed =
            Quaternion.Angle(
                _previousRotation,
                rotation) /
            dt;

        float intensity =
            Mathf.Clamp01(
                Mathf.Max(
                    positionSpeed /
                    Mathf.Max(0.001f, positionHighIntensitySpeed),
                    rotationSpeed /
                    Mathf.Max(1f, rotationHighIntensityDegreesPerSecond)));

        _motionIntensityEma =
            Mathf.Lerp(
                _motionIntensityEma,
                intensity,
                1f - Mathf.Exp(-10f * dt));

        debugMotionIntensity =
            _motionIntensityEma;

        _previousPosition = position;
        _previousRotation = rotation;
        _lastPoseRealtime = now;
    }

    private void ApplyAdaptiveProfile(float policyDt)
    {
        if (
            _motionController == null ||
            _runner == null
        )
        {
            return;
        }

        float trackingHz =
            Mathf.Max(
                0f,
                _runner.LatestTrackingResultRateHz);

        float freshSourceHz =
            Mathf.Max(
                0f,
                _runner.LatestFreshSourceRateHz);

        FacePrecisionTrackingData data =
            default;

        string provider =
            string.Empty;

        bool hasData = false;

        if (_trackingHub != null)
        {
            hasData =
                _trackingHub.TryGetLatestFrame(
                    out data,
                    out provider);
        }
        else if (
            _runner.TryGetLatestPrecisionTrackingData(
                out data)
        )
        {
            // Compatibility only for scenes without the Provider Hub. When
            // the Hub exists, its liveness/source-age decision must remain the
            // single rigid tracking authority even for policy tuning.
            provider =
                data.backend ==
                    KiwiTrackingBackend.InferenceEngine
                    ? "Runner/InferenceEngine"
                    : "Runner/MediaPipe";

            hasData =
                data.isValid;
        }

        float geometryQuality =
            hasData
                ? Mathf.Clamp01(data.geometryQuality)
                : 0f;

        float sourceAgeSeconds =
            hasData
                ? CalculateSourceAgeSeconds(data)
                : 1f;

        float cadenceQuality =
            Mathf.InverseLerp(
                7f,
                28f,
                trackingHz);

        float ageQuality =
            1f -
            Mathf.InverseLerp(
                0.045f,
                0.180f,
                sourceAgeSeconds);

        float providerQuality =
            !string.IsNullOrEmpty(provider) &&
            provider.IndexOf(
                "InferenceEngine",
                StringComparison.OrdinalIgnoreCase) >= 0
                ? 1f
                : 0.58f;

        float rawPipelineQuality =
            Mathf.Clamp01(
                cadenceQuality * 0.42f +
                geometryQuality * 0.28f +
                ageQuality * 0.20f +
                providerQuality * 0.10f);

        KiwiTrackingContinuityState.ContinuityState continuityState =
            _continuity != null
                ? _continuity.State
                : KiwiTrackingContinuityState.ContinuityState.Stable;

        float continuityQuality =
            _continuity != null
                ? _continuity.QualityFactor
                : 1f;

        float predictionAllowance =
            _continuity != null
                ? _continuity.PredictionAllowance
                : 1f;

        float policyTarget =
            rawPipelineQuality *
            Mathf.Lerp(
                0.55f,
                1f,
                continuityQuality);

        float qualityResponse =
            policyTarget < _smoothedPolicyQuality
                ? policyQualityFallResponse
                : policyQualityRiseResponse;

        _smoothedPolicyQuality =
            SmoothTo(
                _smoothedPolicyQuality,
                policyTarget,
                qualityResponse,
                policyDt);

        bool inferenceHighRate =
            providerQuality > 0.90f &&
            trackingHz >= 18f &&
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Stable;

        bool continuityProtected =
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Degraded ||
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Holding ||
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring ||
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Lost;

        float severeAgeSeconds =
            inferenceHighRate
                ? 0.180f
                : 0.110f;

        bool degraded =
            continuityProtected ||
            trackingHz < 12f ||
            sourceAgeSeconds > severeAgeSeconds;

        debugTrackingHz = trackingHz;
        debugFreshSourceHz = freshSourceHz;
        debugSourceAgeMs = sourceAgeSeconds * 1000f;
        debugGeometryQuality = geometryQuality;
        debugPipelineQuality = rawPipelineQuality;
        debugSmoothedPolicyQuality = _smoothedPolicyQuality;
        debugPredictionAllowance = predictionAllowance;
        debugProvider = string.IsNullOrEmpty(provider) ? "-" : provider;
        debugContinuity = continuityState.ToString();

        debugMode =
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Lost
                ? "Lost / freeze"
                : continuityState ==
                    KiwiTrackingContinuityState.ContinuityState.Holding
                    ? "Transient hold"
                    : continuityState ==
                        KiwiTrackingContinuityState.ContinuityState.Reacquiring
                        ? "Reacquiring"
                        : inferenceHighRate
                            ? "High-rate"
                            : degraded
                                ? "Protected"
                                : "Balanced";

        ApplyAuxiliaryTrackerPolicy(
            policyDt);

        ApplyMotionProfile(
            _smoothedPolicyQuality,
            sourceAgeSeconds,
            degraded,
            inferenceHighRate,
            continuityState,
            predictionAllowance,
            policyDt);

        if (tuneFaceParts)
        {
            ApplyFacePartProfile(
                _smoothedPolicyQuality,
                degraded,
                inferenceHighRate,
                continuityState,
                policyDt);
        }

        if (tuneModelSwitch)
        {
            ApplyModelSwitchProfile();
        }
    }

    private void ApplyAuxiliaryTrackerPolicy(float dt)
    {
        if (_runner == null)
        {
            return;
        }

        float targetHz =
            (
                _continuity != null
                    ? _continuity.RecommendedMediaPipeRefreshHz
                    : 6f
            ) *
            Mathf.Clamp(
                runtimeAuxiliaryCadenceScale,
                0.70f,
                1.30f);

        _runner.sentisMediaPipeRefreshRateHz =
            SmoothTo(
                _runner.sentisMediaPipeRefreshRateHz,
                Mathf.Clamp(targetHz, 4f, 15f),
                6f,
                dt);

        debugAuxiliaryMediaPipeHz =
            _runner.sentisMediaPipeRefreshRateHz;
    }

    private void ApplyMotionProfile(
        float pipelineQuality,
        float sourceAgeSeconds,
        bool degraded,
        bool inferenceHighRate,
        KiwiTrackingContinuityState.ContinuityState continuityState,
        float predictionAllowance,
        float dt)
    {
        float intensity =
            _motionIntensityEma;

        float positionSmoothTarget =
            Mathf.Lerp(
                0.018f,
                0.010f,
                pipelineQuality) *
            Mathf.Lerp(
                1f,
                0.72f,
                intensity);

        float restingPositionSmoothTarget =
            Mathf.Lerp(
                0.036f,
                0.024f,
                pipelineQuality);

        float movingRotationResponseTarget =
            Mathf.Lerp(
                52f,
                82f,
                pipelineQuality) *
            Mathf.Lerp(
                1f,
                1.14f,
                intensity);

        float restingRotationResponseTarget =
            Mathf.Lerp(
                34f,
                44f,
                pipelineQuality);

        float predictionStrengthTarget =
            Mathf.Lerp(
                0.45f,
                0.86f,
                pipelineQuality) +
            intensity * 0.05f;

        float predictionCapTarget =
            Mathf.Lerp(
                0.050f,
                0.078f,
                pipelineQuality);

        if (_latencyBudget != null)
        {
            positionSmoothTarget *=
                _latencyBudget.PositionSmoothMultiplier;

            restingPositionSmoothTarget *=
                _latencyBudget.PositionSmoothMultiplier;

            movingRotationResponseTarget *=
                _latencyBudget.RotationResponseMultiplier;

            restingRotationResponseTarget *=
                Mathf.Lerp(
                    1f,
                    _latencyBudget.RotationResponseMultiplier,
                    0.65f);

            predictionStrengthTarget *=
                _latencyBudget.PredictionStrengthMultiplier;

            predictionCapTarget =
                Mathf.Min(
                    predictionCapTarget,
                    _latencyBudget.PredictionBudgetSeconds);
        }

        if (degraded)
        {
            predictionStrengthTarget =
                Mathf.Min(
                    predictionStrengthTarget,
                    0.58f);

            predictionCapTarget =
                Mathf.Min(
                    predictionCapTarget,
                    0.055f);
        }
        else if (inferenceHighRate)
        {
            predictionStrengthTarget =
                Mathf.Min(
                    predictionStrengthTarget,
                    0.82f);

            predictionCapTarget =
                Mathf.Min(
                    predictionCapTarget,
                    sourceAgeSeconds > 0.080f
                        ? 0.065f
                        : 0.045f);
        }
        else
        {
            predictionCapTarget =
                Mathf.Min(
                    predictionCapTarget,
                    0.050f);
        }

        predictionStrengthTarget *=
            predictionAllowance;

        predictionCapTarget *=
            Mathf.Lerp(
                0.45f,
                1f,
                predictionAllowance);

        if (
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Holding ||
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Lost
        )
        {
            predictionStrengthTarget = 0f;
            // v4.7: Holding/Lost must not keep a nominal prediction window.
            // Zero budget makes the latency policy unambiguous across owners.
            predictionCapTarget = 0f;
        }
        else if (
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring
        )
        {
            predictionStrengthTarget =
                Mathf.Min(
                    predictionStrengthTarget,
                    0.38f);

            predictionCapTarget =
                Mathf.Min(
                    predictionCapTarget,
                    0.035f);
        }

        _motionController.applyPresetOnlyOnce = true;
        _motionController.requestHighPresentationRate = true;
        _motionController.targetPresentationFrameRate = 120;

        _motionController.additionalDisplayLeadSeconds =
            SmoothTo(
                _motionController.additionalDisplayLeadSeconds,
                predictionAllowance > 0.5f ? 0.003f : 0f,
                10f,
                dt);

        _motionController.predictionStrength =
            SmoothTo(
                _motionController.predictionStrength,
                Mathf.Clamp(predictionStrengthTarget, 0f, 0.92f),
                9f,
                dt);

        _motionController.maximumPredictionSeconds =
            SmoothTo(
                _motionController.maximumPredictionSeconds,
                predictionCapTarget,
                9f,
                dt);

        _motionController.movingPositionSmoothTime =
            SmoothTo(
                _motionController.movingPositionSmoothTime,
                positionSmoothTarget,
                8f,
                dt);

        _motionController.restingPositionSmoothTime =
            SmoothTo(
                _motionController.restingPositionSmoothTime,
                restingPositionSmoothTarget,
                8f,
                dt);

        _motionController.movingRotationResponse =
            SmoothTo(
                _motionController.movingRotationResponse,
                movingRotationResponseTarget,
                8f,
                dt);

        _motionController.restingRotationResponse =
            SmoothTo(
                _motionController.restingRotationResponse,
                restingRotationResponseTarget,
                8f,
                dt);

        float reconciliationTarget =
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring
                ? 0.024f
                : Mathf.Lerp(
                    0.018f,
                    0.013f,
                    pipelineQuality);

        if (
            _latencyBudget != null &&
            continuityState !=
                KiwiTrackingContinuityState.ContinuityState.Reacquiring
        )
        {
            reconciliationTarget =
                Mathf.Lerp(
                    reconciliationTarget,
                    _latencyBudget.ReconciliationSeconds,
                    0.75f);
        }

        _motionController.highQualityReconciliationSeconds =
            SmoothTo(
                _motionController.highQualityReconciliationSeconds,
                0.006f,
                8f,
                dt);

        _motionController.lowQualityReconciliationSeconds =
            SmoothTo(
                _motionController.lowQualityReconciliationSeconds,
                reconciliationTarget,
                8f,
                dt);

        _motionController.maximumLowQualitySmoothBoost =
            SmoothTo(
                _motionController.maximumLowQualitySmoothBoost,
                Mathf.Lerp(
                    0.018f,
                    0.006f,
                    pipelineQuality),
                8f,
                dt);

        _motionController.minimumLowQualityPredictionFactor =
            0.20f;

        _motionController.adaptiveCadenceSmoothing =
            true;

        _motionController.cadenceLowRateHz =
            12f;

        _motionController.cadenceHighRateHz =
            28f;

        _motionController.maximumCadenceSmoothBoost =
            SmoothTo(
                _motionController.maximumCadenceSmoothBoost,
                Mathf.Lerp(
                    0.020f,
                    0.006f,
                    pipelineQuality),
                8f,
                dt);

        // v4.4 strict single-writer rule:
        // when adaptive Inference presence is enabled, the recovery bootstrap
        // is the sole runtime owner of both Runner and Quality10 thresholds.
        // The mature supervisor writes the parity default only when adaptive
        // ownership is not active.
        if (
            _inferenceRecovery == null ||
            !_inferenceRecovery.adaptPresenceThreshold
        )
        {
            _motionController.inferencePresenceThreshold =
                0.50f;
        }

        debugAppliedPredictionStrength =
            _motionController.predictionStrength;

        debugAppliedPredictionCapMs =
            _motionController.maximumPredictionSeconds * 1000f;
    }

    private void ApplyFacePartProfile(
        float pipelineQuality,
        bool degraded,
        bool inferenceHighRate,
        KiwiTrackingContinuityState.ContinuityState continuityState,
        float dt)
    {
        bool reacquiring =
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Reacquiring;

        bool holdingOrLost =
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Holding ||
            continuityState ==
                KiwiTrackingContinuityState.ContinuityState.Lost;

        bool expressionsTrusted =
            _faceChannels == null ||
            _faceChannels.ExpressionsTrusted;

        bool preferGeometryBlink =
            inferenceHighRate ||
            (
                _faceChannels != null &&
                _faceChannels.PreferGeometryBlink
            );

        bool useBlendshapeBlink =
            expressionsTrusted &&
            !preferGeometryBlink;

        debugExpressionsTrusted =
            expressionsTrusted;

        debugEyeSource =
            useBlendshapeBlink
                ? "Blendshape"
                : preferGeometryBlink
                    ? "InferenceGeometry"
                    : "GeometryFallback";

        float eye2dQuality =
            _dualDomain != null
                ? _dualDomain.EyeQuality
                : pipelineQuality;

        float mouth2dQuality =
            _dualDomain != null
                ? _dualDomain.MouthQuality
                : pipelineQuality;

        float dualDomainQuality =
            _dualDomain != null
                ? _dualDomain.DualDomainQuality
                : pipelineQuality;

        float facePartResponseMultiplier =
            _latencyBudget != null
                ? _latencyBudget.FacePartResponseMultiplier
                : 1f;

        debugEye2dQuality = eye2dQuality;
        debugMouth2dQuality = mouth2dQuality;
        debugDualDomainQuality = dualDomainQuality;

        debugLatencyProfile =
            _latencyBudget != null
                ? _latencyBudget.ResolvedProfile.ToString()
                : "-";

        debugActorCalibrated =
            _actorCalibration != null &&
            _actorCalibration.IsCalibrated;

        if (_cropper != null)
        {
            _cropper.strictLandmarkerTracking = false;
            _cropper.request120Fps = false;
            _cropper.hidePartsWhenLost = false;
            _cropper.lostTrackingResetTime = 0.45f;

            float eyeResponseTarget =
                (
                    reacquiring
                        ? 92f
                        : Mathf.Lerp(
                            112f,
                            145f,
                            pipelineQuality)
                ) *
                facePartResponseMultiplier *
                Mathf.Clamp(
                    userEyeResponseMultiplier,
                    0.65f,
                    1.35f) *
                Mathf.Lerp(
                    0.74f,
                    1f,
                    eye2dQuality);

            float mouthResponseTarget =
                (
                    reacquiring
                        ? 98f
                        : Mathf.Lerp(
                            120f,
                            152f,
                            pipelineQuality)
                ) *
                facePartResponseMultiplier *
                Mathf.Clamp(
                    userMouthResponseMultiplier,
                    0.65f,
                    1.35f) *
                Mathf.Lerp(
                    0.72f,
                    1f,
                    mouth2dQuality);

            _cropper.eyeRenderResponse =
                SmoothTo(
                    _cropper.eyeRenderResponse,
                    eyeResponseTarget,
                    10f,
                    dt);

            _cropper.mouthRenderResponse =
                SmoothTo(
                    _cropper.mouthRenderResponse,
                    mouthResponseTarget,
                    10f,
                    dt);

            _cropper.eyeRenderSizeResponse =
                SmoothTo(
                    _cropper.eyeRenderSizeResponse,
                    Mathf.Lerp(76f, 100f, pipelineQuality),
                    10f,
                    dt);

            _cropper.mouthRenderSizeResponse =
                SmoothTo(
                    _cropper.mouthRenderSizeResponse,
                    Mathf.Lerp(82f, 108f, pipelineQuality),
                    10f,
                    dt);

            _cropper.enablePrediction =
                !holdingOrLost;

            _cropper.compensateMatchedFrameAge =
                true;

            _cropper.directPositionDuringMotion =
                false;

            _cropper.predictionLeadSeconds =
                inferenceHighRate
                    ? 0.002f
                    : degraded
                        ? 0.003f
                        : 0.004f;

            float extrapolationTarget =
                holdingOrLost
                    ? 0.010f
                    : reacquiring
                        ? 0.025f
                        : inferenceHighRate
                            ? 0.060f
                            : degraded
                                ? 0.050f
                                : 0.045f;

            if (
                _latencyBudget != null &&
                !holdingOrLost
            )
            {
                extrapolationTarget =
                    Mathf.Min(
                        extrapolationTarget,
                        _latencyBudget.FacePartPredictionBudgetSeconds);
            }

            _cropper.maxExtrapolationSeconds =
                SmoothTo(
                    _cropper.maxExtrapolationSeconds,
                    extrapolationTarget,
                    10f,
                    dt);

            _cropper.maxPredictionDistance =
                inferenceHighRate
                    ? 0.0045f
                    : 0.0035f;

            _cropper.velocityResponse =
                inferenceHighRate
                    ? 125f
                    : 105f;

            _cropper.rejectIsolatedMouthOutliers =
                true;
        }

        if (_shapeMasks != null)
        {
            float contourResponse =
                (
                    reacquiring
                        ? 96f
                        : Mathf.Lerp(
                            94f,
                            128f,
                            pipelineQuality)
                ) *
                Mathf.Clamp(
                    userContourResponseMultiplier,
                    0.65f,
                    1.35f);

            for (int i = 0; i < _shapeMasks.Length; i++)
            {
                FacePartShapeMask mask =
                    _shapeMasks[i];

                if (mask == null)
                {
                    continue;
                }

                mask.strictLandmarkerTracking = false;
                mask.stabilizeSurfaceOcclusion = false;
                mask.stabilizeEyeVisibility = true;

                mask.useBlendshapeBlink =
                    useBlendshapeBlink;

                mask.useGeometryCloseFallback =
                    true;

                mask.eyeCloseConfirmationSamples =
                    preferGeometryBlink
                        ? 2
                        : 1;

                mask.eyeOpenConfirmationSamples = 1;
                mask.closedEyeVisibilityFloor = 0.42f;

                float partQuality =
                    mask.facePart ==
                        FacePartShapeMask.FacePartType.Mouth
                        ? mouth2dQuality
                        : eye2dQuality;

                float qualityAwareContourResponse =
                    contourResponse *
                    Mathf.Lerp(
                        0.76f,
                        1.05f,
                        partQuality);

                mask.contourRenderResponse =
                    SmoothTo(
                        mask.contourRenderResponse,
                        qualityAwareContourResponse,
                        10f,
                        dt);

                if (
                    _actorCalibration != null &&
                    _actorCalibration.IsCalibrated
                )
                {
                    mask.geometryCloseStart =
                        SmoothTo(
                            mask.geometryCloseStart,
                            _actorCalibration.SuggestedGeometryCloseStart,
                            4f,
                            dt);

                    mask.geometryCloseFull =
                        SmoothTo(
                            mask.geometryCloseFull,
                            _actorCalibration.SuggestedGeometryCloseFull,
                            4f,
                            dt);
                }

                mask.lockContourToMovingCrop = true;
                mask.cropLocalSafetyMargin = 0.016f;
                mask.eyeHideFadeSeconds = 0.016f;
                mask.eyeShowFadeSeconds = 0.028f;
                mask.fullVisibilityYaw = 66f;
                mask.hiddenVisibilityYaw = 82f;
            }
        }

        if (_facePartCoordinator != null)
        {
            _facePartCoordinator.enableDepthRatioGuard = true;
            _facePartCoordinator.farEyeDepthFadeStart = 0.30f;
            _facePartCoordinator.farEyeDepthHidden = 0.68f;
            _facePartCoordinator.enableSurfaceFacingGuard = true;
            _facePartCoordinator.fullVisibilityFacing = 0.18f;
            _facePartCoordinator.hiddenVisibilityFacing = -0.10f;
            _facePartCoordinator.facingCalibrationMaximumYaw = 15f;
            _facePartCoordinator.farEyeFadeStartYaw = 40f;
            _facePartCoordinator.farEyeHiddenYaw = 58f;
            _facePartCoordinator.nearEyeFadeStartYaw = 76f;
            _facePartCoordinator.nearEyeHiddenYaw = 87f;
            _facePartCoordinator.mouthFadeStartYaw = 70f;
            _facePartCoordinator.mouthHiddenYaw = 84f;
            _facePartCoordinator.visibilityHideResponse = 68f;
            _facePartCoordinator.visibilityShowResponse = 28f;
            _facePartCoordinator.clampFinalMouthDisplaySize = true;
            _facePartCoordinator.maximumMouthVisibleWidth = 0.66f;
            _facePartCoordinator.maximumMouthVisibleHeight = 0.60f;
        }
    }

    private void ApplyModelSwitchProfile()
    {
        if (_facePartCoordinator == null)
        {
            return;
        }

        _facePartCoordinator.refitAfterAvatarSwitch = true;
        _facePartCoordinator.requiredFreshTrackingFramesAfterSwap = 3;
        _facePartCoordinator.maximumTrackingWarmupSeconds = 0.45f;
        _facePartCoordinator.settleFramesBeforeFit = 2;
        _facePartCoordinator.maximumFitRetries = 2;
        _facePartCoordinator.facePartFadeInSeconds = 0.08f;
    }

    private void ApplyStableFacePartDefaults()
    {
        if (!tuneFaceParts)
        {
            return;
        }

        RefreshReferences(false);

        if (_facePartCoordinator != null)
        {
            _facePartCoordinator.eyeWidthScale = 1.60f;
            _facePartCoordinator.eyeHeightToWidth = 0.65f;
            _facePartCoordinator.mouthWidthScale = 1.50f;
            _facePartCoordinator.mouthHeightToWidth = 0.65f;
            _facePartCoordinator.eyeContourMargin = 0.095f;
            _facePartCoordinator.mouthContourMargin = 0.012f;
            _facePartCoordinator.maskFeather = 0.030f;
            _facePartCoordinator.cropLocalSafetyMargin = 0.016f;
            _facePartCoordinator.fittedSurfaceOffset = 0.0005f;
        }
    }

    private static float SmoothTo(
        float current,
        float target,
        float response,
        float dt)
    {
        if (
            float.IsNaN(current) ||
            float.IsInfinity(current)
        )
        {
            return target;
        }

        return
            Mathf.Lerp(
                current,
                target,
                1f -
                Mathf.Exp(
                    -Mathf.Max(0f, response) *
                    Mathf.Max(0.000001f, dt)));
    }

    private static float CalculateSourceAgeSeconds(
        FacePrecisionTrackingData data)
    {
        long referenceTicks =
            data.submissionHostTicks > 0L
                ? data.submissionHostTicks
                : data.arrivalHostTicks;

        if (referenceTicks <= 0L)
        {
            return 0f;
        }

        long now =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        long delta =
            now - referenceTicks;

        if (delta <= 0L)
        {
            return 0f;
        }

        return
            (float)(
                delta /
                (double)
                System.Diagnostics.Stopwatch.Frequency);
    }
}
