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
    }

    private sealed class ProviderSlot
    {
        public string id;
        public int priority;
        public TrackingCapability capabilities;
        public FacePrecisionTrackingData data;
        public ulong sourceFrameId;
        public ulong syntheticFrameId;
        public long arrivalHostTicks;
        public double submittedRealtime;
        public float frameIntervalEma;
        public float frameIntervalDeviationEma;
        public float rigidAnchorCorrectionMagnitude;
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
    [SerializeField] private float debugHandoffCenterOffset;
    [SerializeField] private float debugHandoffRotationOffsetDegrees;
    [SerializeField] private float debugHandoffScaleRatio = 1f;
    [SerializeField] private int debugHandoffCount;
    [SerializeField] private bool debugResumeReferenceValid;
    [SerializeField] private float debugResumeGapMs;
    [SerializeField] private int debugResumeHandoffCount;

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
    private bool _hasPublished;

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
        _runner = null;
        _activeProviderId = string.Empty;
        _activeProviderSinceRealtime = 0.0;
        _lastObservedRunnerFrameId = 0UL;
        ClearSwitchCandidate();

        // Do not let a prior scene's cached Runner frame survive a scene change.
        ClearSlot(_mediaPipe);
        ClearSlot(_inference);

        _latestPublished = default;
        _hasPublished = false;
        _lastPublishedProviderId = string.Empty;
        _lastPublishedSourceFrameId = 0UL;
        ResetHandoff(false);
        ClearResumeReference();

        RefreshRunner();
    }

    private void Update()
    {
        RefreshRunner();
        ObserveBuiltInRunner();

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

        if (active.valid)
        {
            selected = active;

            bool holdSatisfied =
                _activeProviderSinceRealtime <=
                    0.0 ||
                nowRealtime -
                    _activeProviderSinceRealtime >=
                    minimumProviderHoldSeconds;

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

                if (
                    _switchCandidateCount >=
                    Mathf.Max(
                        1,
                        providerSwitchConfirmationFrames)
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
    /// Submit one normalized external provider frame.
    ///
    /// Provider-specific SDK code belongs in an adapter. The avatar motion
    /// system only consumes the normalized frame selected by this hub.
    /// </summary>
    public void SubmitExternalFrame(
        string providerId,
        int priority,
        TrackingCapability capabilities,
        FacePrecisionTrackingData data)
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

        ulong sourceFrameId =
            data.frameId;

        if (sourceFrameId == 0UL)
        {
            slot.syntheticFrameId++;

            if (slot.syntheticFrameId == 0UL)
            {
                slot.syntheticFrameId++;
            }

            sourceFrameId =
                slot.syntheticFrameId;
        }

        UpdateSlot(
            slot,
            data,
            sourceFrameId,
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
        }
    }

    public bool TryGetLatestFrame(
        out FacePrecisionTrackingData data,
        out string providerId)
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
            return false;
        }

        data =
            _latestPublished;

        providerId =
            _activeProviderId;

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
                    data.backend
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

        UpdateSlot(
            slot,
            data,
            data.frameId,
            Time.realtimeSinceStartupAsDouble);
    }

    private void UpdateSlot(
        ProviderSlot slot,
        FacePrecisionTrackingData data,
        ulong sourceFrameId,
        double submittedRealtime)
    {
        if (slot == null)
        {
            return;
        }

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

        slot.sourceFrameId =
            sourceFrameId;

        slot.arrivalHostTicks =
            arrivalTicks;

        slot.submittedRealtime =
            submittedRealtime;

        slot.hasFrame =
            true;
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

        _hubFrameId++;

        if (_hubFrameId == 0UL)
        {
            _hubFrameId++;
        }

        output.frameId =
            _hubFrameId;

        _latestPublished =
            output;

        _hasPublished =
            true;

        _lastPublishedProviderId =
            selected.slot.id;

        _lastPublishedSourceFrameId =
            selected.slot.sourceFrameId;
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

        _handoffWeight =
            1f -
            Smooth01(
                releaseProgress);

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

        slot.arrivalHostTicks =
            0L;

        slot.submittedRealtime =
            0.0;

        slot.frameIntervalEma =
            0f;

        slot.frameIntervalDeviationEma =
            0f;

        slot.hasFrame =
            false;
    }
}
