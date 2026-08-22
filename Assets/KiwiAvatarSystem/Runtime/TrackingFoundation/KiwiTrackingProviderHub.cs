using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// KiwiAvatarSystem v3.8 provider-neutral, capability-scoped tracking arbiter.
///
/// The previous v3.0 hub wrapped the current Runner snapshot, but a Runner
/// backend switch made the previous backend disappear immediately. That meant
/// the configured provider switch confirmation could not actually compare two
/// independent Runner backends for a short overlap window.
///
/// v3.1 keeps a short, freshness-bounded cache for MediaPipe and Inference
/// snapshots separately. A provider switch therefore requires genuinely newer
/// candidate frames while stale/lost providers still fail over immediately.
///
/// v4.6 adds a commercial canonical rigid-solve boundary:
/// - built-in root translation is derived from jaw-neutral upper-face geometry,
///   so mouth/jaw deformation cannot pull the rigid avatar root;
/// - provider changes are normalized into one canonical pose space at the
///   handoff boundary, preventing an Inference/MediaPipe coordinate-bias pop;
/// - the handoff correction releases only with observed intentional motion,
///   adding no permanent frame buffer and no extra low-pass stage.
///
///
/// v5.0 separates stream liveness (arrival cadence) from end-to-end source
/// latency. This prevents repeated Inference/MediaPipe handoffs on a live but
/// latency-heavy stream while retaining a hard absolute stale ceiling.
/// External providers keep the same public API.
/// </summary>
[DefaultExecutionOrder(-28000)]
[DisallowMultipleComponent]
public sealed class KiwiTrackingProviderHub : MonoBehaviour
{
    [Flags]
    public enum TrackingCapability
    {
        None = 0,
        HeadPose = 1 << 0,
        FaceGeometry = 1 << 1,
        Expressions = 1 << 2,
        BodyPose = 1 << 3,
        Hands = 1 << 4
    }

    public struct CapabilityHealth
    {
        public bool valid;
        public TrackingCapability requiredCapabilities;
        public string providerId;
        public ulong sourceFrameId;
        public float ageSeconds;
        public float score;
        public float cadenceQuality;
        public float geometryQuality;
        public KiwiTrackingBackend backend;
        public KiwiTrackingTimestampQuality timestampQuality;
        public KiwiTrackingTimebase sourceTimebase;
        public bool canonicalInputHorizontallyMirrored;
    }

    private sealed class ProviderSlot
    {
        public string id;
        public int priority;
        public TrackingCapability capabilities;
        public FacePrecisionTrackingData data;
        public KiwiTrackingNormalizedFrameMetadata metadata;
        public ulong sourceFrameId;
        public ulong syntheticFrameId;
        public ulong lastProviderSourceFrameId;
        public ulong normalizedExternalFrameId;
        public bool hasExternalFrameIdentity;
        public long arrivalHostTicks;
        public double submittedRealtime;
        public float frameIntervalEma;
        public float frameIntervalDeviationEma;
        public float rigidAnchorCorrectionMagnitude;

        // Phase 9: provider-native monotonic clocks are mapped once at the
        // adapter boundary. Canonical consumers only see local Stopwatch ticks.
        public bool hasTimebaseAnchor;
        public KiwiTrackingTimebase mappedTimebase;
        public long timebaseSourceAnchor;
        public long timebaseHostAnchor;
        public long lastProviderSourceTimestamp;
        public long lastNormalizedObservationHostTicks;
        public long lastCanonicalTimestampMilliseconds;
        public int timebaseResetCount;
        public bool hasFrame;
    }

    private struct Candidate
    {
        public bool valid;
        public ProviderSlot slot;
        public float ageSeconds;
        public float score;
        public float cadenceQuality;
    }

    private const string RuntimeObjectName =
        "[Kiwi] Tracking Provider Hub";

    // v4.5.5: Rigid pose consumers must read the provider selected by this hub,
    // not the Runner's most recently published backend directly. This makes
    // the existing hold / score-margin / confirmation policy effective for
    // the actual avatar root.
    private static KiwiTrackingProviderHub _instance;

    public static bool HasRuntimeInstance =>
        _instance != null;

    public static bool TryGetCurrentRigidFrame(
        out FacePrecisionTrackingData data)
    {
        data = default;

        if (_instance == null)
        {
            return false;
        }

        return
            _instance.TryGetLatestFrame(
                out data,
                out _);
    }

    private const string MediaPipeProviderId =
        "Runner/MediaPipe";

    private const string InferenceProviderId =
        "Runner/InferenceEngine";

    [Header("Built-in Runner providers")]
    public bool useFaceLandmarkerRunner = true;

    [Range(0, 200)]
    public int mediaPipePriority = 82;

    [Range(0, 200)]
    public int inferenceEnginePriority = 110;

    [Header("Provider arbitration")]
    [Tooltip("Hard stale ceiling measured from the source/submission timestamp when available. This prevents a newly-arrived but already-old ML result from becoming the rigid-pose authority.")]
    [Range(0.05f, 1f)]
    public float maximumProviderFrameAge = 0.45f;

    [Header("Stream liveness vs pipeline latency")]
    [Tooltip("Minimum amount of result-arrival silence tolerated before a provider is considered stalled. Source age is pipeline latency; arrival silence is continuity.")]
    [Range(0.05f, 0.30f)]
    public float minimumArrivalFreshnessSeconds = 0.10f;

    [Tooltip("Arrival freshness follows measured provider cadence instead of using the source-age latency as a dropout detector.")]
    [Range(1.2f, 5.0f)]
    public float arrivalFreshnessIntervalMultiplier = 2.8f;

    [Tooltip("Maximum result-arrival silence tolerated for a live provider. This is intentionally far below the absolute source-age ceiling.")]
    [Range(0.10f, 0.50f)]
    public float maximumArrivalFreshnessSeconds = 0.22f;

    [Tooltip("Source age at which freshness scoring reaches zero. A live provider may still be used beyond this value until the hard source-age ceiling, but it is reported as high-latency rather than repeatedly dropped.")]
    [Range(0.15f, 0.60f)]
    public float sourceAgeScoreFullSeconds = 0.35f;

    [Tooltip("A different healthy provider must beat the active provider by this score.")]
    [Range(0f, 1f)]
    public float providerSwitchScoreMargin = 0.10f;

    [Tooltip("Independent candidate source frames required for a normal ownership switch.")]
    [Range(1, 6)]
    public int providerSwitchConfirmationFrames = 3;

    [Tooltip("Minimum healthy ownership time before a non-stale provider can be replaced.")]
    [Range(0f, 1f)]
    public float minimumProviderHoldSeconds = 0.45f;

    [Range(0f, 2f)]
    public float qualityScoreWeight = 0.62f;

    [Tooltip("A rigid provider below this geometry quality is unavailable for root authority and may fall back to the other backend.")]
    [Range(0f, 1f)]
    public float minimumProviderGeometryQuality = 0.20f;

    [Range(0f, 2f)]
    public float freshnessScoreWeight = 0.18f;

    [Range(0f, 1f)]
    public float cadenceScoreWeight = 0.12f;

    [Tooltip("Cadence deviation / interval ratio that maps to zero cadence quality.")]
    [Range(0.05f, 1f)]
    public float cadenceJitterFullRatio = 0.42f;

    [Header("Commercial canonical rigid solve")]
    [Tooltip("Use upper-face rigid geometry for root translation so jaw/mouth motion cannot pull the avatar root. This is a spatial anchor change, not a temporal filter.")]
    public bool useJawNeutralRigidTranslationAnchor = true;

    [Range(0f, 1f)]
    public float rigidAnchorEyeWeight = 0.72f;

    [Range(0f, 1f)]
    public float rigidAnchorCheekWeight = 0.28f;

    [Range(0f, 1f)]
    public float rigidAnchorForeheadWeight = 0.00f;

    [Tooltip("Align the first frame of a newly selected provider to the last rendered canonical pose, then release only as intentional motion is observed. This prevents backend handoff pops without adding a frame buffer or low-pass stage.")]
    public bool enableProviderHandoffNormalization = true;

    [Range(0.05f, 0.50f)]
    public float handoffReferenceMaximumAge = 0.28f;

    [Tooltip("Short Holding gaps may resume from the last displayed canonical rigid pose even when the same provider returns. Longer gaps are not aligned because motion may have occurred while unobserved.")]
    [Range(0.05f, 0.50f)]
    public float resumeHandoffMaximumGapSeconds = 0.22f;

    [Tooltip("Resume alignment is bounded by fresh provider samples so a user who moved during a short tracking gap cannot remain permanently pinned to the pre-gap pose.")]
    [Range(1, 6)]
    public int resumeHandoffReleaseFrames = 2;

    [Range(0.005f, 0.20f)]
    public float handoffMaximumCenterOffset = 0.10f;

    [Range(1f, 45f)]
    public float handoffMaximumRotationOffsetDegrees = 20f;

    [Range(0.60f, 1.00f)]
    public float handoffMinimumScaleRatio = 0.80f;

    [Range(1.00f, 1.50f)]
    public float handoffMaximumScaleRatio = 1.25f;

    [Tooltip("Accumulated provider-local translation, measured in eye spans, that fully releases a transient handoff alignment.")]
    [Range(0.10f, 2.00f)]
    public float handoffReleaseTranslationEyeSpans = 0.80f;

    [Range(3f, 45f)]
    public float handoffReleaseRotationDegrees = 18f;

    [Range(0.05f, 0.50f)]
    public float handoffReleaseScaleFraction = 0.20f;

    // KIWI_V5_1_PHASE16_5_COMMERCIAL_HANDOFF_RELEASE
    [Tooltip("Maximum handoff-alignment weight that may be released by one fresh provider sample. This prevents a backend coordinate-basis offset from being dumped into one accepted pose while leaving ordinary same-provider tracking untouched.")]
    [Range(0.15f, 0.80f)]
    public float handoffMaximumWeightReleasePerSample = 0.42f;

    // KIWI_V5_1_PHASE16_8_COMMERCIAL_STICKY_RIGID_AUTHORITY
    [Header("Commercial sticky rigid authority")]
    [Tooltip("Keep the current built-in rigid authority through a short cadence miss instead of immediately switching backends. Publication still enters Holding; stale observations are never exposed as fresh tracking.")]
    public bool enableCommercialStickyRigidAuthority = true;

    [Tooltip("Arrival-silence grace before a healthy-but-late built-in rigid provider may fail over. This is authority hysteresis only; no stale frame is published during the grace period.")]
    [Range(0.25f, 1.20f)]
    public float commercialTransientFailoverGraceSeconds = 0.60f;

    [Tooltip("Absolute source-age ceiling for authority grace. Prevents an old provider identity from blocking failover indefinitely.")]
    [Range(0.45f, 1.50f)]
    public float commercialFailoverSourceAgeCeilingSeconds = 0.80f;

