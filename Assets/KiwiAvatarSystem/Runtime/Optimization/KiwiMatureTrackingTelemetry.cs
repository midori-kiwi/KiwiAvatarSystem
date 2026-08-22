using System;
using System.Reflection;
using System.Text;
using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Lightweight runtime telemetry for validating the mature tracking pipeline.
///
/// Visibility is controlled by the app-screen Commercial Setup dock. This intentionally reads the raw Inference tracker
/// counters as well as the Runner's accepted-result metrics so a future video
/// can distinguish "not scheduled", "readback stalled", and "scheduled but
/// rejected by quality/presence" without changing tracking behavior.
/// </summary>
[DefaultExecutionOrder(33000)]
[DisallowMultipleComponent]
public sealed class KiwiMatureTrackingTelemetry : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Mature Tracking Telemetry";

    [Header("Overlay")]
    public bool showOverlay = false;

    [Range(220f, 720f)]
    public float panelWidth = 460f;

    [Header("Diagnostics")]
    [SerializeField] private string debugProvider = "-";
    [SerializeField] private float debugRigidAnchorCorrection;
    [SerializeField] private bool debugProviderHandoffActive;
    [SerializeField] private string debugProviderHandoffTarget = "-";
    [SerializeField] private float debugProviderHandoffWeight;
    [SerializeField] private float debugProviderHandoffCenterOffset;
    [SerializeField] private float debugProviderHandoffRotationOffset;
    [SerializeField] private float debugProviderHandoffScaleRatio = 1f;
    [SerializeField] private int debugProviderHandoffCount;
    [SerializeField] private float debugProviderArrivalAgeMs;
    [SerializeField] private float debugProviderArrivalLimitMs;
    [SerializeField] private bool debugResumeReferenceValid;
    [SerializeField] private float debugResumeGapMs;
    [SerializeField] private int debugResumeHandoffCount;
    [SerializeField] private string debugNormalizationTimebase = "-";
    [SerializeField] private string debugNormalizationTimestampQuality = "-";
    [SerializeField] private string debugNormalizationSourceHorizontal = "-";
    [SerializeField] private bool debugNormalizationCanonicalMirrored;
    [SerializeField] private bool debugNormalizationHorizontalTransform;
    [SerializeField] private int debugNormalizationTimebaseResets;
    [SerializeField] private int debugNormalizationArrivalFallbacks;
    [SerializeField] private int debugNormalizationHorizontalTransforms;
    [SerializeField] private float debugFreshSourceHz;
    [SerializeField] private float debugResultHz;
    [SerializeField] private float debugReadbackMs;
    [SerializeField] private float debugSourceAgeMs;
    [SerializeField] private float debugGeometryQuality;
    [SerializeField] private int debugScheduled;
    [SerializeField] private int debugReadbackCompleted;
    [SerializeField] private int debugCompletedAccepted;
    [SerializeField] private int debugDroppedFresh;
    [SerializeField] private int debugPipelineDepth;
    [SerializeField] private int debugSchedulingLaneLimit;
    [SerializeField] private int debugActiveLanes;
    [SerializeField] private float debugOldestPendingMs;
    [SerializeField] private float debugRegionWidth;
    [SerializeField] private float debugRegionHeight;
    [SerializeField] private float debugRegionPixelAspectError;
    [SerializeField] private bool debugGammaPreservation;
    [SerializeField] private int debugRejectedPresence;
    [SerializeField] private int debugRejectedInvalid;
    [SerializeField] private int debugDiscardedStale;
    [SerializeField] private int debugDiscardedCrossSystem;
    [SerializeField] private float debugRawPresenceLogit;
    [SerializeField] private float debugPresenceProbability;
    [SerializeField] private string debugInferenceReject = "-";
    [SerializeField] private float debugTrackerLatencyMs;
    [SerializeField] private bool debugReadbackPending;
    [SerializeField] private bool debugHasRegion;
    [SerializeField] private float debugLivePresenceThreshold;
    [SerializeField] private float debugLeftEyeAlpha = 1f;
    [SerializeField] private float debugRightEyeAlpha = 1f;
    [SerializeField] private float debugMouthAlpha = 1f;
    [SerializeField] private Vector3 debugPartMaskVisibility = Vector3.one;
    [SerializeField] private Vector3Int debugPartMaskPoints;
    [SerializeField] private bool debugAllPartsMissingRecovery;
    [SerializeField] private int debugVisibilityRecoveries;
    [SerializeField] private string debugContinuityState = "-";
    [SerializeField] private float debugContinuityAgeMs;
    [SerializeField] private float debugContinuityArrivalAgeMs;
    [SerializeField] private float debugContinuityCadenceHz;
    [SerializeField] private float debugContinuityJitter;
    [SerializeField] private int debugReacquireStreak;
    [SerializeField] private float debugRigidPredictionAllowance = 1f;
    [SerializeField] private bool debugRigidHolding;
    [SerializeField] private bool debugRigidLost;
    [SerializeField] private float debugRigidPositionDeadZoneMultiplier = 1f;
    [SerializeField] private float debugRigidEffectivePositionDeadZone;
    [SerializeField] private float debugPolicyQuality;
    [SerializeField] private float debugAuxiliaryMediaPipeHz;
    [SerializeField] private string debugGeometryChannel = "-";
    [SerializeField] private string debugExpressionChannel = "-";
    [SerializeField] private string debugEyeSource = "-";
    [SerializeField] private bool debugAttachmentRecalibrationPending;
    [SerializeField] private int debugAttachmentRecalibrationCount;
    [SerializeField] private int debugAttachmentPendingCalibrationGeneration;
    [SerializeField] private float debugLeftEye2dQuality;
    [SerializeField] private float debugRightEye2dQuality;
    [SerializeField] private float debugMouth2dQuality;
    [SerializeField] private float debugDualDomainQuality;
    [SerializeField] private bool debugActorCalibrated;
    [SerializeField] private float debugNeutralEyeOpen;
    [SerializeField] private int debugActorCalibrationSamples;
    [SerializeField] private int debugActorProfileCalibrationGeneration;
    [SerializeField] private string debugLatencyProfile = "-";
    [SerializeField] private float debugPredictionBudgetMs;
    [SerializeField] private bool debugConstraintCalibrated;
    [SerializeField] private int debugConstraintCalibrationSamples;
    [SerializeField] private int debugConstraintProfileCalibrationGeneration;
    [SerializeField] private float debugConstraintCalibrationQuality;
    [SerializeField] private float debugSurfaceConstraintStrength;
    [SerializeField] private Vector2 debugSurfaceLeftEyeOffset;
    [SerializeField] private Vector2 debugSurfaceRightEyeOffset;
    [SerializeField] private Vector2 debugSurfaceMouthOffset;
    [SerializeField] private bool debugSurfaceApiAvailable;
    [SerializeField] private string debugSurfaceConstraintState = "-";
    [SerializeField] private bool debugLiveMotionOperational;
    [SerializeField] private float debugLiveMotionRateHz;
    [SerializeField] private float debugLiveMotionReadbackMs;
    [SerializeField] private int debugLiveMotionPending;
    [SerializeField] private int debugLiveMotionDropped;
    [SerializeField] private int debugLiveMotionStaleDropped;
    [SerializeField] private bool debugLiveMotionOverloadSuspended;
    [SerializeField] private int debugLiveMotionOverloadSuspensions;
    [SerializeField] private int debugLiveMotionSearchRadius;
    [SerializeField] private Vector3 debugLiveMotionConfidence;
    [SerializeField] private Vector2 debugLiveLeftCorrection;
    [SerializeField] private Vector2 debugLiveRightCorrection;
    [SerializeField] private Vector2 debugLiveMouthCorrection;
    [SerializeField] private Vector2 debugLiveExpectedRigidPixels;
    [SerializeField] private Vector2 debugLiveRejectedCommonPixels;
    [SerializeField] private Vector3 debugLiveLocalResidualPixels;
    [SerializeField] private bool debugLiveCanonicalAligned;
    [SerializeField] private float debugContainmentRisk;
    [SerializeField] private float debugContainmentAgeMs;
    [SerializeField] private float debugContainmentEyeScale;
    [SerializeField] private float debugContainmentMouthScale;
    [SerializeField] private float debugContainmentPredictionDistance;
    [SerializeField] private float debugEyePairRollDegrees;
    [SerializeField] private float debugEyePairRotationDeltaDegrees;
    [SerializeField] private float debugEyePairScale = 1f;
    [SerializeField] private bool debugEyePairFallback;
    [SerializeField] private bool debugMouthAnatomyClamped;
    [SerializeField] private float debugMouthSeparationRatio = 1f;
    [SerializeField] private float debugAnatomyCollisionSeverity;
    [SerializeField] private float debugAnatomyOverlapRatio;
    [SerializeField] private float debugAnatomySurfaceAsymmetry;
    [SerializeField] private string debugAnatomyOutlierEye = "-";
    [SerializeField] private float debugAnatomyMouthScale = 1f;
    [SerializeField] private string debugCommercialProfile = "-";
    [SerializeField] private string debugCommercialQualityTier = "-";
    [SerializeField] private float debugCommercialRenderFps;
    [SerializeField] private string debugPathfinderState = "-";
    [SerializeField] private float debugPathfinderScore;
    [SerializeField] private string debugPathfinderRecommendation = "-";
    [SerializeField] private float debugSemanticSourceAgeMs = -1f;
    [SerializeField] private int debugSemanticStaleRejects;
    [SerializeField] private long debugSemanticTransactionTimestamp = -1L;
    [SerializeField] private Vector3Int debugSemanticPartAccepted = Vector3Int.one;
    [SerializeField] private Vector3Int debugSemanticPartRejectCounts;
    [SerializeField] private bool debugMaskReadinessComplete;
    [SerializeField] private int debugCameraGeneration;
    [SerializeField] private string debugCameraGenerationReason = "None";
    [SerializeField] private int debugCameraGenerationEvents;
    [SerializeField] private int debugCameraIdentityChanges;
    [SerializeField] private int debugCameraRestarts;
    [SerializeField] private int debugCameraTimelineRegressions;
    [SerializeField] private int debugCameraStaleCallbackDrops;
    [SerializeField] private int debugCameraUnmatchedCallbackDrops;
    [SerializeField] private int debugLiveMotionSourceIdentityInvalidations;
    [SerializeField] private int debugProviderGeneration;
    [SerializeField] private int debugModelGeneration;
    [SerializeField] private int debugConfigEpoch;
    [SerializeField] private int debugCalibrationGeneration;
    [SerializeField] private string debugCalibrationReason = "-";
    [SerializeField] private string debugCalibrationScope = "None";
    [SerializeField] private int debugCalibrationEvents;
    [SerializeField] private int debugCalibrationCommits;
    [SerializeField] private int debugCalibrationRejectedCommits;
    [SerializeField] private string debugCalibrationLastCommitOwner = "-";
    [SerializeField] private int debugCalibrationLastCommitGeneration;
    [SerializeField] private int debugTrackingSessionGeneration;
    [SerializeField] private long debugObservationSequence;
    [SerializeField] private long debugSemanticTransactionSequence;
    [SerializeField] private int debugLiveMotionGenerationDrops;
    [SerializeField] private int debugLiveMotionCalibrationGenerationDrops;
    [SerializeField] private int debugLiveMotionSuppressionInvalidations;
    [SerializeField] private int debugPolicyVersion;
    [SerializeField] private string debugPolicySource = "-";
    [SerializeField] private float debugPolicyMediaPipeRefreshHz;
    [SerializeField] private float debugPolicyPresenceThreshold;
    [SerializeField] private int debugPolicyTrackingInputWidth;
    [SerializeField] private float debugPolicyAuxiliaryCadenceScale = 1f;
    [SerializeField] private string debugPresentationLeftReasons = "None";
    [SerializeField] private string debugPresentationRightReasons = "None";
    [SerializeField] private string debugPresentationMouthReasons = "None";
    [SerializeField] private string debugGlobalRecoveryState = "Starting";
    [SerializeField] private long debugGlobalRecoverySequence;
    [SerializeField] private string debugGlobalRecoveryReason = "None";
    [SerializeField] private string debugGlobalRecoverySource = "-";
    [SerializeField] private string debugSemanticRecoveryHealth = "Healthy";
    [SerializeField] private string debugSemanticRecoveryComponents = "None";
    [SerializeField] private long debugSemanticRecoverySequence;
    [SerializeField] private string debugSemanticRecoveryReason = "None";
    [SerializeField] private string debugSemanticRecoverySource = "-";
    [SerializeField] private int debugSemanticRecoveryRequests;
    [SerializeField] private string debugValidationHealth = "Starting";
    [SerializeField] private int debugValidationActive;
    [SerializeField] private int debugValidationTotal;
    [SerializeField] private int debugValidationWarnings;
    [SerializeField] private int debugValidationErrors;
    [SerializeField] private int debugValidationCriticals;
    [SerializeField] private string debugValidationLastCode = "None";
    [SerializeField] private int debugValidationLastFrame = -1;
    [SerializeField] private string debugAcceptanceState = "Disabled";
    [SerializeField] private string debugAcceptanceScenario = "None";
    [SerializeField] private int debugAcceptancePassed;
    [SerializeField] private int debugAcceptanceFailed;
    [SerializeField] private string debugAcceptanceLastScenario = "None";
    [SerializeField] private string debugAcceptanceMatrix = "NotRun";

    private FaceLandmarkerRunner _runner;
    private KiwiFaceMotion _faceMotion;
    private KiwiTrackingProviderHub _hub;
    private KiwiFacePartVisibilityRecovery _visibilityRecovery;
    private KiwiFacePartPresentationResolver _presentationResolver;
    private KiwiRecoveryDomainCoordinator _recoveryDomains;
    private KiwiTrackingContinuityState _continuity;
    private KiwiFaceChannelContinuity _faceChannels;
    private KiwiFaceAttachmentRecalibration _attachmentRecalibration;
    private KiwiDualDomainFaceQuality _dualDomain;
    private KiwiActorFaceCalibration _actorCalibration;
    private KiwiLatencyBudgetController _latencyBudget;
    private KiwiModelPrimaryFacePartConstraint _modelPrimaryConstraint;
    private KiwiFacePartLiveMotionBridge _liveMotionBridge;
    private KiwiFacePartAdaptiveContainment _adaptiveContainment;
    private KiwiFacePartAnatomyGuard _anatomyGuard;
    private KiwiMatureVTuberSupervisor _supervisor;
    private KiwiCommercialProfileController _commercialProfile;
    private KiwiCommercialQualityGovernor _commercialQuality;
    private KiwiCommercialPathfinder _pathfinder;

    private GUIStyle _style;
    private double _nextRefreshRealtime;
    private readonly StringBuilder _panelBuilder =
        new StringBuilder(4096);
    private string _cachedPanelText =
        "Kiwi v5.1 Phase 11 Runtime Validation Telemetry";

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiMatureTrackingTelemetry>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiMatureTrackingTelemetry>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        RefreshReferences();
    }

    public bool IsOverlayVisible =>
        showOverlay;

    /// <summary>
    /// Screen-space rectangle owned by this overlay. Other Kiwi diagnostic
    /// panels use the same contract so independently auto-installed IMGUI
    /// surfaces never compete for the top-right corner.
    /// </summary>
    public Rect OverlayRect =>
        CalculateOverlayRect();

    private Rect CalculateOverlayRect()
    {
        float maximumWidth =
            Mathf.Max(1f, Screen.width - 16f);

        float minimumWidth =
            Mathf.Min(220f, maximumWidth);

        float width =
            Mathf.Clamp(
                panelWidth,
                minimumWidth,
                maximumWidth);

        float comparisonReserve = 0f;
        KiwiFrameComparisonOverlay comparison =
            KiwiFrameComparisonOverlay.Instance;

        if (
            comparison != null &&
            comparison.visible
        )
        {
            comparisonReserve =
                comparison.PreferredPanelHeight + 20f;
        }

        float height =
            Mathf.Min(
                666f,
                Mathf.Max(1f, Screen.height - 16f - comparisonReserve));

        float x =
            Mathf.Max(
                8f,
                Screen.width -
                width -
                8f);

        return new Rect(
            x,
            8f,
            width,
            height);
    }

    public void SetOverlayVisible(
        bool visible)
    {
        showOverlay =
            visible;
    }

    public void ToggleOverlayVisible()
    {
        showOverlay =
            !showOverlay;
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (now < _nextRefreshRealtime)
        {
            return;
        }

        _nextRefreshRealtime =
            now + 0.20;

        RefreshReferences();
        RefreshTelemetry();
        BuildPanelText();
    }

    private void RefreshReferences()
    {
        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (_faceMotion == null)
        {
            _faceMotion =
                FindFirstObjectByType<KiwiFaceMotion>(
                    FindObjectsInactive.Include);
        }

        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (_visibilityRecovery == null)
        {
            _visibilityRecovery =
                FindFirstObjectByType<KiwiFacePartVisibilityRecovery>(
                    FindObjectsInactive.Include);
        }

        if (_presentationResolver == null)
        {
            _presentationResolver =
                FindFirstObjectByType<KiwiFacePartPresentationResolver>(
                    FindObjectsInactive.Include);
        }

        if (_recoveryDomains == null)
        {
            _recoveryDomains =
                FindFirstObjectByType<KiwiRecoveryDomainCoordinator>(
                    FindObjectsInactive.Include);
        }

        if (_continuity == null)
        {
            _continuity =
                FindFirstObjectByType<KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (_faceChannels == null)
        {
            _faceChannels =
                FindFirstObjectByType<KiwiFaceChannelContinuity>(
                    FindObjectsInactive.Include);
        }

        if (_attachmentRecalibration == null)
        {
            _attachmentRecalibration =
                FindFirstObjectByType<KiwiFaceAttachmentRecalibration>(
                    FindObjectsInactive.Include);
        }

        if (_dualDomain == null)
        {
            _dualDomain =
                FindFirstObjectByType<KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (_actorCalibration == null)
        {
            _actorCalibration =
                FindFirstObjectByType<KiwiActorFaceCalibration>(
                    FindObjectsInactive.Include);
        }

        if (_latencyBudget == null)
        {
            _latencyBudget =
                FindFirstObjectByType<KiwiLatencyBudgetController>(
                    FindObjectsInactive.Include);
        }

        if (_modelPrimaryConstraint == null)
        {
            _modelPrimaryConstraint =
                FindFirstObjectByType<
                    KiwiModelPrimaryFacePartConstraint>(
                    FindObjectsInactive.Include);
        }

        if (_liveMotionBridge == null)
        {
            _liveMotionBridge =
                FindFirstObjectByType<
                    KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include);
        }

        if (_adaptiveContainment == null)
        {
            _adaptiveContainment =
                FindFirstObjectByType<
                    KiwiFacePartAdaptiveContainment>(
                    FindObjectsInactive.Include);
        }

        if (_anatomyGuard == null)
        {
            _anatomyGuard =
                FindFirstObjectByType<
                    KiwiFacePartAnatomyGuard>(
                    FindObjectsInactive.Include);
        }

        if (_supervisor == null)
        {
            _supervisor =
                FindFirstObjectByType<KiwiMatureVTuberSupervisor>(
                    FindObjectsInactive.Include);
        }

        if (_commercialProfile == null)
        {
            _commercialProfile =
                FindFirstObjectByType<
                    KiwiCommercialProfileController>(
                    FindObjectsInactive.Include);
        }

        if (_commercialQuality == null)
        {
            _commercialQuality =
                FindFirstObjectByType<
                    KiwiCommercialQualityGovernor>(
                    FindObjectsInactive.Include);
        }

        if (_pathfinder == null)
        {
            _pathfinder =
                FindFirstObjectByType<
                    KiwiCommercialPathfinder>(
                    FindObjectsInactive.Include);
        }
    }

    private void RefreshTelemetry()
    {
        if (_runner == null)
        {
            return;
        }

        debugFreshSourceHz =
            _runner.LatestFreshSourceRateHz;

        debugResultHz =
            _runner.LatestTrackingResultRateHz;

        debugReadbackMs =
            _runner.LatestReadbackLatencyMs;

        debugLivePresenceThreshold =
            _runner.sentisMinimumPresence;

        FacePrecisionTrackingData data =
            default;

        string provider =
            string.Empty;

        bool hasData =
            _hub != null &&
            _hub.TryGetLatestFrame(
                out data,
                out provider);

        if (
            !hasData &&
            _runner.TryGetLatestPrecisionTrackingData(
                out data)
        )
        {
            hasData = true;

            provider =
                data.backend ==
                    KiwiTrackingBackend.InferenceEngine
                    ? "Runner/InferenceEngine"
                    : "Runner/MediaPipe";
        }

        debugProvider =
            string.IsNullOrEmpty(provider)
                ? "-"
                : provider;

        if (_hub != null)
        {
            debugRigidAnchorCorrection =
                _hub.ActiveRigidAnchorCorrection;

            debugProviderHandoffActive =
                _hub.HandoffActive;

            debugProviderHandoffTarget =
                string.IsNullOrEmpty(
                    _hub.HandoffProviderId)
                    ? "-"
                    : _hub.HandoffProviderId;

            debugProviderHandoffWeight =
                _hub.HandoffWeight;

            debugProviderHandoffCenterOffset =
                _hub.HandoffCenterOffsetMagnitude;

            debugProviderHandoffRotationOffset =
                _hub.HandoffRotationOffsetDegrees;

            debugProviderHandoffScaleRatio =
                _hub.HandoffScaleRatio;

            debugProviderHandoffCount =
                _hub.HandoffCount;

            debugProviderArrivalAgeMs =
                _hub.ActiveProviderArrivalAgeMilliseconds;

            debugProviderArrivalLimitMs =
                _hub.ActiveProviderArrivalLimitMilliseconds;

            debugResumeReferenceValid =
                _hub.ResumeReferenceValid;

            debugResumeGapMs =
                _hub.ResumeGapMilliseconds;

            debugResumeHandoffCount =
                _hub.ResumeHandoffCount;

            debugNormalizationTimebase =
                _hub.ActiveSourceTimebase.ToString();
            debugNormalizationTimestampQuality =
                _hub.ActiveTimestampQuality.ToString();
            debugNormalizationSourceHorizontal =
                _hub.ActiveSourceHorizontalConvention.ToString();
            debugNormalizationCanonicalMirrored =
                _hub.CanonicalInputHorizontallyMirrored;
            debugNormalizationHorizontalTransform =
                _hub.ActiveHorizontalTransformApplied;
            debugNormalizationTimebaseResets =
                _hub.ActiveTimebaseResetCount;
            debugNormalizationArrivalFallbacks =
                _hub.ArrivalFallbackCount;
            debugNormalizationHorizontalTransforms =
                _hub.HorizontalTransformCount;
        }
        else
        {
            debugRigidAnchorCorrection = 0f;
            debugProviderHandoffActive = false;
            debugProviderHandoffTarget = "-";
            debugProviderHandoffWeight = 0f;
            debugProviderHandoffCenterOffset = 0f;
            debugProviderHandoffRotationOffset = 0f;
            debugProviderHandoffScaleRatio = 1f;
            debugProviderHandoffCount = 0;
            debugProviderArrivalAgeMs = 0f;
            debugProviderArrivalLimitMs = 0f;
            debugResumeReferenceValid = false;
            debugResumeGapMs = 0f;
            debugResumeHandoffCount = 0;
            debugNormalizationTimebase = "-";
            debugNormalizationTimestampQuality = "-";
            debugNormalizationSourceHorizontal = "-";
            debugNormalizationCanonicalMirrored = false;
            debugNormalizationHorizontalTransform = false;
            debugNormalizationTimebaseResets = 0;
            debugNormalizationArrivalFallbacks = 0;
            debugNormalizationHorizontalTransforms = 0;
        }

        debugGeometryQuality =
            hasData
                ? data.geometryQuality
                : 0f;

        debugSourceAgeMs =
            hasData
                ? CalculateSourceAgeSeconds(data) * 1000f
                : 0f;

        object tracker =
            GetPrivateField(
                _runner,
                "_sentisTracker");

        debugScheduled =
            GetPublicIntProperty(
                tracker,
                "ScheduledFrameCount");

        debugReadbackCompleted =
            GetPublicIntProperty(
                tracker,
                "ReadbackCompletedFrameCount");

        debugCompletedAccepted =
            GetPublicIntProperty(
                tracker,
                "CompletedFrameCount");

        debugDroppedFresh =
            GetPublicIntProperty(
                tracker,
                "DroppedFreshFrameCount");

        debugPipelineDepth =
            GetPublicIntProperty(
                tracker,
                "PipelineDepth");

        debugSchedulingLaneLimit =
            GetPublicIntProperty(
                tracker,
                "SchedulingLaneLimit");

        debugActiveLanes =
            GetPublicIntProperty(
                tracker,
                "ActiveLaneCount");

        debugOldestPendingMs =
            GetPublicFloatProperty(
                tracker,
                "OldestPendingAgeMs");

        debugRegionWidth =
            GetPublicFloatProperty(
                tracker,
                "RegionWidthNormalized");

        debugRegionHeight =
            GetPublicFloatProperty(
                tracker,
                "RegionHeightNormalized");

        debugRegionPixelAspectError =
            GetPublicFloatProperty(
                tracker,
                "RegionPixelAspectError");

        debugGammaPreservation =
            GetPublicBoolProperty(
                tracker,
                "InputGammaPreservationActive");

        debugRejectedPresence =
            GetPublicIntProperty(
                tracker,
                "RejectedPresenceFrameCount");

        debugRejectedInvalid =
            GetPublicIntProperty(
                tracker,
                "RejectedInvalidFrameCount");

        debugDiscardedStale =
            GetPublicIntProperty(
                tracker,
                "DiscardedStaleFrameCount");

        debugDiscardedCrossSystem =
            GetPublicIntProperty(
                tracker,
                "DiscardedCrossSystemFrameCount");

        debugRawPresenceLogit =
            GetPublicFloatProperty(
                tracker,
                "LatestRawPresenceLogit");

        debugPresenceProbability =
            GetPublicFloatProperty(
                tracker,
                "LatestPresence");

        debugInferenceReject =
            GetPublicStringProperty(
                tracker,
                "LatestRejectionReason");

        debugTrackerLatencyMs =
            GetPublicFloatProperty(
                tracker,
                "LatestLatencyMs");

        debugReadbackPending =
            GetPublicBoolProperty(
                tracker,
                "IsAsyncReadbackPending");

        debugHasRegion =
            GetPublicBoolProperty(
                tracker,
                "HasRegion");

        if (_visibilityRecovery != null)
        {
            debugLeftEyeAlpha =
                _visibilityRecovery.LeftEyeCanvasAlpha;

            debugRightEyeAlpha =
                _visibilityRecovery.RightEyeCanvasAlpha;

            debugMouthAlpha =
                _visibilityRecovery.MouthCanvasAlpha;

            debugPartMaskVisibility =
                new Vector3(
                    _visibilityRecovery.LeftEyeMaskVisibility,
                    _visibilityRecovery.RightEyeMaskVisibility,
                    _visibilityRecovery.MouthMaskVisibility);

            debugPartMaskPoints =
                new Vector3Int(
                    _visibilityRecovery.LeftEyeMaskPoints,
                    _visibilityRecovery.RightEyeMaskPoints,
                    _visibilityRecovery.MouthMaskPoints);

            debugAllPartsMissingRecovery =
                _visibilityRecovery.AllPartsMissingRecoveryActive;

            debugVisibilityRecoveries =
                _visibilityRecovery.RecoveryCount;
        }

        if (_presentationResolver != null)
        {
            debugLeftEyeAlpha =
                _presentationResolver.LeftEyeAlpha;
            debugRightEyeAlpha =
                _presentationResolver.RightEyeAlpha;
            debugMouthAlpha =
                _presentationResolver.MouthAlpha;

            debugPresentationLeftReasons =
                _presentationResolver.LeftEyeReasons;
            debugPresentationRightReasons =
                _presentationResolver.RightEyeReasons;
            debugPresentationMouthReasons =
                _presentationResolver.MouthReasons;
        }

        if (_recoveryDomains != null)
        {
            debugGlobalRecoveryState =
                _recoveryDomains.CurrentGlobalState.ToString();
            debugGlobalRecoverySequence =
                KiwiRecoveryDomainCoordinator.GlobalRecoverySequence;
            debugGlobalRecoveryReason =
                KiwiRecoveryDomainCoordinator.LastGlobalReason.ToString();
            debugGlobalRecoverySource =
                KiwiRecoveryDomainCoordinator.LastGlobalSource;

            debugSemanticRecoveryHealth =
                _recoveryDomains.CurrentSemanticHealth.ToString();
            debugSemanticRecoveryComponents =
                _recoveryDomains.ActiveSemanticComponents.ToString();
            debugSemanticRecoverySequence =
                KiwiRecoveryDomainCoordinator.SemanticRecoverySequence;
            debugSemanticRecoveryReason =
                KiwiRecoveryDomainCoordinator.LastSemanticReason.ToString();
            debugSemanticRecoverySource =
                KiwiRecoveryDomainCoordinator.LastSemanticSource;
            debugSemanticRecoveryRequests =
                _recoveryDomains.ActiveSemanticRequestCount;
        }

        debugValidationHealth =
            KiwiRuntimeValidationHarness.Health.ToString();
        debugValidationActive =
            KiwiRuntimeValidationHarness.ActiveViolationCount;
        debugValidationTotal =
            KiwiRuntimeValidationHarness.TotalViolationCount;
        debugValidationWarnings =
            KiwiRuntimeValidationHarness.WarningCount;
        debugValidationErrors =
            KiwiRuntimeValidationHarness.ErrorCount;
        debugValidationCriticals =
            KiwiRuntimeValidationHarness.CriticalCount;
        debugValidationLastCode =
            KiwiRuntimeValidationHarness.LastViolationCode;
        debugValidationLastFrame =
            KiwiRuntimeValidationHarness.LastViolationFrame;

        debugAcceptanceState =
            KiwiFaultInjectionAcceptanceHarness.State.ToString();
        debugAcceptanceScenario =
            KiwiFaultInjectionAcceptanceHarness.ActiveScenario.ToString();
        debugAcceptancePassed =
            KiwiFaultInjectionAcceptanceHarness.PassedCount;
        debugAcceptanceFailed =
            KiwiFaultInjectionAcceptanceHarness.FailedCount;
        debugAcceptanceLastScenario =
            KiwiFaultInjectionAcceptanceHarness.LastScenario;
        debugAcceptanceMatrix =
            KiwiFaultInjectionAcceptanceHarness.DeterministicMatrixStatus;

        if (_continuity != null)
        {
            debugContinuityState =
                _continuity.State.ToString();

            // v4.7: source age is the camera-observation age used by the
            // continuity state machine. Arrival age is retained separately so
            // GPU/ML completion jitter cannot masquerade as fresh tracking.
            debugContinuityAgeMs =
                float.IsInfinity(_continuity.SourceAgeSeconds)
                    ? 9999f
                    : _continuity.SourceAgeSeconds * 1000f;

            debugContinuityArrivalAgeMs =
                float.IsInfinity(_continuity.ArrivalAgeSeconds)
                    ? 9999f
                    : _continuity.ArrivalAgeSeconds * 1000f;

            debugContinuityCadenceHz =
                _continuity.CadenceHz;

            debugContinuityJitter =
                _continuity.CadenceJitterRatio;

            debugReacquireStreak =
                _continuity.ReacquireStreak;
        }

        debugRigidPredictionAllowance =
            KiwiCommercialRigidMotionPolicy.LastPredictionAllowance;

        if (_continuity != null)
        {
            // LastPredictionAllowance updates only when the prediction path is
            // evaluated. Clamp the displayed value by the live continuity
            // ceiling so telemetry never reports 1.00 while Degraded/Holding.
            debugRigidPredictionAllowance =
                Mathf.Min(
                    debugRigidPredictionAllowance,
                    _continuity.PredictionAllowance);
        }

        debugSemanticSourceAgeMs =
            KiwiCommercialFacePartPolicy.LastSemanticSourceAgeMs;

        debugSemanticStaleRejects =
            KiwiCommercialFacePartPolicy.StaleSemanticRejectCount;

        debugSemanticTransactionTimestamp =
            KiwiCommercialFacePartPolicy.PartDecisionTimestamp;

        debugSemanticPartAccepted =
            new Vector3Int(
                KiwiCommercialFacePartPolicy.LastLeftEyeAccepted ? 1 : 0,
                KiwiCommercialFacePartPolicy.LastRightEyeAccepted ? 1 : 0,
                KiwiCommercialFacePartPolicy.LastMouthAccepted ? 1 : 0);

        debugSemanticPartRejectCounts =
            new Vector3Int(
                KiwiCommercialFacePartPolicy.LeftEyeRejectCount,
                KiwiCommercialFacePartPolicy.RightEyeRejectCount,
                KiwiCommercialFacePartPolicy.MouthRejectCount);

        debugMaskReadinessComplete =
            _visibilityRecovery != null &&
            _visibilityRecovery.MaskReadinessComplete;

        debugCameraGeneration =
            KiwiRuntimeGenerationContext.CameraGeneration;

        debugCameraGenerationReason =
            KiwiCameraGeneration.LastReason.ToString();

        debugCameraGenerationEvents =
            KiwiCameraGeneration.EventCount;

        debugCameraIdentityChanges =
            KiwiCameraGeneration.IdentityChangeCount;

        debugCameraRestarts =
            KiwiCameraGeneration.RestartCount;

        debugCameraTimelineRegressions =
            KiwiCameraGeneration.TimelineRegressionCount;

        debugCameraStaleCallbackDrops =
            KiwiCameraGeneration.StaleCallbackDropCount;

        debugCameraUnmatchedCallbackDrops =
            KiwiCameraGeneration.UnmatchedCallbackDropCount;

        debugLiveMotionSourceIdentityInvalidations =
            _liveMotionBridge != null
                ? _liveMotionBridge.SourceIdentityInvalidations
                : 0;

        debugProviderGeneration =
            KiwiRuntimeGenerationContext.ProviderGeneration;

        debugModelGeneration =
            KiwiRuntimeGenerationContext.ModelGeneration;

        debugConfigEpoch =
            KiwiRuntimeGenerationContext.ConfigEpoch;

        debugCalibrationGeneration =
            KiwiRuntimeGenerationContext.CalibrationGeneration;

        debugCalibrationReason =
            KiwiCalibrationGeneration.LastReason;

        debugCalibrationScope =
            KiwiCalibrationGeneration.LastScope.ToString();

        debugCalibrationEvents =
            KiwiCalibrationGeneration.EventCount;

        debugCalibrationCommits =
            KiwiCalibrationGeneration.CommitCount;

        debugCalibrationRejectedCommits =
            KiwiCalibrationGeneration.RejectedCommitCount;

        debugCalibrationLastCommitOwner =
            KiwiCalibrationGeneration.LastCommitOwner;

        debugCalibrationLastCommitGeneration =
            KiwiCalibrationGeneration.LastCommitGeneration;

        debugTrackingSessionGeneration =
            KiwiRuntimeGenerationContext.TrackingSessionGeneration;

        debugObservationSequence =
            KiwiRuntimeGenerationContext.ObservationSequence;

        debugSemanticTransactionSequence =
            KiwiRuntimeGenerationContext.SemanticTransactionSequence;

        debugLiveMotionGenerationDrops =
            _liveMotionBridge != null
                ? _liveMotionBridge.GenerationReadbackDrops
                : 0;

        debugLiveMotionCalibrationGenerationDrops =
            _liveMotionBridge != null
                ? _liveMotionBridge.CalibrationGenerationReadbackDrops
                : 0;

        debugLiveMotionSuppressionInvalidations =
            _liveMotionBridge != null
                ? _liveMotionBridge.RuntimeSuppressionInvalidations
                : 0;

        debugPolicyVersion =
            KiwiRuntimePolicyResolver.PolicyVersion;

        debugPolicySource =
            KiwiRuntimePolicyResolver.LastPolicySource;

        debugPolicyMediaPipeRefreshHz =
            KiwiRuntimePolicyResolver.ResolvedMediaPipeRefreshHz;

        debugPolicyPresenceThreshold =
            KiwiRuntimePolicyResolver.ResolvedPresenceThreshold;

        debugPolicyTrackingInputWidth =
            KiwiRuntimePolicyResolver.ResolvedTrackingInputWidth;

        debugPolicyAuxiliaryCadenceScale =
            KiwiRuntimePolicyResolver.ResolvedAuxiliaryCadenceScale;

        debugRigidHolding =
            KiwiCommercialRigidMotionPolicy.LastHoldActive;

        debugRigidLost =
            KiwiCommercialRigidMotionPolicy.LastLostActive;

        debugRigidPositionDeadZoneMultiplier =
            KiwiCommercialRigidMotionPolicy.LastPositionDeadZoneMultiplier;

        debugRigidEffectivePositionDeadZone =
            KiwiCommercialRigidMotionPolicy.LastEffectivePositionDeadZone;

        if (_supervisor != null)
        {
            debugPolicyQuality =
                _supervisor.CurrentPolicyQuality;

            debugAuxiliaryMediaPipeHz =
                _supervisor.CurrentAuxiliaryMediaPipeHz;
        }

        if (_faceChannels != null)
        {
            debugGeometryChannel =
                _faceChannels.GeometryState +
                "@" +
                (
                    string.IsNullOrEmpty(
                        _faceChannels.GeometryProviderId)
                        ? "-"
                        : _faceChannels.GeometryProviderId
                );

            debugExpressionChannel =
                _faceChannels.ExpressionState +
                "@" +
                (
                    string.IsNullOrEmpty(
                        _faceChannels.ExpressionProviderId)
                        ? "-"
                        : _faceChannels.ExpressionProviderId
                );

            debugEyeSource =
                _faceChannels.RecommendedEyeSource;
        }

        if (_attachmentRecalibration != null)
        {
            debugAttachmentRecalibrationPending =
                _attachmentRecalibration.IsPending;

            debugAttachmentRecalibrationCount =
                _attachmentRecalibration.RecalibrationCount;

            debugAttachmentPendingCalibrationGeneration =
                _attachmentRecalibration.PendingCalibrationGeneration;
        }

        if (_dualDomain != null)
        {
            debugLeftEye2dQuality =
                _dualDomain.LeftEyeQuality;

            debugRightEye2dQuality =
                _dualDomain.RightEyeQuality;

            debugMouth2dQuality =
                _dualDomain.MouthQuality;

            debugDualDomainQuality =
                _dualDomain.DualDomainQuality;
        }

        if (_actorCalibration != null)
        {
            debugActorCalibrated =
                _actorCalibration.IsCalibrated;

            debugNeutralEyeOpen =
                _actorCalibration.NeutralEyeOpenRatio;

            debugActorCalibrationSamples =
                _actorCalibration.CollectedSamples;

            debugActorProfileCalibrationGeneration =
                _actorCalibration.ProfileCalibrationGeneration;
        }

        if (_latencyBudget != null)
        {
            debugLatencyProfile =
                _latencyBudget.ResolvedProfile.ToString();

            debugPredictionBudgetMs =
                _latencyBudget.PredictionBudgetSeconds *
                1000f;
        }

        if (_modelPrimaryConstraint != null)
        {
            debugConstraintCalibrated =
                _modelPrimaryConstraint.IsCalibrated;

            debugConstraintCalibrationSamples =
                _modelPrimaryConstraint.CalibrationSamples;

            debugConstraintProfileCalibrationGeneration =
                _modelPrimaryConstraint.ProfileCalibrationGeneration;

            debugConstraintCalibrationQuality =
                _modelPrimaryConstraint.CalibrationQuality;

            debugSurfaceConstraintStrength =
                _modelPrimaryConstraint.ConstraintStrength;

            debugSurfaceLeftEyeOffset =
                _modelPrimaryConstraint.LeftEyeOffset;

            debugSurfaceRightEyeOffset =
                _modelPrimaryConstraint.RightEyeOffset;

            debugSurfaceMouthOffset =
                _modelPrimaryConstraint.MouthOffset;

            debugSurfaceApiAvailable =
                _modelPrimaryConstraint.SurfaceApiAvailable;

            debugSurfaceConstraintState =
                _modelPrimaryConstraint.ConstraintState;
        }

        if (_liveMotionBridge != null)
        {
            debugLiveMotionOperational =
                _liveMotionBridge.IsOperational;

            debugLiveMotionRateHz =
                _liveMotionBridge.MatchRateHz;

            debugLiveMotionReadbackMs =
                _liveMotionBridge.ReadbackLatencyMs;

            debugLiveMotionPending =
                _liveMotionBridge.PendingReadbacks;

            debugLiveMotionDropped =
                _liveMotionBridge.DroppedMatchFrames;

            debugLiveMotionStaleDropped =
                _liveMotionBridge.StaleReadbackDrops;

            debugLiveMotionOverloadSuspended =
                _liveMotionBridge.IsOverloadSuspended;

            debugLiveMotionOverloadSuspensions =
                _liveMotionBridge.OverloadSuspensions;

            debugLiveMotionSearchRadius =
                _liveMotionBridge.ActiveSearchRadiusPixels;

            debugLiveMotionConfidence =
                new Vector3(
                    _liveMotionBridge.LeftConfidence,
                    _liveMotionBridge.RightConfidence,
                    _liveMotionBridge.MouthConfidence);

            debugLiveLeftCorrection =
                _liveMotionBridge.LeftCorrection;

            debugLiveRightCorrection =
                _liveMotionBridge.RightCorrection;

            debugLiveMouthCorrection =
                _liveMotionBridge.MouthCorrection;

            debugLiveExpectedRigidPixels =
                _liveMotionBridge.ExpectedRigidPixelDelta;

            debugLiveRejectedCommonPixels =
                _liveMotionBridge.RejectedCommonModePixels;

            debugLiveLocalResidualPixels =
                _liveMotionBridge.LocalResidualPixelMagnitude;

            debugLiveCanonicalAligned =
                _liveMotionBridge.CanonicalSemanticAligned;
        }

        if (_adaptiveContainment != null)
        {
            debugContainmentRisk =
                _adaptiveContainment.MotionRisk;

            debugContainmentAgeMs =
                _adaptiveContainment.SourceAgeSeconds *
                1000f;

            debugContainmentEyeScale =
                _adaptiveContainment.AppliedEyeWidthScale;

            debugContainmentMouthScale =
                _adaptiveContainment.AppliedMouthWidthScale;

            debugContainmentPredictionDistance =
                _adaptiveContainment.AppliedPredictionDistance;
        }

        if (_liveMotionBridge != null)
        {
            debugEyePairRollDegrees =
                _liveMotionBridge.EyePairRollDegrees;

            debugEyePairRotationDeltaDegrees =
                _liveMotionBridge.EyePairRotationDeltaDegrees;

            debugEyePairScale =
                _liveMotionBridge.EyePairScale;

            debugEyePairFallback =
                _liveMotionBridge.EyePairFallbackUsed;

            debugMouthAnatomyClamped =
                _liveMotionBridge.MouthAnatomyClamped;

            debugMouthSeparationRatio =
                _liveMotionBridge.MouthSeparationRatio;
        }

        if (_anatomyGuard != null)
        {
            debugAnatomyCollisionSeverity =
                _anatomyGuard.CollisionSeverity;

            debugAnatomyOverlapRatio =
                _anatomyGuard.OverlapRatio;

            debugAnatomySurfaceAsymmetry =
                _anatomyGuard.SurfaceAsymmetrySeverity;

            debugAnatomyOutlierEye =
                _anatomyGuard.SurfaceOutlierEye;

            debugAnatomyMouthScale =
                _anatomyGuard.MouthScaleFactor;
        }
        if (_commercialProfile != null)
        {
            debugCommercialProfile =
                _commercialProfile.ActiveProfileName +
                "/" +
                _commercialProfile.CurrentStyleName;
        }

        if (_commercialQuality != null)
        {
            debugCommercialQualityTier =
                _commercialQuality.CurrentTierName;

            debugCommercialRenderFps =
                _commercialQuality.RenderFps;
        }

        if (_pathfinder != null)
        {
            debugPathfinderState =
                _pathfinder.StateName;

            debugPathfinderScore =
                _pathfinder.HealthScore;

            debugPathfinderRecommendation =
                _pathfinder.Recommendation;
        }
    }


    private void BuildPanelText()
    {
        StringBuilder b =
            _panelBuilder;

        b.Clear();

        b.Append("Kiwi v5.1 Phase 11 Runtime Validation Telemetry\n");
        b.Append("provider: ").Append(debugProvider).Append('\n');
        b.Append("rigid anchor jaw-neutral corr=")
            .Append(debugRigidAnchorCorrection.ToString("F4"))
            .Append('\n');
        b.Append("handoff active=")
            .Append(debugProviderHandoffActive)
            .Append(" to=").Append(debugProviderHandoffTarget)
            .Append(" w=").Append(debugProviderHandoffWeight.ToString("F2"))
            .Append(" d=").Append(debugProviderHandoffCenterOffset.ToString("F4"))
            .Append(" r=").Append(debugProviderHandoffRotationOffset.ToString("F1"))
            .Append(" s=").Append(debugProviderHandoffScaleRatio.ToString("F3"))
            .Append(" #").Append(debugProviderHandoffCount)
            .Append('\n');
        b.Append("provider live arrival=")
            .Append(debugProviderArrivalAgeMs.ToString("F0"))
            .Append("/")
            .Append(debugProviderArrivalLimitMs.ToString("F0"))
            .Append(" ms\n");
        b.Append("normalization time=")
            .Append(debugNormalizationTimebase)
            .Append(" quality=")
            .Append(debugNormalizationTimestampQuality)
            .Append(" srcX=")
            .Append(debugNormalizationSourceHorizontal)
            .Append(" targetMirror=")
            .Append(debugNormalizationCanonicalMirrored)
            .Append(" xform=")
            .Append(debugNormalizationHorizontalTransform)
            .Append(" reset/fallback/xform#=")
            .Append(debugNormalizationTimebaseResets).Append('/')
            .Append(debugNormalizationArrivalFallbacks).Append('/')
            .Append(debugNormalizationHorizontalTransforms)
            .Append('\n');
        b.Append("resumeRef=").Append(debugResumeReferenceValid)
            .Append(" gap=").Append(debugResumeGapMs.ToString("F0")).Append("ms")
            .Append(" resume#=").Append(debugResumeHandoffCount)
            .Append('\n');
        b.Append("partFrame op=")
            .Append(KiwiFacePartRigidSampleFrame.IsOperational)
            .Append(" eyeLine=")
            .Append(KiwiFacePartRigidSampleFrame.EyeLineAngleDegrees.ToString("F1"))
            .Append(" localRot=")
            .Append(KiwiFacePartRigidSampleFrame.AppliedRotationDegrees.ToString("F1"))
            .Append(" reject#=")
            .Append(KiwiFacePartRigidSampleFrame.RejectedAngleJumpCount)
            .Append('\n');
        b.Append("rigid core pure=")
            .Append(_faceMotion != null && _faceMotion.ultraDisableSecondaryBodyMotion)
            .Append(" staticLock=")
            .Append(_faceMotion != null && _faceMotion.ultraStaticPoseLock)
            .Append(" hub=")
            .Append(KiwiTrackingProviderHub.HasRuntimeInstance)
            .Append('\n');
        b.Append("continuity: ").Append(debugContinuityState)
            .Append(" sourceAge=").Append(debugContinuityAgeMs.ToString("F0"))
            .Append(" ms arrivalAge=").Append(debugContinuityArrivalAgeMs.ToString("F0"))
            .Append(" ms cadence=").Append(debugContinuityCadenceHz.ToString("F1"))
            .Append(" Hz jitter=").Append(debugContinuityJitter.ToString("F2"))
            .Append(" reacq=").Append(debugReacquireStreak).Append('\n');
        b.Append("frame continuity suppress=")
            .Append(KiwiFrameContinuityDiagnostics.DirectBypassSuppressed)
            .Append(" gap=")
            .Append(KiwiFrameContinuityDiagnostics.DiscontinuityGuardActive)
            .Append(" track=")
            .Append(KiwiFrameContinuityDiagnostics.TrackingRateHz.ToString("F1"))
            .Append(" Hz interval=")
            .Append((KiwiFrameContinuityDiagnostics.EffectiveSampleInterval * 1000f).ToString("F0"))
            .Append(" ms cap=")
            .Append(KiwiFrameContinuityDiagnostics.ResponseCap.ToString("F1"))
            .Append('\n');
        b.Append("rigid policy hold=").Append(debugRigidHolding)
            .Append(" lost=").Append(debugRigidLost)
            .Append(" predAllow=").Append(debugRigidPredictionAllowance.ToString("F2"))
            .Append(" posDZx=").Append(debugRigidPositionDeadZoneMultiplier.ToString("F2"))
            .Append(" effDZ=").Append(debugRigidEffectivePositionDeadZone.ToString("F5"))
            .Append('\n');
        b.Append("policyQ=").Append(debugPolicyQuality.ToString("F2"))
            .Append(" auxMP=").Append(debugAuxiliaryMediaPipeHz.ToString("F1")).Append(" Hz\n");
        b.Append("commercial=").Append(debugCommercialProfile)
            .Append(" quality=").Append(debugCommercialQualityTier)
            .Append(" render=").Append(debugCommercialRenderFps.ToString("F0"))
            .Append(" fps path=").Append(debugPathfinderState)
            .Append(" score=").Append(debugPathfinderScore.ToString("F2")).Append('\n');
        b.Append("channels geom=").Append(debugGeometryChannel).Append('\n');
        b.Append("expr=").Append(debugExpressionChannel).Append(" eye=").Append(debugEyeSource).Append('\n');
        b.Append("recovery global=")
            .Append(debugGlobalRecoveryState)
            .Append(" #").Append(debugGlobalRecoverySequence)
            .Append(" reason/source=")
            .Append(debugGlobalRecoveryReason).Append('/')
            .Append(debugGlobalRecoverySource).Append('\n');
        b.Append("recovery semantic=")
            .Append(debugSemanticRecoveryHealth)
            .Append(" active=").Append(debugSemanticRecoveryComponents)
            .Append(" #").Append(debugSemanticRecoverySequence)
            .Append(" req=").Append(debugSemanticRecoveryRequests)
            .Append(" reason/source=")
            .Append(debugSemanticRecoveryReason).Append('/')
            .Append(debugSemanticRecoverySource).Append('\n');
        b.Append("validation=")
            .Append(debugValidationHealth)
            .Append(" active/total=")
            .Append(debugValidationActive).Append('/')
            .Append(debugValidationTotal)
            .Append(" w/e/c=")
            .Append(debugValidationWarnings).Append('/')
            .Append(debugValidationErrors).Append('/')
            .Append(debugValidationCriticals)
            .Append(" last=")
            .Append(debugValidationLastCode).Append('@')
            .Append(debugValidationLastFrame)
            .Append('\n');
        b.Append("acceptance=")
            .Append(debugAcceptanceState)
            .Append(" scenario=")
            .Append(debugAcceptanceScenario)
            .Append(" pass/fail=")
            .Append(debugAcceptancePassed).Append('/')
            .Append(debugAcceptanceFailed)
            .Append(" last=")
            .Append(debugAcceptanceLastScenario)
            .Append(" matrix=")
            .Append(debugAcceptanceMatrix)
            .Append('\n');
        b.Append("attach recal pending/count/gen=").Append(debugAttachmentRecalibrationPending)
            .Append(" / ").Append(debugAttachmentRecalibrationCount)
            .Append(" / ").Append(debugAttachmentPendingCalibrationGeneration).Append('\n');
        b.Append("2D q L/R/M=").Append(debugLeftEye2dQuality.ToString("F2"))
            .Append(" / ").Append(debugRightEye2dQuality.ToString("F2"))
            .Append(" / ").Append(debugMouth2dQuality.ToString("F2"))
            .Append(" dual=").Append(debugDualDomainQuality.ToString("F2")).Append('\n');
        b.Append("semantic age=").Append(debugSemanticSourceAgeMs.ToString("F0"))
            .Append("ms staleReject=").Append(debugSemanticStaleRejects)
            .Append(" maskReady=").Append(debugMaskReadinessComplete).Append('\n');
        b.Append("camera reason/events/id/restart/timeline/stale/unmatched/liveLocal=")
            .Append(debugCameraGenerationReason).Append('/')
            .Append(debugCameraGenerationEvents).Append('/')
            .Append(debugCameraIdentityChanges).Append('/')
            .Append(debugCameraRestarts).Append('/')
            .Append(debugCameraTimelineRegressions).Append('/')
            .Append(debugCameraStaleCallbackDrops).Append('/')
            .Append(debugCameraUnmatchedCallbackDrops).Append('/')
            .Append(debugLiveMotionSourceIdentityInvalidations)
            .Append('\n');
        b.Append("generation cam/prov/model/config/cal/session=")
            .Append(debugCameraGeneration).Append('/')
            .Append(debugProviderGeneration).Append('/')
            .Append(debugModelGeneration).Append('/')
            .Append(debugConfigEpoch).Append('/')
            .Append(debugCalibrationGeneration).Append('/')
            .Append(debugTrackingSessionGeneration)
            .Append(" obs=").Append(debugObservationSequence)
            .Append(" semantic=").Append(debugSemanticTransactionSequence)
            .Append(" liveDrop=").Append(debugLiveMotionGenerationDrops)
            .Append(" liveCalDrop=").Append(debugLiveMotionCalibrationGenerationDrops)
            .Append(" liveSuppress=").Append(debugLiveMotionSuppressionInvalidations)
            .Append('\n');
        b.Append("calibration reason/scope=")
            .Append(debugCalibrationReason).Append('/')
            .Append(debugCalibrationScope)
            .Append(" events/commit/reject=")
            .Append(debugCalibrationEvents).Append('/')
            .Append(debugCalibrationCommits).Append('/')
            .Append(debugCalibrationRejectedCommits)
            .Append(" lastCommit=")
            .Append(debugCalibrationLastCommitOwner).Append('@')
            .Append(debugCalibrationLastCommitGeneration)
            .Append('\n');
        b.Append("canonical id=")
            .Append(KiwiCanonicalTrackingFrame.CanonicalFrameId)
            .Append(" provider=")
            .Append(KiwiCanonicalTrackingFrame.ProviderId)
            .Append(" rigid/semantic=")
            .Append(KiwiCanonicalTrackingFrame.RigidTimestamp)
            .Append('/')
            .Append(KiwiCanonicalTrackingFrame.SemanticTimestamp)
            .Append(" match=")
            .Append(KiwiCanonicalTrackingFrame.SemanticMatched)
            .Append(" timing=")
            .Append(KiwiCanonicalTrackingFrame.TimestampQuality)
            .Append(" mirror=")
            .Append(KiwiCanonicalTrackingFrame.CanonicalInputHorizontallyMirrored)
            .Append(" latch/refresh/mismatch/hold=")
            .Append(KiwiCanonicalTrackingFrame.RigidLatchCount).Append('/')
            .Append(KiwiCanonicalTrackingFrame.LateRefreshCount).Append('/')
            .Append(KiwiCanonicalTrackingFrame.SemanticMismatchCount).Append('/')
            .Append(KiwiCanonicalTrackingFrame.SemanticHoldCount)
            .Append('\n');
        b.Append("policy v/source=").Append(debugPolicyVersion)
            .Append('/').Append(debugPolicySource)
            .Append(" input=").Append(debugPolicyTrackingInputWidth)
            .Append(" auxHz=").Append(debugPolicyMediaPipeRefreshHz.ToString("F1"))
            .Append(" presence=").Append(debugPolicyPresenceThreshold.ToString("F3"))
            .Append(" cadenceScale=").Append(debugPolicyAuxiliaryCadenceScale.ToString("F2"))
            .Append('\n');
        b.Append("semantic txn ts=").Append(debugSemanticTransactionTimestamp)
            .Append(" accept L/R/M=")
            .Append(debugSemanticPartAccepted.x).Append('/')
            .Append(debugSemanticPartAccepted.y).Append('/')
            .Append(debugSemanticPartAccepted.z)
            .Append(" reject#=")
            .Append(debugSemanticPartRejectCounts.x).Append('/')
            .Append(debugSemanticPartRejectCounts.y).Append('/')
            .Append(debugSemanticPartRejectCounts.z).Append('\n');
        b.Append("actorCal=").Append(debugActorCalibrated)
            .Append(" samples=").Append(debugActorCalibrationSamples)
            .Append(" profileGen=").Append(debugActorProfileCalibrationGeneration)
            .Append(" neutralEye=").Append(debugNeutralEyeOpen.ToString("F3")).Append('\n');
        b.Append("latency=").Append(debugLatencyProfile)
            .Append(" predBudget=").Append(debugPredictionBudgetMs.ToString("F1")).Append(" ms\n");
        b.Append("constraintCal=").Append(debugConstraintCalibrated)
            .Append(" samples=").Append(debugConstraintCalibrationSamples)
            .Append(" profileGen=").Append(debugConstraintProfileCalibrationGeneration)
            .Append(" q=").Append(debugConstraintCalibrationQuality.ToString("F2"))
            .Append(" api=").Append(debugSurfaceApiAvailable)
            .Append(" state=").Append(debugSurfaceConstraintState).Append('\n');
        b.Append("surfaceConstraint=").Append(debugSurfaceConstraintStrength.ToString("F2"))
            .Append(" L=").Append(debugSurfaceLeftEyeOffset.ToString("F3"))
            .Append(" R=").Append(debugSurfaceRightEyeOffset.ToString("F3"))
            .Append(" M=").Append(debugSurfaceMouthOffset.ToString("F3")).Append('\n');
        b.Append("live2D=").Append(debugLiveMotionOperational)
            .Append(" rate=").Append(debugLiveMotionRateHz.ToString("F1"))
            .Append("Hz rb=").Append(debugLiveMotionReadbackMs.ToString("F1"))
            .Append("ms r=").Append(debugLiveMotionSearchRadius)
            .Append(" p/d/s=").Append(debugLiveMotionPending).Append('/').Append(debugLiveMotionDropped)
            .Append('/').Append(debugLiveMotionStaleDropped)
            .Append(" suspend=").Append(debugLiveMotionOverloadSuspended)
            .Append("#").Append(debugLiveMotionOverloadSuspensions).Append('\n');
        b.Append("live2D conf L/R/M=").Append(debugLiveMotionConfidence.x.ToString("F2"))
            .Append('/').Append(debugLiveMotionConfidence.y.ToString("F2"))
            .Append('/').Append(debugLiveMotionConfidence.z.ToString("F2"))
            .Append(" corr L=").Append(debugLiveLeftCorrection.ToString("F3"))
            .Append(" R=").Append(debugLiveRightCorrection.ToString("F3"))
            .Append(" M=").Append(debugLiveMouthCorrection.ToString("F3")).Append('\n');
        b.Append("live2D rigid/local px expected=")
            .Append(debugLiveExpectedRigidPixels.ToString("F1"))
            .Append(" commonRejected=")
            .Append(debugLiveRejectedCommonPixels.ToString("F1"))
            .Append(" local L/R/M=")
            .Append(debugLiveLocalResidualPixels.x.ToString("F1"))
            .Append('/').Append(debugLiveLocalResidualPixels.y.ToString("F1"))
            .Append('/').Append(debugLiveLocalResidualPixels.z.ToString("F1"))
            .Append(" canonical=").Append(debugLiveCanonicalAligned)
            .Append('\n');
        b.Append("contain risk=").Append(debugContainmentRisk.ToString("F2"))
            .Append(" age=").Append(debugContainmentAgeMs.ToString("F0"))
            .Append("ms eye/mouth=").Append(debugContainmentEyeScale.ToString("F2"))
            .Append('/').Append(debugContainmentMouthScale.ToString("F2"))
            .Append(" pred=").Append(debugContainmentPredictionDistance.ToString("F3")).Append('\n');
        b.Append("eyePair roll=").Append(debugEyePairRollDegrees.ToString("F1"))
            .Append(" dRot=").Append(debugEyePairRotationDeltaDegrees.ToString("F1"))
            .Append(" scale=").Append(debugEyePairScale.ToString("F3"))
            .Append(" fallback=").Append(debugEyePairFallback).Append('\n');
        b.Append("mouthTopo clamp=").Append(debugMouthAnatomyClamped)
            .Append(" sep=").Append(debugMouthSeparationRatio.ToString("F2"))
            .Append(" finalCollision=").Append(debugAnatomyCollisionSeverity.ToString("F2"))
            .Append(" overlap=").Append(debugAnatomyOverlapRatio.ToString("F2")).Append('\n');
        b.Append("surfaceEye asym=").Append(debugAnatomySurfaceAsymmetry.ToString("F2"))
            .Append(" outlier=").Append(debugAnatomyOutlierEye)
            .Append(" mouthScale=").Append(debugAnatomyMouthScale.ToString("F2")).Append('\n');
        b.Append("source/result: ").Append(debugFreshSourceHz.ToString("F1"))
            .Append(" / ").Append(debugResultHz.ToString("F1")).Append(" Hz\n");
        b.Append("source age: ").Append(debugSourceAgeMs.ToString("F1"))
            .Append(" ms geom Q=").Append(debugGeometryQuality.ToString("F2")).Append('\n');
        b.Append("MediaPipe readback: ").Append(debugReadbackMs.ToString("F1")).Append(" ms\n");
        b.Append("Inference sched/read/accept/drop: ").Append(debugScheduled)
            .Append(" / ").Append(debugReadbackCompleted)
            .Append(" / ").Append(debugCompletedAccepted)
            .Append(" / ").Append(debugDroppedFresh).Append('\n');
        b.Append("lanes active/limit/depth=").Append(debugActiveLanes)
            .Append(" / ").Append(debugSchedulingLaneLimit)
            .Append(" / ").Append(debugPipelineDepth)
            .Append(" oldest=").Append(debugOldestPendingMs.ToString("F1")).Append(" ms\n");
        b.Append("ROI w/h=").Append(debugRegionWidth.ToString("F3")).Append(" / ")
            .Append(debugRegionHeight.ToString("F3")).Append(" pixel-square err=")
            .Append(debugRegionPixelAspectError.ToString("F3")).Append('\n');
        b.Append("input gamma-preserve=").Append(debugGammaPreservation).Append(" border=replicate\n");
        b.Append("face logit=").Append(debugRawPresenceLogit.ToString("F3"))
            .Append(" p=").Append(debugPresenceProbability.ToString("F3"))
            .Append(" threshold=").Append(debugLivePresenceThreshold.ToString("F3")).Append('\n');
        b.Append("Inference latency=").Append(debugTrackerLatencyMs.ToString("F1"))
            .Append(" ms reject=").Append(debugInferenceReject).Append('\n');
        b.Append("reject p/invalid/stale/xsys=").Append(debugRejectedPresence)
            .Append(" / ").Append(debugRejectedInvalid)
            .Append(" / ").Append(debugDiscardedStale)
            .Append(" / ").Append(debugDiscardedCrossSystem).Append('\n');
        b.Append("ROI=").Append(debugHasRegion).Append(" pending=").Append(debugReadbackPending).Append('\n');
        b.Append("parts alpha L/R/M=").Append(debugLeftEyeAlpha.ToString("F2"))
            .Append(" / ").Append(debugRightEyeAlpha.ToString("F2"))
            .Append(" / ").Append(debugMouthAlpha.ToString("F2"))
            .Append(" recoveries=").Append(debugVisibilityRecoveries).Append('\n');
        b.Append("parts reason L/R/M=")
            .Append(debugPresentationLeftReasons).Append(" / ")
            .Append(debugPresentationRightReasons).Append(" / ")
            .Append(debugPresentationMouthReasons).Append('\n');
        b.Append("parts mask vis=").Append(debugPartMaskVisibility.x.ToString("F2"))
            .Append('/').Append(debugPartMaskVisibility.y.ToString("F2"))
            .Append('/').Append(debugPartMaskVisibility.z.ToString("F2"))
            .Append(" pts=").Append(debugPartMaskPoints.x).Append('/')
            .Append(debugPartMaskPoints.y).Append('/').Append(debugPartMaskPoints.z)
            .Append(" failopen=").Append(debugAllPartsMissingRecovery);

        _cachedPanelText =
            b.ToString();
    }

    private void OnGUI()
    {
        if (!showOverlay)
        {
            return;
        }

        if (_style == null)
        {
            _style =
                new GUIStyle(GUI.skin.box)
                {
                    alignment =
                        TextAnchor.UpperLeft,
                    fontSize =
                        14,
                    wordWrap =
                        true,
                    padding =
                        new RectOffset(
                            10,
                            10,
                            8,
                            8)
                };
        }

        string text =
            _cachedPanelText;

        GUI.Box(
            CalculateOverlayRect(),
            text,
            _style);
    }

    private static float CalculateSourceAgeSeconds(
        FacePrecisionTrackingData data)
    {
        long ticks =
            data.submissionHostTicks > 0L
                ? data.submissionHostTicks
                : data.arrivalHostTicks;

        if (ticks <= 0L)
        {
            return 0f;
        }

        long now =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        long delta =
            now - ticks;

        if (delta <= 0L)
        {
            return 0f;
        }

        return
            (float)(
                delta /
                (double)
                System.Diagnostics.Stopwatch
                    .Frequency);
    }

    private static object GetPrivateField(
        object target,
        string fieldName)
    {
        if (target == null)
        {
            return null;
        }

        FieldInfo field =
            target.GetType()
                .GetField(
                    fieldName,
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

        return
            field != null
                ? field.GetValue(target)
                : null;
    }

    private static object GetPublicProperty(
        object target,
        string propertyName)
    {
        if (target == null)
        {
            return null;
        }

        PropertyInfo property =
            target.GetType()
                .GetProperty(
                    propertyName,
                    BindingFlags.Instance |
                    BindingFlags.Public);

        return
            property != null
                ? property.GetValue(target)
                : null;
    }

    private static int GetPublicIntProperty(
        object target,
        string propertyName)
    {
        object value =
            GetPublicProperty(
                target,
                propertyName);

        return
            value is int integer
                ? integer
                : 0;
    }

    private static float GetPublicFloatProperty(
        object target,
        string propertyName)
    {
        object value =
            GetPublicProperty(
                target,
                propertyName);

        return
            value is float number
                ? number
                : 0f;
    }

    private static string GetPublicStringProperty(
        object target,
        string propertyName)
    {
        object value =
            GetPublicProperty(
                target,
                propertyName);

        return
            value as string ??
            "-";
    }

    private static bool GetPublicBoolProperty(
        object target,
        string propertyName)
    {
        object value =
            GetPublicProperty(
                target,
                propertyName);

        return
            value is bool boolean &&
            boolean;
    }
}