    [Tooltip("Minimum healthy ownership dwell before a live built-in provider may be replaced by another live provider.")]
    [Range(0.45f, 2.50f)]
    public float commercialMinimumAuthorityDwellSeconds = 1.00f;

    [Tooltip("Independent Inference Engine source frames required when returning from MediaPipe while MediaPipe remains healthy.")]
    [Range(3, 8)]
    public int commercialPrimaryRecoveryConfirmationFrames = 4;

    [Header("Diagnostics")]
    [SerializeField] private string debugActiveProvider = "-";
    [SerializeField] private float debugActiveScore;
    [SerializeField] private float debugActiveAgeMs;
    [SerializeField] private float debugActiveArrivalAgeMs;
    [SerializeField] private float debugActiveArrivalLimitMs;
    [SerializeField] private float debugActiveCadenceQuality;
    [SerializeField] private int debugExternalProviderCount;
    [SerializeField] private string debugSwitchCandidate = "-";
    [SerializeField] private int debugSwitchCandidateFrames;
    [SerializeField] private float debugRigidAnchorCorrection;
    [SerializeField] private bool debugHandoffActive;
    [SerializeField] private string debugHandoffProvider = "-";
    [SerializeField] private float debugHandoffWeight;
    [SerializeField] private float debugHandoffTargetWeight;
    [SerializeField] private float debugHandoffReleaseStep;
    [SerializeField] private float debugHandoffCenterOffset;
    [SerializeField] private float debugHandoffRotationOffsetDegrees;
    [SerializeField] private float debugHandoffScaleRatio = 1f;
    [SerializeField] private int debugHandoffCount;
    [SerializeField] private bool debugResumeReferenceValid;
    [SerializeField] private float debugResumeGapMs;
    [SerializeField] private int debugResumeHandoffCount;
    [SerializeField] private bool debugCommercialFailoverDeferred;
    [SerializeField] private float debugCommercialFailoverGraceRemainingMs;
    [SerializeField] private int debugCommercialDeferredFailoverCount;

    [Header("Phase 9 normalization diagnostics")]
    [SerializeField] private string debugActiveTimebase = "-";
    [SerializeField] private string debugActiveTimestampQuality = "-";
    [SerializeField] private string debugActiveHorizontalConvention = "-";
    [SerializeField] private bool debugCanonicalInputMirrored;
    [SerializeField] private bool debugHorizontalTransformApplied;
    [SerializeField] private int debugTimebaseResetCount;
    [SerializeField] private int debugArrivalFallbackCount;
    [SerializeField] private int debugHorizontalTransformCount;

    private FaceLandmarkerRunner _runner;

    private readonly ProviderSlot _mediaPipe =
        new ProviderSlot
        {
            id = MediaPipeProviderId,
            capabilities =
                TrackingCapability.HeadPose |
                TrackingCapability.FaceGeometry |
                TrackingCapability.Expressions
        };

    private readonly ProviderSlot _inference =
        new ProviderSlot
        {
            id = InferenceProviderId,
            capabilities =
                TrackingCapability.HeadPose |
                TrackingCapability.FaceGeometry
        };

    private readonly Dictionary<string, ProviderSlot>
        _external =
            new Dictionary<string, ProviderSlot>(
                StringComparer.Ordinal);

    private string _activeProviderId =
        string.Empty;

    private double _activeProviderSinceRealtime;

    private string _switchCandidateId =
        string.Empty;

    private ulong _switchCandidateSourceFrameId;
    private int _switchCandidateCount;

    private ulong _lastObservedRunnerFrameId;

    private ulong _hubFrameId;
    private string _lastPublishedProviderId =
        string.Empty;
    private ulong _lastPublishedSourceFrameId;

    private FacePrecisionTrackingData _latestPublished;
    private KiwiTrackingNormalizedFrameMetadata _latestPublishedMetadata;
    private bool _hasPublished;

    private int _arrivalFallbackCount;
    private int _horizontalTransformCount;

    private bool _handoffActive;
    private string _handoffProviderId = string.Empty;
    private FacePrecisionTrackingData _handoffStartRaw;
    private Vector2 _handoffCenterOffset;
    private Quaternion _handoffRotationOffset = Quaternion.identity;
    private float _handoffScaleRatio = 1f;
    private float _handoffWeight;
    private int _handoffCount;
    private bool _handoffIsResume;
    private int _resumeReleaseFrameCount;

    // v4.9: publication validity and presentation continuity are separate.
    // When a provider briefly exceeds the hard source-age ceiling, consumers
    // enter Holding and the hub stops publishing that stale frame. Preserve a
    // bounded copy of the last displayed canonical pose solely as a resume
    // alignment reference. It is never exposed as a fresh tracking frame.
    private bool _hasResumeReference;
    private FacePrecisionTrackingData _resumeReference;
    private string _resumeReferenceProviderId = string.Empty;
    private double _resumeGapStartedRealtime;
    private int _resumeHandoffCount;
    private bool _commercialFailoverDeferredLastPass;
    private bool _commercialFailoverWindowActive;
    private int _commercialDeferredFailoverCount;

    // KIWI_V5_1_PHASE16_6_COMMERCIAL_RIGID_COHERENCE_GUARD
    // Final canonical-provider output guard. It is intentionally not a normal
    // smoother: only a physically inconsistent center+rotation+depth shock, or
    // a catastrophic center residual unsupported by eye/nose/chin translation,
    // is bounded. Ordinary same-provider motion remains untouched.
    private bool _hasCommercialRigidCoherenceHistory;
    private FacePrecisionTrackingData _commercialRigidPrevious;
    private string _commercialRigidPreviousProvider = string.Empty;
    private int _commercialRigidShockCount;
    private float _commercialRigidLastCenterResidualEyeSpans;
    private float _commercialRigidLastRotationDelta;
    private float _commercialRigidLastDepthLogDelta;

    public int CommercialRigidShockCount => _commercialRigidShockCount;
    public float CommercialRigidLastCenterResidualEyeSpans =>
        _commercialRigidLastCenterResidualEyeSpans;
    public float CommercialRigidLastRotationDelta =>
        _commercialRigidLastRotationDelta;
    public float CommercialRigidLastDepthLogDelta =>
        _commercialRigidLastDepthLogDelta;

    public string ActiveProviderId =>
        _activeProviderId;

    public bool HasPublishedFrame =>
        _hasPublished;

    public bool HandoffActive =>
        _handoffActive;

    public string HandoffProviderId =>
        _handoffProviderId;

    public float HandoffWeight =>
        _handoffWeight;

    public float HandoffTargetWeight =>
        debugHandoffTargetWeight;

    public float HandoffReleaseStep =>
        debugHandoffReleaseStep;

    public float HandoffCenterOffsetMagnitude =>
        _handoffCenterOffset.magnitude;

    public float HandoffRotationOffsetDegrees =>
        Quaternion.Angle(
            Quaternion.identity,
            _handoffRotationOffset);

    public float HandoffScaleRatio =>
        _handoffScaleRatio;

    public int HandoffCount =>
        _handoffCount;

    public bool ResumeReferenceValid =>
        _hasResumeReference;

    public float ResumeGapMilliseconds =>
        _hasResumeReference
            ? Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    _resumeGapStartedRealtime) * 1000f)
            : 0f;

    public int ResumeHandoffCount =>
        _resumeHandoffCount;

    public bool CommercialFailoverDeferred =>
        _commercialFailoverDeferredLastPass;

    public float CommercialFailoverGraceRemainingMilliseconds =>
        debugCommercialFailoverGraceRemainingMs;

    public int CommercialDeferredFailoverCount =>
        _commercialDeferredFailoverCount;

    public KiwiTrackingNormalizedFrameMetadata ActiveNormalizationMetadata =>
        _latestPublishedMetadata;

    public KiwiTrackingTimebase ActiveSourceTimebase =>
        _latestPublishedMetadata.valid
            ? _latestPublishedMetadata.sourceTimebase
            : KiwiTrackingTimebase.LegacyUnspecified;

    public KiwiTrackingTimestampQuality ActiveTimestampQuality =>
        _latestPublishedMetadata.valid
            ? _latestPublishedMetadata.timestampQuality
            : KiwiTrackingTimestampQuality.ArrivalFallback;

    public KiwiTrackingHorizontalConvention ActiveSourceHorizontalConvention =>
        _latestPublishedMetadata.valid
            ? _latestPublishedMetadata.sourceHorizontalConvention
            : KiwiTrackingHorizontalConvention.CanonicalPresentation;

    public bool CanonicalInputHorizontallyMirrored =>
        GetCanonicalInputHorizontallyMirrored();

    public bool ActiveHorizontalTransformApplied =>
        _latestPublishedMetadata.valid &&
        _latestPublishedMetadata.horizontalTransformApplied;

    public int ActiveTimebaseResetCount =>
        _latestPublishedMetadata.valid
            ? _latestPublishedMetadata.timebaseResetCount
            : 0;

    public int ArrivalFallbackCount =>
        _arrivalFallbackCount;

    public int HorizontalTransformCount =>
        _horizontalTransformCount;

    public float ActiveProviderSourceAgeMilliseconds =>
        debugActiveAgeMs;

    public float ActiveProviderArrivalAgeMilliseconds =>
        debugActiveArrivalAgeMs;

    public float ActiveProviderArrivalLimitMilliseconds =>
        debugActiveArrivalLimitMs;

    public float ActiveRigidAnchorCorrection =>
        string.Equals(
            _activeProviderId,
            InferenceProviderId,
            StringComparison.Ordinal)
                ? _inference.rigidAnchorCorrectionMagnitude
                : string.Equals(
                    _activeProviderId,
                    MediaPipeProviderId,
                    StringComparison.Ordinal)
                    ? _mediaPipe.rigidAnchorCorrectionMagnitude
                    : 0f;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiTrackingProviderHub>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiTrackingProviderHub>();
    }

    private void Awake()
    {
        _instance = this;

        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshRunner();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        KiwiRuntimeGenerationContext.AdvanceTrackingSessionGeneration();

        _runner = null;
        _activeProviderId = string.Empty;
        _activeProviderSinceRealtime = 0.0;
        _lastObservedRunnerFrameId = 0UL;
        ClearSwitchCandidate();

        // Do not let a prior scene's cached Runner frame survive a scene change.
        ClearSlot(_mediaPipe);
        ClearSlot(_inference);

        _latestPublished = default;
        _latestPublishedMetadata = default;
        _hasPublished = false;
        _lastPublishedProviderId = string.Empty;
        _lastPublishedSourceFrameId = 0UL;
        ResetHandoff(false);
        ClearResumeReference();
        _commercialFailoverDeferredLastPass = false;
        _commercialFailoverWindowActive = false;
        _commercialDeferredFailoverCount = 0;
        debugCommercialFailoverDeferred = false;
        debugCommercialFailoverGraceRemainingMs = 0f;
        debugCommercialDeferredFailoverCount = 0;

        RefreshRunner();
    }

    private void Update()
    {
        RefreshCanonicalSelectionNow();
    }

    // KIWI_V5_1_PHASE5_CANONICAL_PROVIDER_REFRESH
    // Update runs before FaceLandmarkerRunner.Update because the Hub has a very
    // early execution order. The canonical frame coordinator calls this same
    // arbitration pass again in LateUpdate, after Runner.Update and before
    // FacePartCropper, so Root and semantic FaceParts can consume one current
    // provider decision without duplicating or bypassing switch policy.
    public void RefreshCanonicalSelectionNow()
    {
        RefreshRunner();
        ObserveBuiltInRunner();

        _commercialFailoverDeferredLastPass = false;
        debugCommercialFailoverDeferred = false;
        debugCommercialFailoverGraceRemainingMs = 0f;

        Candidate best =
            FindBestCandidate(
                TrackingCapability.HeadPose |
                TrackingCapability.FaceGeometry);

        if (!best.valid)
        {
            // KIWI_V4_9_CONTINUITY_RESUME_REFERENCE
            // Stop publishing the stale observation immediately, but keep the
            // last displayed canonical pose as a short-lived presentation-only
            // resume reference. This closes the Holding -> fresh snap that was
            // visible in the dense v4.8 recording without making stale data
            // readable as current tracking.
            CaptureResumeReferenceIfNeeded();

            // KIWI_V5_0_1_PRESERVE_PROVIDER_IDENTITY_DURING_SHORT_GAP
            // A temporarily unavailable candidate is not a provider switch.
            // Keep the active provider identity while publication is gated so
            // the same backend can resume without being normalized back onto
            // the pre-gap pose every time one cadence interval is missed.
            // TryGetLatestFrame still returns false because _hasPublished is
            // cleared below; this preserves Holding semantics without pinning.
            _hasPublished = false;
            _commercialFailoverWindowActive = false;
            ClearSwitchCandidate();
            ResetHandoff(false);

            debugActiveProvider = "-";
            debugActiveScore = 0f;
            debugActiveAgeMs = 0f;
            debugActiveArrivalAgeMs = 0f;
            debugActiveArrivalLimitMs = 0f;
            debugActiveCadenceQuality = 0f;
            return;
        }

        ExpireResumeReferenceIfNeeded();

        Candidate active =
            GetCandidateById(
                _activeProviderId,
                TrackingCapability.HeadPose |
                TrackingCapability.FaceGeometry);

        Candidate selected =
            best;

        double nowRealtime =
            Time.realtimeSinceStartupAsDouble;

        // KIWI_V5_1_PHASE16_8_COMMERCIAL_STICKY_RIGID_AUTHORITY
        // A 150-500 ms cadence miss is presentation Holding, not proof that a
        // second backend should become the rigid authority. Keep the existing
        // provider identity during a bounded grace window. No old observation
        // is published here: _hasPublished is cleared and consumers keep the
        // normal Holding/Lost Prediction=0 contract. Invalid geometry bypasses
        // this grace and may fail over immediately.
        if (
            !active.valid &&
            ShouldDeferCommercialFailover(
                best,
                out float commercialGraceRemainingSeconds,
                out ProviderSlot deferredActiveSlot)
        )
        {
            CaptureResumeReferenceIfNeeded();
            _hasPublished = false;
            ClearSwitchCandidate();
            ResetHandoff(false);

            _commercialFailoverDeferredLastPass = true;
            if (!_commercialFailoverWindowActive)
            {
                _commercialFailoverWindowActive = true;
                _commercialDeferredFailoverCount++;
            }
            debugCommercialFailoverDeferred = true;
            debugCommercialDeferredFailoverCount =
                _commercialDeferredFailoverCount;
            debugCommercialFailoverGraceRemainingMs =
                Mathf.Max(0f, commercialGraceRemainingSeconds * 1000f);

            debugActiveProvider =
                string.IsNullOrEmpty(_activeProviderId)
                    ? "-"
                    : _activeProviderId + " (hold)";
            debugActiveScore = 0f;
            debugActiveAgeMs =
                deferredActiveSlot != null
                    ? CalculateFrameAgeSeconds(deferredActiveSlot) * 1000f
                    : 0f;
            debugActiveArrivalAgeMs =
                deferredActiveSlot != null
                    ? CalculateArrivalAgeSeconds(deferredActiveSlot) * 1000f
                    : 0f;
            debugActiveArrivalLimitMs =
                commercialTransientFailoverGraceSeconds * 1000f;
            debugActiveCadenceQuality =
                deferredActiveSlot != null
                    ? CalculateCadenceQuality(deferredActiveSlot)
                    : 0f;
            return;
        }

        _commercialFailoverWindowActive = false;

        if (active.valid)
        {
            selected = active;

            float requiredAuthorityDwellSeconds =
                enableCommercialStickyRigidAuthority
                    ? Mathf.Max(
                        minimumProviderHoldSeconds,
                        commercialMinimumAuthorityDwellSeconds)
                    : minimumProviderHoldSeconds;

            bool holdSatisfied =
                _activeProviderSinceRealtime <=
                    0.0 ||
                nowRealtime -
                    _activeProviderSinceRealtime >=
                    requiredAuthorityDwellSeconds;

            if (
                holdSatisfied &&
                !string.Equals(
                    best.slot.id,
                    active.slot.id,
                    StringComparison.Ordinal) &&
                best.score >=
                    active.score +
                    providerSwitchScoreMargin
            )
            {
                ObserveSwitchCandidate(best);

                int requiredSwitchFrames =
                    Mathf.Max(
                        1,
                        providerSwitchConfirmationFrames);

                if (
                    enableCommercialStickyRigidAuthority &&
                    string.Equals(
                        active.slot.id,
                        MediaPipeProviderId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        best.slot.id,
                        InferenceProviderId,
                        StringComparison.Ordinal)
                )
                {
                    requiredSwitchFrames =
                        Mathf.Max(
                            requiredSwitchFrames,
                            commercialPrimaryRecoveryConfirmationFrames);
                }

                if (
                    _switchCandidateCount >=
                    requiredSwitchFrames
                )
                {
                    selected = best;
                    ClearSwitchCandidate();
                }
            }
            else
            {
                ClearSwitchCandidate();
            }
        }
        else
        {
            // Lost/stale active provider: do not wait for confirmation.
            ClearSwitchCandidate();
        }

        bool providerChanged =
            !string.Equals(
                _activeProviderId,
                selected.slot.id,
                StringComparison.Ordinal);

        if (providerChanged)
        {
            // ProviderGeneration changes only when rigid authority identity
            // actually changes. A same-provider short-gap resume keeps the
            // same _activeProviderId and therefore does not invalidate itself
            // as a synthetic provider switch.
            KiwiRuntimeGenerationContext.AdvanceProviderGeneration();

            BeginProviderHandoff(selected);

            _activeProviderSinceRealtime =
                nowRealtime;
        }
        else if (
            _hasResumeReference &&
            string.Equals(
                _resumeReferenceProviderId,
                selected.slot.id,
                StringComparison.Ordinal)
        )
        {
            // KIWI_V5_0_1_SAME_PROVIDER_RESUME_IS_NOT_HANDOFF
            // The backend stayed the same. Existing KiwiFaceMotion motion
            // resampling handles the next measured sample; applying a full
            // zero-discontinuity provider handoff here can repeatedly align
            // every recovery sample to the old displayed pose and freeze Root.
            ClearResumeReference();
        }

        _activeProviderId =
            selected.slot.id;

        debugActiveProvider =
            selected.slot.id;

        debugActiveScore =
            selected.score;

        debugActiveAgeMs =
            selected.ageSeconds * 1000f;

        debugActiveArrivalAgeMs =
            CalculateArrivalAgeSeconds(
                selected.slot) * 1000f;

        debugActiveArrivalLimitMs =
            CalculateArrivalFreshnessLimit(
                selected.slot) * 1000f;

        debugActiveCadenceQuality =
            selected.cadenceQuality;

        debugRigidAnchorCorrection =
            selected.slot.rigidAnchorCorrectionMagnitude;

        PublishIfChanged(selected);

        debugExternalProviderCount =
            _external.Count;

        RemoveLongStaleExternalProviders();
    }

    /// <summary>
    /// Legacy external-provider submission.
    ///
    /// Compatibility contract: the adapter already uses Kiwi's current
    /// presentation horizontal convention. submissionHostTicks, when present,
    /// is local Stopwatch time; otherwise arrival time is used. New adapters
    /// should call the explicit-contract overload below.
    /// </summary>
    public void SubmitExternalFrame(
        string providerId,
        int priority,
        TrackingCapability capabilities,
        FacePrecisionTrackingData data)
    {
        SubmitExternalFrame(
            providerId,
            priority,
            capabilities,
            data,
            KiwiExternalTrackingFrameContract.LegacyNormalized(data));
    }

    /// <summary>
    /// Phase 9 explicit adapter boundary. Provider-native time and horizontal
    /// convention are normalized here before arbitration / handoff.
    /// </summary>
    public void SubmitExternalFrame(
        string providerId,
        int priority,
        TrackingCapability capabilities,
        FacePrecisionTrackingData data,
        KiwiExternalTrackingFrameContract contract)
    {
        if (
            string.IsNullOrWhiteSpace(providerId) ||
            !data.isValid
        )
        {
            return;
        }

        if (
            !_external.TryGetValue(
                providerId,
                out ProviderSlot slot)
        )
        {
            slot =
                new ProviderSlot
                {
                    id = providerId
                };

            _external.Add(
                providerId,
                slot);
        }

        slot.priority =
            priority;

        slot.capabilities =
            capabilities;

        ulong providerSourceFrameId =
            data.frameId;

        ulong sourceFrameId =
            NormalizeExternalSourceFrameId(
                slot,
                providerSourceFrameId);

        UpdateSlot(
            slot,
            data,
            sourceFrameId,
            providerSourceFrameId,
            contract,
            true,
            Time.realtimeSinceStartupAsDouble);
    }

    public void RemoveExternalProvider(
        string providerId)
    {
        if (
            string.IsNullOrEmpty(providerId)
        )
        {
            return;
        }

        _external.Remove(providerId);

        if (
            string.Equals(
                _activeProviderId,
                providerId,
                StringComparison.Ordinal)
        )
        {
            _activeProviderId =
                string.Empty;

            KiwiRuntimeGenerationContext.AdvanceProviderGeneration();
        }
    }

    public bool TryGetLatestFrame(
        out FacePrecisionTrackingData data,
        out string providerId)
    {
        return TryGetLatestFrame(
            out data,
            out providerId,
            out _);
    }

    public bool TryGetLatestFrame(
        out FacePrecisionTrackingData data,
        out string providerId,
        out KiwiTrackingNormalizedFrameMetadata metadata)
    {
        Candidate active =
            GetCandidateById(
                _activeProviderId,
                TrackingCapability.HeadPose |
                TrackingCapability.FaceGeometry);

        if (
            !_hasPublished ||
            !active.valid ||
            !_latestPublished.isValid ||
            _latestPublished.frameId == 0UL
        )
        {
            // Never leak the last published rigid sample through an unsuccessful
            // Try* call. Most consumers check the return value, but clearing the
            // out parameters makes stale-frame misuse impossible for future code.
            data = default;
            providerId = string.Empty;
            metadata = default;
            return false;
        }

        data =
            _latestPublished;

        providerId =
            _activeProviderId;

        metadata =
            _latestPublishedMetadata;

        return true;
    }

    /// <summary>
    /// Capability-aware access for future head/body/hand adapters.
    /// Existing motion consumers can keep using the overload above.
    /// </summary>
    public bool TryGetLatestFrame(
        TrackingCapability requiredCapabilities,
        out FacePrecisionTrackingData data,
        out string providerId)
    {
        bool requestsRigidHead =
            (
                requiredCapabilities &
                TrackingCapability.HeadPose
            ) !=
            0;

        if (
            requestsRigidHead &&
            _hasPublished &&
            _latestPublished.isValid
        )
        {
            Candidate active =
                GetCandidateById(
                    _activeProviderId,
                    TrackingCapability.HeadPose |
                    TrackingCapability.FaceGeometry);

            if (active.valid)
            {
                data =
                    _latestPublished;

                providerId =
                    _activeProviderId;

                return true;
            }
        }

        Candidate candidate =
            FindBestCandidate(
                requiredCapabilities);

        if (!candidate.valid)
        {
            data = default;
            providerId = string.Empty;
            return false;
        }

        data =
            candidate.slot.data;

        providerId =
            candidate.slot.id;

        return
            data.isValid;
    }

    /// <summary>
    /// Returns the best provider health for a capability set without changing
    /// the active rigid-head/geometry owner.
    /// </summary>
    public bool TryGetCapabilityHealth(
        TrackingCapability requiredCapabilities,
        out FacePrecisionTrackingData data,
        out CapabilityHealth health)
    {
        Candidate candidate =
            FindBestCandidate(
                requiredCapabilities);

        if (!candidate.valid)
        {
            data = default;
            health = default;
            health.requiredCapabilities =
                requiredCapabilities;
            health.providerId =
                string.Empty;
            return false;
        }

        data =
            candidate.slot.data;

        health =
            new CapabilityHealth
            {
                valid =
                    data.isValid,
                requiredCapabilities =
                    requiredCapabilities,
                providerId =
                    candidate.slot.id,
                sourceFrameId =
                    candidate.slot.sourceFrameId,
                ageSeconds =
                    candidate.ageSeconds,
                score =
                    candidate.score,
                cadenceQuality =
                    candidate.cadenceQuality,
                geometryQuality =
                    Mathf.Clamp01(
                        data.geometryQuality),
                backend =
                    data.backend,
                timestampQuality =
                    candidate.slot.metadata.timestampQuality,
                sourceTimebase =
                    candidate.slot.metadata.sourceTimebase,
                canonicalInputHorizontallyMirrored =
                    candidate.slot.metadata.canonicalInputHorizontallyMirrored
            };

        return
            health.valid;
    }

    private void RefreshRunner()
    {
        if (!useFaceLandmarkerRunner)
        {
            _runner = null;
            return;
        }

        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        _mediaPipe.priority =
            mediaPipePriority;

        _inference.priority =
            inferenceEnginePriority;
    }

    private void ObserveBuiltInRunner()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.frameId == 0UL ||
            data.frameId == _lastObservedRunnerFrameId
        )
        {
            return;
        }

        _lastObservedRunnerFrameId =
            data.frameId;

        ProviderSlot slot =
            data.backend ==
                KiwiTrackingBackend.InferenceEngine
                ? _inference
                : _mediaPipe;

        Vector2 originalCenter =
            data.faceCenter;

        if (
            useJawNeutralRigidTranslationAnchor &&
            TryCalculateJawNeutralRigidAnchor(
                data,
                out Vector2 rigidCenter)
        )
        {
            data.faceCenter =
                rigidCenter;

            slot.rigidAnchorCorrectionMagnitude =
                Vector2.Distance(
                    originalCenter,
                    rigidCenter);
        }
        else
        {
            slot.rigidAnchorCorrectionMagnitude =
                0f;
        }

        bool runnerMirrored =
            _runner != null &&
            _runner.IsInputHorizontallyMirrored;

        KiwiExternalTrackingFrameContract contract =
            new KiwiExternalTrackingFrameContract
            {
                timebase =
                    data.submissionHostTicks > 0L
                        ? KiwiTrackingTimebase.HostStopwatchTicks
                        : KiwiTrackingTimebase.ArrivalHostOnly,
                sourceTimestamp =
                    data.submissionHostTicks,
                arrivalHostTicks =
                    data.arrivalHostTicks,
                horizontalConvention =
                    runnerMirrored
                        ? KiwiTrackingHorizontalConvention.Mirrored
                        : KiwiTrackingHorizontalConvention.Unmirrored
            };

        UpdateSlot(
            slot,
            data,
            data.frameId,
            data.frameId,
            contract,
            false,
            Time.realtimeSinceStartupAsDouble);
    }

    private void UpdateSlot(
        ProviderSlot slot,
        FacePrecisionTrackingData data,
        ulong sourceFrameId,
        ulong providerSourceFrameId,
        KiwiExternalTrackingFrameContract contract,
        bool isExternal,
        double submittedRealtime)
    {
        if (slot == null)
        {
            return;
        }

        NormalizeProviderFrame(
            slot,
            ref data,
            sourceFrameId,
            providerSourceFrameId,
            contract,
            isExternal,
            out KiwiTrackingNormalizedFrameMetadata metadata);

        long arrivalTicks =
            data.arrivalHostTicks;

        float interval =
            0f;

        if (
            slot.hasFrame &&
            arrivalTicks > 0L &&
            slot.arrivalHostTicks > 0L &&
            arrivalTicks > slot.arrivalHostTicks
        )
        {
            interval =
                (float)(
                    (arrivalTicks - slot.arrivalHostTicks) /
                    (double)
                    System.Diagnostics.Stopwatch.Frequency);
        }
        else if (
            slot.hasFrame &&
            submittedRealtime >
                slot.submittedRealtime
        )
        {
            interval =
                (float)(
                    submittedRealtime -
                    slot.submittedRealtime);
        }

        if (
            interval >
            0.0001f &&
            interval <
            1.0f
        )
        {
            if (slot.frameIntervalEma <= 0f)
            {
                slot.frameIntervalEma =
                    interval;
            }
            else
            {
                float deviation =
                    Mathf.Abs(
                        interval -
                        slot.frameIntervalEma);

                slot.frameIntervalDeviationEma =
                    Mathf.Lerp(
                        slot.frameIntervalDeviationEma,
                        deviation,
                        0.20f);

                slot.frameIntervalEma =
                    Mathf.Lerp(
                        slot.frameIntervalEma,
                        interval,
                        0.16f);
            }
        }

        slot.data =
            data;

        slot.metadata =
            metadata;

        slot.sourceFrameId =
            sourceFrameId;

        slot.arrivalHostTicks =
            arrivalTicks;

        slot.submittedRealtime =
            submittedRealtime;

        slot.hasFrame =
            true;
    }

    private void NormalizeProviderFrame(
        ProviderSlot slot,
        ref FacePrecisionTrackingData data,
        ulong normalizedSourceFrameId,
        ulong providerSourceFrameId,
        KiwiExternalTrackingFrameContract contract,
        bool isExternal,
        out KiwiTrackingNormalizedFrameMetadata metadata)
    {
        long nowHostTicks =
            KiwiTrackingNormalizationMath.CurrentHostTicks();

        long arrivalHostTicks =
            contract.arrivalHostTicks > 0L
                ? contract.arrivalHostTicks
                : data.arrivalHostTicks;

        if (
            arrivalHostTicks <= 0L ||
            arrivalHostTicks > nowHostTicks
        )
        {
            arrivalHostTicks =
                nowHostTicks;
        }

        KiwiTrackingTimebase timebase =
            contract.timebase;

        if (timebase == KiwiTrackingTimebase.LegacyUnspecified)
        {
            timebase =
                data.submissionHostTicks > 0L
                    ? KiwiTrackingTimebase.HostStopwatchTicks
                    : KiwiTrackingTimebase.ArrivalHostOnly;
        }

        bool sameExternalSourceFrame =
            isExternal &&
            slot.hasFrame &&
            normalizedSourceFrameId == slot.sourceFrameId &&
            slot.metadata.valid;

        if (sameExternalSourceFrame)
        {
            // A duplicate provider frame is liveness, not a new observation.
            // Preserve every source-owned field/geometry exactly and update only
            // the local arrival tick. This prevents duplicate transport from
            // refreshing source age, quality, handedness, or arbitration input.
            data =
                slot.data;

            data.arrivalHostTicks =
                arrivalHostTicks;

            KiwiTrackingNormalizedFrameMetadata previous =
                slot.metadata;

            metadata =
                new KiwiTrackingNormalizedFrameMetadata(
                    valid: previous.valid,
                    sourceTimebase: previous.sourceTimebase,
                    timestampQuality: previous.timestampQuality,
                    sourceHorizontalConvention:
                        previous.sourceHorizontalConvention,
                    canonicalInputHorizontallyMirrored:
                        previous.canonicalInputHorizontallyMirrored,
                    horizontalTransformApplied:
                        previous.horizontalTransformApplied,
                    providerSourceTimestamp:
                        previous.providerSourceTimestamp,
                    providerSourceFrameId:
                        previous.providerSourceFrameId,
                    observationHostTicks:
                        previous.observationHostTicks,
                    arrivalHostTicks:
                        arrivalHostTicks,
                    timebaseResetCount:
                        previous.timebaseResetCount);

            return;
        }

        long observationHostTicks =
            arrivalHostTicks;

        KiwiTrackingTimestampQuality timestampQuality =
            KiwiTrackingTimestampQuality.ArrivalFallback;

        switch (timebase)
        {
            case KiwiTrackingTimebase.HostStopwatchTicks:
            {
                long sourceHostTicks =
                    contract.sourceTimestamp > 0L
                        ? contract.sourceTimestamp
                        : data.submissionHostTicks;

                if (
                    sourceHostTicks > 0L &&
                    sourceHostTicks <= arrivalHostTicks
                )
                {
                    observationHostTicks =
                        sourceHostTicks;
                    timestampQuality =
                        KiwiTrackingTimestampQuality.ExactHostObservation;
                }

                break;
            }

            case KiwiTrackingTimebase.ProviderMonotonicMilliseconds:
            case KiwiTrackingTimebase.ProviderMonotonicMicroseconds:
            case KiwiTrackingTimebase.ProviderMonotonicNanoseconds:
                observationHostTicks =
                    MapProviderTimeToHost(
                        slot,
                        timebase,
                        contract.sourceTimestamp,
                        arrivalHostTicks);
                timestampQuality =
                    KiwiTrackingTimestampQuality.ProviderMapped;
                break;

            default:
                observationHostTicks =
                    arrivalHostTicks;
                timestampQuality =
                    KiwiTrackingTimestampQuality.ArrivalFallback;
                break;
        }

        if (observationHostTicks <= 0L)
        {
            observationHostTicks =
                arrivalHostTicks;
            timestampQuality =
                KiwiTrackingTimestampQuality.ArrivalFallback;
        }

        if (observationHostTicks > arrivalHostTicks)
        {
            observationHostTicks =
                arrivalHostTicks;
            timestampQuality =
                KiwiTrackingTimestampQuality.ArrivalFallback;
        }

        data.submissionHostTicks =
            observationHostTicks;

        data.arrivalHostTicks =
            arrivalHostTicks;

        data.hasMatchedSubmissionTiming =
            timestampQuality !=
                KiwiTrackingTimestampQuality.ArrivalFallback;

        if (isExternal)
        {
            long canonicalMilliseconds =
                KiwiTrackingNormalizationMath.HostTicksToMilliseconds(
                    observationHostTicks);

            if (
                canonicalMilliseconds <=
                slot.lastCanonicalTimestampMilliseconds
            )
            {
                canonicalMilliseconds =
                    slot.lastCanonicalTimestampMilliseconds + 1L;
            }

            slot.lastCanonicalTimestampMilliseconds =
                canonicalMilliseconds;

            data.timestamp =
                canonicalMilliseconds;
        }

        bool canonicalMirrored =
            GetCanonicalInputHorizontallyMirrored();

        bool horizontalTransformApplied =
            KiwiTrackingNormalizationMath.RequiresHorizontalMirror(
                contract.horizontalConvention,
                canonicalMirrored);

        if (horizontalTransformApplied)
        {
            KiwiTrackingNormalizationMath.MirrorHorizontal(
                ref data);
            _horizontalTransformCount++;
        }

        if (
            timestampQuality ==
                KiwiTrackingTimestampQuality.ArrivalFallback
        )
        {
            _arrivalFallbackCount++;
        }

        metadata =
            new KiwiTrackingNormalizedFrameMetadata(
                valid: true,
                sourceTimebase: timebase,
                timestampQuality: timestampQuality,
                sourceHorizontalConvention:
                    contract.horizontalConvention,
                canonicalInputHorizontallyMirrored:
                    canonicalMirrored,
                horizontalTransformApplied:
                    horizontalTransformApplied,
                providerSourceTimestamp:
                    contract.sourceTimestamp,
                providerSourceFrameId:
                    providerSourceFrameId,
                observationHostTicks:
                    observationHostTicks,
                arrivalHostTicks:
                    arrivalHostTicks,
                timebaseResetCount:
                    slot.timebaseResetCount);
    }

    private long MapProviderTimeToHost(
        ProviderSlot slot,
        KiwiTrackingTimebase timebase,
        long sourceTimestamp,
        long arrivalHostTicks)
    {
        bool timebaseChanged =
            slot.hasTimebaseAnchor &&
            slot.mappedTimebase != timebase;

        bool sourceRegressed =
            slot.hasTimebaseAnchor &&
            !timebaseChanged &&
            sourceTimestamp <
                slot.lastProviderSourceTimestamp;

        if (
            !slot.hasTimebaseAnchor ||
            timebaseChanged ||
            sourceRegressed
        )
        {
            if (slot.hasTimebaseAnchor)
            {
                slot.timebaseResetCount++;
            }

            slot.hasTimebaseAnchor =
                true;
            slot.mappedTimebase =
                timebase;
            slot.timebaseSourceAnchor =
                sourceTimestamp;
            slot.timebaseHostAnchor =
                arrivalHostTicks;
            slot.lastProviderSourceTimestamp =
                sourceTimestamp;
            slot.lastNormalizedObservationHostTicks =
                arrivalHostTicks;

            return arrivalHostTicks;
        }

        double scale =
            KiwiTrackingNormalizationMath.SourceUnitsToHostTicks(
                timebase);

        double sourceDelta =
            (double)sourceTimestamp -
            slot.timebaseSourceAnchor;

        double hostDelta =
            sourceDelta *
            scale;

        long normalized =
            arrivalHostTicks;

        if (
            scale > 0.0 &&
            !double.IsNaN(hostDelta) &&
            !double.IsInfinity(hostDelta) &&
            hostDelta >= long.MinValue &&
            hostDelta <= long.MaxValue
        )
        {
            double candidate =
                slot.timebaseHostAnchor +
                hostDelta;

            if (
                !double.IsNaN(candidate) &&
                !double.IsInfinity(candidate) &&
                candidate >= 1.0 &&
                candidate <= long.MaxValue
            )
            {
                normalized =
                    (long)Math.Round(candidate);
            }
        }

        // Provider clocks can drift relative to local Stopwatch. Observation
        // time cannot be later than result arrival, so re-anchor just enough to
        // keep the mapped clock causal instead of publishing a future frame.
        if (normalized > arrivalHostTicks)
        {
            long correction =
                normalized -
                arrivalHostTicks;

            double adjustedAnchor =
                (double)slot.timebaseHostAnchor -
                correction;

            slot.timebaseHostAnchor =
                adjustedAnchor > 1.0
                    ? (long)Math.Min(
                        adjustedAnchor,
                        long.MaxValue)
                    : 1L;

            normalized =
                arrivalHostTicks;
        }

        if (
            normalized <=
                slot.lastNormalizedObservationHostTicks &&
            arrivalHostTicks >
                slot.lastNormalizedObservationHostTicks &&
            sourceTimestamp >
                slot.lastProviderSourceTimestamp
        )
        {
            // A coarse provider clock (for example integer milliseconds) can
            // repeat/advance too slowly. Arrival is still local monotonic time and
            // is safer than fabricating a negative/zero source interval.
            normalized =
                arrivalHostTicks;
        }

        slot.lastProviderSourceTimestamp =
            sourceTimestamp;

        slot.lastNormalizedObservationHostTicks =
            normalized;

        return normalized;
    }

    private static ulong NormalizeExternalSourceFrameId(
        ProviderSlot slot,
        ulong providerSourceFrameId)
    {
        if (slot == null)
        {
            return 0UL;
        }

        bool newProviderFrame =
            providerSourceFrameId == 0UL ||
            !slot.hasExternalFrameIdentity ||
            providerSourceFrameId !=
                slot.lastProviderSourceFrameId;

        if (newProviderFrame)
        {
            slot.normalizedExternalFrameId++;

            if (slot.normalizedExternalFrameId == 0UL)
            {
                slot.normalizedExternalFrameId++;
            }

            if (providerSourceFrameId != 0UL)
            {
                slot.lastProviderSourceFrameId =
                    providerSourceFrameId;
                slot.hasExternalFrameIdentity =
                    true;
            }
        }

        return slot.normalizedExternalFrameId;
    }

    private bool GetCanonicalInputHorizontallyMirrored()
    {
        return
            _runner != null &&
            _runner.IsInputHorizontallyMirrored;
    }

    private ProviderSlot GetProviderSlotByIdRaw(
        string providerId)
    {
        if (string.IsNullOrEmpty(providerId))
        {
            return null;
        }

        if (string.Equals(
            providerId,
            MediaPipeProviderId,
            StringComparison.Ordinal))
        {
            return _mediaPipe;
        }

        if (string.Equals(
            providerId,
            InferenceProviderId,
            StringComparison.Ordinal))
        {
            return _inference;
        }

        return _external.TryGetValue(
            providerId,
            out ProviderSlot external)
                ? external
                : null;
    }

    private bool ShouldDeferCommercialFailover(
        Candidate best,
        out float graceRemainingSeconds,
        out ProviderSlot activeSlot)
    {
        graceRemainingSeconds = 0f;
        activeSlot = null;

        if (
            !enableCommercialStickyRigidAuthority ||
            !best.valid ||
            best.slot == null ||
            string.IsNullOrEmpty(_activeProviderId) ||
            string.Equals(
                _activeProviderId,
                best.slot.id,
                StringComparison.Ordinal)
        )
        {
            return false;
        }

        activeSlot =
            GetProviderSlotByIdRaw(_activeProviderId);

        if (
            activeSlot == null ||
            !activeSlot.hasFrame ||
            !activeSlot.data.isValid ||
            activeSlot.data.geometryQuality <
                minimumProviderGeometryQuality
        )
        {
            return false;
        }

        if (
            activeSlot.metadata.valid &&
            activeSlot.metadata.canonicalInputHorizontallyMirrored !=
                GetCanonicalInputHorizontallyMirrored()
        )
        {
            return false;
        }

        // Only built-in Runner backends use this short dropout grace. External
        // providers retain their explicit adapter failover behavior.
        bool activeBuiltIn =
            string.Equals(
                activeSlot.id,
                InferenceProviderId,
                StringComparison.Ordinal) ||
            string.Equals(
                activeSlot.id,
                MediaPipeProviderId,
                StringComparison.Ordinal);

        bool bestBuiltIn =
            string.Equals(
                best.slot.id,
                InferenceProviderId,
                StringComparison.Ordinal) ||
            string.Equals(
                best.slot.id,
                MediaPipeProviderId,
                StringComparison.Ordinal);

        if (!activeBuiltIn || !bestBuiltIn)
        {
            return false;
        }

        float arrivalAge =
            CalculateArrivalAgeSeconds(activeSlot);
        float sourceAge =
            CalculateFrameAgeSeconds(activeSlot);
        float grace =
            Mathf.Max(
                maximumArrivalFreshnessSeconds,
                commercialTransientFailoverGraceSeconds);
        float sourceCeiling =
            Mathf.Max(
                maximumProviderFrameAge,
                commercialFailoverSourceAgeCeilingSeconds);

        if (
            arrivalAge >= grace ||
            sourceAge >= sourceCeiling
        )
        {
            return false;
        }

        graceRemainingSeconds =
            Mathf.Max(0f, grace - arrivalAge);

        return true;
    }

    private Candidate FindBestCandidate(
        TrackingCapability requiredCapabilities)
    {
        Candidate best =
            default;

        EvaluateSlot(
            _inference,
            requiredCapabilities,
            ref best);

        EvaluateSlot(
            _mediaPipe,
            requiredCapabilities,
            ref best);

        foreach (
            KeyValuePair<string, ProviderSlot> pair
            in _external)
        {
            EvaluateSlot(
                pair.Value,
                requiredCapabilities,
                ref best);
        }

        return best;
    }

    private void EvaluateSlot(
        ProviderSlot slot,
        TrackingCapability requiredCapabilities,
        ref Candidate best)
    {
        Candidate candidate =
            BuildCandidate(
                slot,
                requiredCapabilities);

        if (
            candidate.valid &&
            (
                !best.valid ||
                candidate.score >
                    best.score
            )
        )
        {
            best =
                candidate;
        }
    }

    private Candidate GetCandidateById(
        string providerId,
        TrackingCapability requiredCapabilities)
    {
        if (
            string.IsNullOrEmpty(providerId)
        )
        {
            return default;
        }

        if (
            string.Equals(
                providerId,
                MediaPipeProviderId,
                StringComparison.Ordinal)
        )
        {
            return
                BuildCandidate(
                    _mediaPipe,
                    requiredCapabilities);
        }

        if (
            string.Equals(
                providerId,
                InferenceProviderId,
                StringComparison.Ordinal)
        )
        {
            return
                BuildCandidate(
                    _inference,
                    requiredCapabilities);
        }

        if (
            _external.TryGetValue(
                providerId,
                out ProviderSlot slot)
        )
        {
            return
                BuildCandidate(
                    slot,
                    requiredCapabilities);
        }

        return default;
    }

    private Candidate BuildCandidate(
        ProviderSlot slot,
        TrackingCapability requiredCapabilities)
    {
        if (
            slot == null ||
            !slot.hasFrame ||
            !slot.data.isValid ||
            (
                slot.capabilities &
                requiredCapabilities
            ) !=
            requiredCapabilities ||
            slot.data.geometryQuality <
                minimumProviderGeometryQuality
        )
        {
            return default;
        }

        // Phase 9: a frame normalized under a previous camera mirror
        // convention cannot compete with frames in the current presentation
        // space. The provider becomes eligible again on its next fresh submit.
        if (
            slot.metadata.valid &&
            slot.metadata.canonicalInputHorizontallyMirrored !=
                GetCanonicalInputHorizontallyMirrored()
        )
        {
            return default;
        }

        // KIWI_V5_0_LATENCY_LIVENESS_SPLIT
        // Source age is end-to-end pipeline latency. Arrival age is how long the
        // provider has been silent. Treating source latency as dropout state made
        // a healthy 20-30 Hz stream repeatedly disappear whenever GPU latency
        // crossed the old 200 ms ceiling. Mature mocap systems keep a live stream
        // continuous while reporting latency separately.
        float age =
            CalculateFrameAgeSeconds(
                slot);

        float arrivalAge =
            CalculateArrivalAgeSeconds(
                slot);

        float arrivalLimit =
            CalculateArrivalFreshnessLimit(
                slot);

        if (
            age > maximumProviderFrameAge ||
            arrivalAge > arrivalLimit
        )
        {
            return default;
        }

        float sourceFreshness =
            1f -
            Mathf.Clamp01(
                age /
                Mathf.Max(
                    0.001f,
                    sourceAgeScoreFullSeconds));

        float streamFreshness =
            1f -
            Mathf.Clamp01(
                arrivalAge /
                Mathf.Max(
                    0.001f,
                    arrivalLimit));

        float freshness =
            sourceFreshness * 0.30f +
            streamFreshness * 0.70f;

        float cadenceQuality =
            CalculateCadenceQuality(
                slot);

        float score =
            Mathf.Clamp(
                slot.priority /
                100f,
                0f,
                2f) +
            Mathf.Clamp01(
                slot.data.geometryQuality) *
            qualityScoreWeight +
            freshness *
            freshnessScoreWeight +
            cadenceQuality *
            cadenceScoreWeight;

        return
            new Candidate
            {
                valid = true,
                slot = slot,
                ageSeconds = age,
                score = score,
                cadenceQuality = cadenceQuality
            };
    }

    private float CalculateArrivalAgeSeconds(
        ProviderSlot slot)
    {
        if (slot == null)
        {
            return float.PositiveInfinity;
        }

        long arrivalTicks =
            slot.data.arrivalHostTicks > 0L
                ? slot.data.arrivalHostTicks
                : slot.arrivalHostTicks;

        if (arrivalTicks > 0L)
        {
            long now =
                System.Diagnostics.Stopwatch.GetTimestamp();

            if (now <= arrivalTicks)
            {
                return 0f;
            }

            return
                (float)(
                    (now - arrivalTicks) /
                    (double)System.Diagnostics.Stopwatch.Frequency);
        }

        return
            Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    slot.submittedRealtime));
    }

    private float CalculateArrivalFreshnessLimit(
        ProviderSlot slot)
    {
        float interval =
            slot != null &&
            slot.frameIntervalEma > 0.0001f
                ? slot.frameIntervalEma
                : 1f / 15f;

        return
            Mathf.Clamp(
                interval *
                    Mathf.Max(
                        1.2f,
                        arrivalFreshnessIntervalMultiplier),
                minimumArrivalFreshnessSeconds,
                maximumArrivalFreshnessSeconds);
    }

    private float CalculateCadenceQuality(
        ProviderSlot slot)
    {
        if (
            slot == null ||
            slot.frameIntervalEma <=
                0.0001f
        )
        {
            return 0.50f;
        }

        float jitterRatio =
            slot.frameIntervalDeviationEma /
            Mathf.Max(
                0.0001f,
                slot.frameIntervalEma);

        return
            1f -
            Mathf.Clamp01(
                jitterRatio /
                Mathf.Max(
                    0.01f,
                    cadenceJitterFullRatio));
    }

    private static float CalculateFrameAgeSeconds(
        ProviderSlot slot)
    {
        if (slot == null)
        {
            return float.PositiveInfinity;
        }

        float age =
            CalculateTrackingDataAgeSeconds(
                slot.data);

        if (
            age > 0f ||
            slot.data.submissionHostTicks > 0L ||
            slot.data.arrivalHostTicks > 0L
        )
        {
            return age;
        }

        return
            Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    slot.submittedRealtime));
    }

    private static float CalculateTrackingDataAgeSeconds(
        FacePrecisionTrackingData data)
    {
        // Source/submission time is the observation time. Arrival time only
        // tells us when asynchronous work finished. Using arrival time here
        // made a 100-300 ms-old result look "fresh" at the provider boundary.
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
                System.Diagnostics.Stopwatch
                    .Frequency);
    }

    private void ObserveSwitchCandidate(
        Candidate candidate)
    {
        if (
            !candidate.valid ||
            candidate.slot == null
        )
        {
            ClearSwitchCandidate();
            return;
        }

        if (
            !string.Equals(
                _switchCandidateId,
                candidate.slot.id,
                StringComparison.Ordinal)
        )
        {
            _switchCandidateId =
                candidate.slot.id;

            _switchCandidateSourceFrameId =
                candidate.slot.sourceFrameId;

            _switchCandidateCount =
                1;
        }
        else if (
            candidate.slot.sourceFrameId !=
            _switchCandidateSourceFrameId
        )
        {
            _switchCandidateSourceFrameId =
                candidate.slot.sourceFrameId;

            _switchCandidateCount++;
        }

        debugSwitchCandidate =
            candidate.slot.id;

        debugSwitchCandidateFrames =
            _switchCandidateCount;
    }

    private void PublishIfChanged(
        Candidate selected)
    {
        if (
            !selected.valid ||
            selected.slot == null
        )
        {
            return;
        }

        bool changed =
            !string.Equals(
                selected.slot.id,
                _lastPublishedProviderId,
                StringComparison.Ordinal) ||
            selected.slot.sourceFrameId !=
                _lastPublishedSourceFrameId;

        if (!changed)
        {
            return;
        }

        FacePrecisionTrackingData output =
            selected.slot.data;

        ApplyProviderHandoff(
            selected.slot.id,
            ref output);

        ApplyCommercialRigidCoherenceGuard(
            selected.slot.id,
            ref output);

        _hubFrameId++;

        if (_hubFrameId == 0UL)
        {
            _hubFrameId++;
        }

        output.frameId =
            _hubFrameId;

        _latestPublished =
            output;

        _latestPublishedMetadata =
            selected.slot.metadata;

        UpdateNormalizationDiagnostics(
            _latestPublishedMetadata);

        KiwiRuntimeGenerationContext.NextObservationSequence();

        _hasPublished =
            true;

        _lastPublishedProviderId =
            selected.slot.id;

        _lastPublishedSourceFrameId =
            selected.slot.sourceFrameId;
    }

    private void ApplyCommercialRigidCoherenceGuard(
        string providerId,
        ref FacePrecisionTrackingData data)
    {
        if (!data.isValid)
        {
            return;
        }

        if (!_hasCommercialRigidCoherenceHistory)
        {
            AcceptCommercialRigidCoherenceHistory(providerId, data);
            return;
        }

        FacePrecisionTrackingData previous =
            _commercialRigidPrevious;

        float referenceEyeSpan =
            Mathf.Max(
                0.01f,
                Mathf.Max(
                    previous.eyeSpan2D,
                    data.eyeSpan2D));

        Vector2 eyeDelta =
            data.eyeCenter - previous.eyeCenter;
        Vector2 noseDelta =
            data.nose - previous.nose;
        Vector2 chinDelta =
            data.chin - previous.chin;

        Vector2 expectedTranslation =
            eyeDelta * 0.50f +
            noseDelta * 0.20f +
            chinDelta * 0.30f;

        Vector2 expectedCenter =
            previous.faceCenter + expectedTranslation;

        float centerResidual =
            Vector2.Distance(
                data.faceCenter,
                expectedCenter) /
            referenceEyeSpan;

        float anchorMotion =
            Mathf.Max(
                eyeDelta.magnitude,
                Mathf.Max(
                    noseDelta.magnitude,
                    chinDelta.magnitude)) /
            referenceEyeSpan;

        float rotationDelta =
            Quaternion.Angle(
                previous.faceRotation,
                data.faceRotation);

        float depthRatio = 1f;
        if (
            previous.eyeSpan3D > 0.000001f &&
            data.eyeSpan3D > 0.000001f
        )
        {
            depthRatio =
                data.eyeSpan3D /
                previous.eyeSpan3D;
        }
        else if (
            previous.eyeSpan2D > 0.000001f &&
            data.eyeSpan2D > 0.000001f
        )
        {
            depthRatio =
                data.eyeSpan2D /
                previous.eyeSpan2D;
        }

        float depthLogDelta =
            Mathf.Abs(
                Mathf.Log(
                    Mathf.Clamp(
                        depthRatio,
                        0.50f,
                        2.00f)));

        _commercialRigidLastCenterResidualEyeSpans =
            centerResidual;
        _commercialRigidLastRotationDelta =
            rotationDelta;
        _commercialRigidLastDepthLogDelta =
            depthLogDelta;

        bool coupledShock =
            centerResidual > 0.42f &&
            rotationDelta > 18f &&
            depthLogDelta > 0.085f &&
            anchorMotion < 0.70f;

        bool catastrophicCenterShock =
            centerResidual > 1.00f &&
            anchorMotion < 0.50f;

        if (coupledShock || catastrophicCenterShock)
        {
            float maximumCenterResidual =
                referenceEyeSpan *
                (coupledShock ? 0.22f : 0.30f);

            Vector2 residual =
                data.faceCenter - expectedCenter;

            if (residual.magnitude > maximumCenterResidual)
            {
                residual =
                    residual.normalized *
                    maximumCenterResidual;
            }

            data.faceCenter =
                expectedCenter + residual;

            if (coupledShock)
            {
                data.faceRotation =
                    Quaternion.RotateTowards(
                        previous.faceRotation,
                        data.faceRotation,
                        15f);

                data.eyeSpan2D =
                    BoundCommercialScaleMetric(
                        previous.eyeSpan2D,
                        data.eyeSpan2D,
                        0.10f);

                data.eyeSpan3D =
                    BoundCommercialScaleMetric(
                        previous.eyeSpan3D,
                        data.eyeSpan3D,
                        0.10f);

                data.faceWidth2D =
                    BoundCommercialScaleMetric(
                        previous.faceWidth2D,
                        data.faceWidth2D,
                        0.10f);

                data.faceHeight2D =
                    BoundCommercialScaleMetric(
                        previous.faceHeight2D,
                        data.faceHeight2D,
                        0.10f);
            }

            _commercialRigidShockCount++;
        }

        AcceptCommercialRigidCoherenceHistory(
            providerId,
            data);
    }

    private static float BoundCommercialScaleMetric(
        float previous,
        float current,
        float maximumFraction)
    {
        if (
            previous <= 0.000001f ||
            current <= 0.000001f
        )
        {
            return current;
        }

        float maximumDelta =
            previous * Mathf.Max(0f, maximumFraction);

        return Mathf.MoveTowards(
            previous,
            current,
            maximumDelta);
    }

    private void AcceptCommercialRigidCoherenceHistory(
        string providerId,
        FacePrecisionTrackingData data)
    {
        _commercialRigidPrevious = data;
        _commercialRigidPreviousProvider =
            providerId ?? string.Empty;
        _hasCommercialRigidCoherenceHistory = true;
    }

    private void UpdateNormalizationDiagnostics(
        KiwiTrackingNormalizedFrameMetadata metadata)
    {
        if (!metadata.valid)
        {
            debugActiveTimebase = "-";
            debugActiveTimestampQuality = "-";
            debugActiveHorizontalConvention = "-";
            debugCanonicalInputMirrored =
                GetCanonicalInputHorizontallyMirrored();
            debugHorizontalTransformApplied = false;
            debugTimebaseResetCount = 0;
        }
        else
        {
            debugActiveTimebase =
                metadata.sourceTimebase.ToString();
            debugActiveTimestampQuality =
                metadata.timestampQuality.ToString();
            debugActiveHorizontalConvention =
                metadata.sourceHorizontalConvention.ToString();
            debugCanonicalInputMirrored =
                metadata.canonicalInputHorizontallyMirrored;
            debugHorizontalTransformApplied =
                metadata.horizontalTransformApplied;
            debugTimebaseResetCount =
                metadata.timebaseResetCount;
        }

        debugArrivalFallbackCount =
            _arrivalFallbackCount;
        debugHorizontalTransformCount =
            _horizontalTransformCount;
    }

    private bool TryCalculateJawNeutralRigidAnchor(
        FacePrecisionTrackingData data,
        out Vector2 anchor)
    {
        anchor =
            data.faceCenter;

        float eyeWeight =
            IsUsablePoint(data.eyeCenter)
                ? Mathf.Max(0f, rigidAnchorEyeWeight)
                : 0f;

        float cheekWeight =
            IsUsablePoint(data.cheekCenter)
                ? Mathf.Max(0f, rigidAnchorCheekWeight)
                : 0f;

        float foreheadWeight =
            IsUsablePoint(data.forehead)
                ? Mathf.Max(0f, rigidAnchorForeheadWeight)
                : 0f;

        float total =
            eyeWeight +
            cheekWeight +
            foreheadWeight;

        if (total <= 0.0001f)
        {
            return false;
        }

        Vector2 sum =
            Vector2.zero;

        if (eyeWeight > 0f)
        {
            sum +=
                data.eyeCenter *
                eyeWeight;
        }

        if (cheekWeight > 0f)
        {
            sum +=
                data.cheekCenter *
                cheekWeight;
        }

        if (foreheadWeight > 0f)
        {
            sum +=
                data.forehead *
                foreheadWeight;
        }

        anchor =
            sum /
            total;

        return
            IsUsablePoint(anchor);
    }

    private void CaptureResumeReferenceIfNeeded()
    {
        if (
            _hasResumeReference ||
            !_hasPublished ||
            !_latestPublished.isValid ||
            string.IsNullOrEmpty(
                _lastPublishedProviderId)
        )
        {
            return;
        }

        _resumeReference =
            _latestPublished;

        _resumeReferenceProviderId =
            _lastPublishedProviderId;

        _resumeGapStartedRealtime =
            Time.realtimeSinceStartupAsDouble;

        _hasResumeReference =
            true;

        UpdateResumeDiagnostics();
    }

    private bool TryGetResumeReference(
        out FacePrecisionTrackingData reference,
        out string providerId)
    {
        reference =
            default;

        providerId =
            string.Empty;

        if (!_hasResumeReference)
        {
            return false;
        }

        double gapSeconds =
            Time.realtimeSinceStartupAsDouble -
            _resumeGapStartedRealtime;

        if (
            gapSeconds < 0.0 ||
            gapSeconds >
                Mathf.Max(
                    0.01f,
                    resumeHandoffMaximumGapSeconds)
        )
        {
            ClearResumeReference();
            return false;
        }

        reference =
            _resumeReference;

        providerId =
            _resumeReferenceProviderId;

        return
            reference.isValid;
    }

    private void ExpireResumeReferenceIfNeeded()
    {
        if (!_hasResumeReference)
        {
            return;
        }

        if (
            Time.realtimeSinceStartupAsDouble -
                _resumeGapStartedRealtime >
            Mathf.Max(
                0.01f,
                resumeHandoffMaximumGapSeconds)
        )
        {
            ClearResumeReference();
        }
        else
        {
            UpdateResumeDiagnostics();
        }
    }

    private void ClearResumeReference()
    {
        _hasResumeReference =
            false;

        _resumeReference =
            default;

        _resumeReferenceProviderId =
            string.Empty;

        _resumeGapStartedRealtime =
            0.0;

        UpdateResumeDiagnostics();
    }

    private void UpdateResumeDiagnostics()
    {
        debugResumeReferenceValid =
            _hasResumeReference;

        debugResumeGapMs =
            _hasResumeReference
                ? Mathf.Max(
                    0f,
                    (float)(
                        Time.realtimeSinceStartupAsDouble -
                        _resumeGapStartedRealtime) * 1000f)
                : 0f;

        debugResumeHandoffCount =
            _resumeHandoffCount;
    }

    private void BeginProviderHandoff(
        Candidate selected)
    {
        ResetHandoff(false);

        if (
            !enableProviderHandoffNormalization ||
            !selected.valid ||
            selected.slot == null
        )
        {
            return;
        }

        FacePrecisionTrackingData reference =
            default;

        string referenceProviderId =
            string.Empty;

        bool resumeFromGap =
            TryGetResumeReference(
                out reference,
                out referenceProviderId);

        if (!resumeFromGap)
        {
            if (
                !_hasPublished ||
                !_latestPublished.isValid ||
                string.IsNullOrEmpty(
                    _lastPublishedProviderId) ||
                string.Equals(
                    _lastPublishedProviderId,
                    selected.slot.id,
                    StringComparison.Ordinal)
            )
            {
                return;
            }

            float previousAge =
                CalculateTrackingDataAgeSeconds(
                    _latestPublished);

            if (
                previousAge >
                    Mathf.Max(
                        0.01f,
                        handoffReferenceMaximumAge)
            )
            {
                return;
            }

            reference =
                _latestPublished;

            referenceProviderId =
                _lastPublishedProviderId;
        }

        float incomingAge =
            CalculateTrackingDataAgeSeconds(
                selected.slot.data);

        if (
            incomingAge >
                Mathf.Max(
                    0.01f,
                    handoffReferenceMaximumAge)
        )
        {
            return;
        }

        FacePrecisionTrackingData incoming =
            selected.slot.data;

        Vector2 centerOffset =
            reference.faceCenter -
            incoming.faceCenter;

        float maximumCenter =
            Mathf.Max(
                0f,
                handoffMaximumCenterOffset);

        if (
            maximumCenter > 0f &&
            centerOffset.magnitude >
                maximumCenter
        )
        {
            centerOffset =
                centerOffset.normalized *
                maximumCenter;
        }

        Quaternion rotationOffset =
            reference.faceRotation *
            Quaternion.Inverse(
                incoming.faceRotation);

        rotationOffset =
            NormalizeQuaternionSafe(
                rotationOffset);

        float rotationAngle =
            Quaternion.Angle(
                Quaternion.identity,
                rotationOffset);

        float maximumRotation =
            Mathf.Max(
                0f,
                handoffMaximumRotationOffsetDegrees);

        if (
            maximumRotation > 0f &&
            rotationAngle > maximumRotation &&
            rotationAngle > 0.0001f
        )
        {
            rotationOffset =
                Quaternion.Slerp(
                    Quaternion.identity,
                    rotationOffset,
                    maximumRotation /
                    rotationAngle);
        }

        float scaleRatio =
            1f;

        if (
            reference.eyeSpan2D >
                0.0001f &&
            incoming.eyeSpan2D >
                0.0001f
        )
        {
            scaleRatio =
                reference.eyeSpan2D /
                incoming.eyeSpan2D;
        }

        scaleRatio =
            Mathf.Clamp(
                scaleRatio,
                Mathf.Min(
                    handoffMinimumScaleRatio,
                    handoffMaximumScaleRatio),
                Mathf.Max(
                    handoffMinimumScaleRatio,
                    handoffMaximumScaleRatio));

        _handoffActive =
            true;

        _handoffProviderId =
            selected.slot.id;

        _handoffStartRaw =
            incoming;

        _handoffCenterOffset =
            centerOffset;

        _handoffRotationOffset =
            rotationOffset;

        _handoffScaleRatio =
            scaleRatio;

        _handoffWeight =
            1f;

        _handoffIsResume =
            resumeFromGap;

        _resumeReleaseFrameCount =
            0;

        _handoffCount++;

        if (resumeFromGap)
        {
            _resumeHandoffCount++;
        }

        ClearResumeReference();
        UpdateHandoffDiagnostics();
    }

    private void ApplyProviderHandoff(
        string providerId,
        ref FacePrecisionTrackingData data)
    {
        if (
            !_handoffActive ||
            !enableProviderHandoffNormalization ||
            !string.Equals(
                providerId,
                _handoffProviderId,
                StringComparison.Ordinal)
        )
        {
            if (
                _handoffActive &&
                !string.Equals(
                    providerId,
                    _handoffProviderId,
                    StringComparison.Ordinal)
            )
            {
                ResetHandoff(false);
            }

            return;
        }

        float translationProgress =
            Vector2.Distance(
                data.faceCenter,
                _handoffStartRaw.faceCenter) /
            Mathf.Max(
                0.0001f,
                _handoffStartRaw.eyeSpan2D) /
            Mathf.Max(
                0.01f,
                handoffReleaseTranslationEyeSpans);

        float rotationProgress =
            Quaternion.Angle(
                _handoffStartRaw.faceRotation,
                data.faceRotation) /
            Mathf.Max(
                0.1f,
                handoffReleaseRotationDegrees);

        float scaleFraction =
            0f;

        if (
            _handoffStartRaw.eyeSpan2D >
                0.0001f &&
            data.eyeSpan2D >
                0.0001f
        )
        {
            scaleFraction =
                Mathf.Abs(
                    data.eyeSpan2D /
                    _handoffStartRaw.eyeSpan2D -
                    1f);
        }

        float scaleProgress =
            scaleFraction /
            Mathf.Max(
                0.001f,
                handoffReleaseScaleFraction);

        float releaseProgress =
            Mathf.Clamp01(
                Mathf.Max(
                    translationProgress,
                    rotationProgress,
                    scaleProgress));

        if (_handoffIsResume)
        {
            // KIWI_V4_9_RESUME_BOUNDED_RELEASE
            // A same/cross-provider resume reference is presentation-only. If
            // the user genuinely moved while tracking was briefly unavailable,
            // a purely motion-relative release can otherwise remain at weight
            // 1 forever once the user becomes still. Keep the first resumed
            // sample fully aligned, then release over a tiny bounded number of
            // *fresh provider samples* (not render frames or wall-clock time).
            float sampleReleaseProgress =
                Mathf.Clamp01(
                    _resumeReleaseFrameCount /
                    (float)Mathf.Max(
                        1,
                        resumeHandoffReleaseFrames));

            releaseProgress =
                Mathf.Max(
                    releaseProgress,
                    sampleReleaseProgress);

            _resumeReleaseFrameCount++;
        }

        // KIWI_V5_1_PHASE16_5_COMMERCIAL_HANDOFF_RELEASE
        // The raw motion estimate decides *whether* the temporary coordinate-basis
        // alignment may release. The release itself is monotonic and slew-limited
        // per accepted provider sample. This is intentionally not a temporal pose
        // filter: normal same-provider Landmarker samples remain untouched.
        float targetHandoffWeight =
            1f -
            Smooth01(
                releaseProgress);

        targetHandoffWeight =
            Mathf.Min(
                _handoffWeight,
                targetHandoffWeight);

        float maximumReleasePerSample =
            Mathf.Clamp(
                handoffMaximumWeightReleasePerSample,
                0.05f,
                1f);

        if (_handoffIsResume)
        {
            maximumReleasePerSample =
                Mathf.Max(
                    maximumReleasePerSample,
                    1f /
                    Mathf.Max(
                        1,
                        resumeHandoffReleaseFrames));
        }

        float previousHandoffWeight =
            _handoffWeight;

        _handoffWeight =
            Mathf.MoveTowards(
                _handoffWeight,
                targetHandoffWeight,
                maximumReleasePerSample);

        debugHandoffTargetWeight =
            targetHandoffWeight;

        debugHandoffReleaseStep =
            Mathf.Max(
                0f,
                previousHandoffWeight -
                _handoffWeight);

        if (_handoffWeight <= 0.0001f)
        {
            ResetHandoff(true);
            return;
        }

        Vector2 rawCenter =
            data.faceCenter;

        Vector2 alignedCenter =
            rawCenter +
            _handoffCenterOffset *
            _handoffWeight;

        float scale =
            Mathf.Lerp(
                1f,
                _handoffScaleRatio,
                _handoffWeight);

        data.rightEyeCenter =
            AlignPoint(
                data.rightEyeCenter,
                rawCenter,
                alignedCenter,
                scale);

        data.leftEyeCenter =
            AlignPoint(
                data.leftEyeCenter,
                rawCenter,
                alignedCenter,
                scale);

        data.eyeCenter =
            AlignPoint(
                data.eyeCenter,
                rawCenter,
                alignedCenter,
                scale);

        data.chin =
            AlignPoint(
                data.chin,
                rawCenter,
                alignedCenter,
                scale);

        data.nose =
            AlignPoint(
                data.nose,
                rawCenter,
                alignedCenter,
                scale);

        data.cheekCenter =
            AlignPoint(
                data.cheekCenter,
                rawCenter,
                alignedCenter,
                scale);

        data.forehead =
            AlignPoint(
                data.forehead,
                rawCenter,
                alignedCenter,
                scale);

        data.faceCenter =
            alignedCenter;

        data.eyeSpan2D *=
            scale;

        data.eyeSpan3D *=
            scale;

        data.faceWidth2D *=
            scale;

        data.faceHeight2D *=
            scale;

        Quaternion appliedOffset =
            Quaternion.Slerp(
                Quaternion.identity,
                _handoffRotationOffset,
                _handoffWeight);

        data.faceRotation =
            NormalizeQuaternionSafe(
                appliedOffset *
                data.faceRotation);

        UpdateHandoffDiagnostics();
    }

    private static Vector2 AlignPoint(
        Vector2 point,
        Vector2 rawCenter,
        Vector2 alignedCenter,
        float scale)
    {
        if (!IsUsablePoint(point))
        {
            return point;
        }

        return
            alignedCenter +
            (
                point -
                rawCenter
            ) *
            scale;
    }

    private void ResetHandoff(
        bool releasedByMotion)
    {
        _handoffActive =
            false;

        _handoffProviderId =
            string.Empty;

        _handoffStartRaw =
            default;

        _handoffCenterOffset =
            Vector2.zero;

        _handoffRotationOffset =
            Quaternion.identity;

        _handoffScaleRatio =
            1f;

        _handoffWeight =
            0f;

        _handoffIsResume =
            false;

        _resumeReleaseFrameCount =
            0;

        debugHandoffActive =
            false;

        debugHandoffProvider =
            releasedByMotion
                ? "released"
                : "-";

        debugHandoffWeight =
            0f;

        debugHandoffTargetWeight =
            0f;

        debugHandoffReleaseStep =
            0f;

        debugHandoffCenterOffset =
            0f;

        debugHandoffRotationOffsetDegrees =
            0f;

        debugHandoffScaleRatio =
            1f;

        debugHandoffCount =
            _handoffCount;
    }

    private void UpdateHandoffDiagnostics()
    {
        debugHandoffActive =
            _handoffActive;

        debugHandoffProvider =
            string.IsNullOrEmpty(
                _handoffProviderId)
                ? "-"
                : _handoffProviderId;

        debugHandoffWeight =
            _handoffWeight;

        if (!_handoffActive)
        {
            debugHandoffTargetWeight = 0f;
            debugHandoffReleaseStep = 0f;
        }

        debugHandoffCenterOffset =
            _handoffCenterOffset.magnitude;

        debugHandoffRotationOffsetDegrees =
            Quaternion.Angle(
                Quaternion.identity,
                _handoffRotationOffset);

        debugHandoffScaleRatio =
            _handoffScaleRatio;

        debugHandoffCount =
            _handoffCount;
    }

    private static bool IsUsablePoint(
        Vector2 point)
    {
        return
            !float.IsNaN(point.x) &&
            !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) &&
            !float.IsInfinity(point.y) &&
            point != Vector2.zero;
    }

    private static Quaternion NormalizeQuaternionSafe(
        Quaternion value)
    {
        float magnitude =
            Mathf.Sqrt(
                value.x * value.x +
                value.y * value.y +
                value.z * value.z +
                value.w * value.w);

        if (
            magnitude <= 0.000001f ||
            float.IsNaN(magnitude) ||
            float.IsInfinity(magnitude)
        )
        {
            return
                Quaternion.identity;
        }

        float inverse =
            1f /
            magnitude;

        return
            new Quaternion(
                value.x * inverse,
                value.y * inverse,
                value.z * inverse,
                value.w * inverse);
    }

    private static float Smooth01(
        float value)
    {
        value =
            Mathf.Clamp01(
                value);

        return
            value *
            value *
            (3f - 2f * value);
    }

    private void RemoveLongStaleExternalProviders()
    {
        if (_external.Count == 0)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        double staleSeconds =
            Mathf.Max(
                1f,
                maximumProviderFrameAge *
                4f);

        List<string> remove =
            null;

        foreach (
            KeyValuePair<string, ProviderSlot> pair
            in _external)
        {
            if (
                now -
                pair.Value.submittedRealtime >
                staleSeconds
            )
            {
                if (remove == null)
                {
                    remove =
                        new List<string>();
                }

                remove.Add(
                    pair.Key);
            }
        }

        if (remove == null)
        {
            return;
        }

        for (
            int i = 0;
            i < remove.Count;
            i++
        )
        {
            _external.Remove(
                remove[i]);
        }
    }

    private void ClearSwitchCandidate()
    {
        _switchCandidateId =
            string.Empty;

        _switchCandidateSourceFrameId =
            0UL;

        _switchCandidateCount =
            0;

        debugSwitchCandidate =
            "-";

        debugSwitchCandidateFrames =
            0;
    }

    private static void ClearSlot(
        ProviderSlot slot)
    {
        if (slot == null)
        {
            return;
        }

        slot.data =
            default;

        slot.sourceFrameId =
            0UL;

        slot.syntheticFrameId =
            0UL;

        slot.metadata =
            default;

        slot.lastProviderSourceFrameId =
            0UL;

        slot.normalizedExternalFrameId =
            0UL;

        slot.hasExternalFrameIdentity =
            false;

        slot.arrivalHostTicks =
            0L;

        slot.submittedRealtime =
            0.0;

        slot.frameIntervalEma =
            0f;

        slot.frameIntervalDeviationEma =
            0f;

        slot.hasTimebaseAnchor =
            false;

        slot.mappedTimebase =
            KiwiTrackingTimebase.LegacyUnspecified;

        slot.timebaseSourceAnchor =
            0L;

        slot.timebaseHostAnchor =
            0L;

        slot.lastProviderSourceTimestamp =
            0L;

        slot.lastNormalizedObservationHostTicks =
            0L;

        slot.lastCanonicalTimestampMilliseconds =
            0L;

        slot.timebaseResetCount =
            0;

        slot.hasFrame =
            false;
    }
}
