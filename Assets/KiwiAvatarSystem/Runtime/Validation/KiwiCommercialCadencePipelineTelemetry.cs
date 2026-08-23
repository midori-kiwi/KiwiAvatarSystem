using System.Diagnostics;
using System.Reflection;
using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Phase 16.14 observer-only stable-lane / freshness pipeline telemetry.
///
/// Commercial tracking separates camera acquisition, auxiliary MediaPipe
/// submission/result cadence, high-rate GPU inference pressure, canonical
/// adoption and final presentation. This observer exposes those stages without
/// becoming another tracking/presentation owner.
/// </summary>
[DefaultExecutionOrder(36450)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialCadencePipelineTelemetry : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_9_COMMERCIAL_CADENCE_PIPELINE_TELEMETRY
    // KIWI_V5_1_PHASE16_10_INFERENCE_PIPELINE_TELEMETRY
    // KIWI_V5_1_PHASE16_11_FRESHNESS_AGE_TELEMETRY
    // KIWI_V5_1_PHASE16_12_PERSISTENT_ROI_TELEMETRY
    // KIWI_V5_1_PHASE16_13_LATENCY_FIRST_TELEMETRY
    // KIWI_V5_1_PHASE16_14_STABLE_PIPELINE_TELEMETRY
    private const string RuntimeObjectName =
        "[Kiwi] Commercial Cadence Pipeline Telemetry";

    private static KiwiCommercialCadencePipelineTelemetry _instance;

    private FaceLandmarkerRunner _runner;
    private KiwiInferenceFaceTracker _inferenceTracker;
    private FieldInfo _inferenceTrackerField;
    private float _nextTrackerRefreshTime;

    private ulong _lastCanonicalFrameId;
    private long _lastCanonicalObservationTicks;
    private long _lastAdoptionHostTicks;
    private float _adoptionRateHz;
    private int _adoptionCount;

    private float _freshSourceRateHz;
    private float _submissionRateHz;
    private float _resultRateHz;
    private float _readbackLatencyMs;
    private float _sourceToSubmissionGapHz;
    private float _submissionToResultGapHz;
    private float _resultToAdoptionGapHz;
    private float _submissionEfficiency;
    private float _resultEfficiency;
    private float _adoptionEfficiency;

    private int _inferencePipelineDepth;
    private int _inferenceLaneLimit;
    private int _inferenceActiveLanes;
    private float _inferenceOldestPendingAgeMs;
    private float _inferenceLatencyMs;
    private float _inferenceScheduleDelayMs;
    private float _inferenceSourceToCompletionAgeMs;
    private float _inferenceAcceptedSourceAgeMs;

    // Phase 16.12 persistent ROI / presence / stage diagnostics.
    private float _inferenceRawPresenceLogit;
    private float _inferencePresence;
    private int _inferenceConsecutiveFailures;
    private bool _inferenceHasRegion;
    private bool _inferenceTrackingHealthy;
    private bool _inferenceRegionRetentionActive;
    private float _inferenceRegionTrustedAgeMs;
    private float _inferenceRegionGraceRemainingMs;
    private float _inferenceRegionRecoveryScale;
    private int _inferenceRegionRetainedFailureCount;
    private int _inferenceRegionReleaseCount;
    private float _inferenceRegionCenterX;
    private float _inferenceRegionCenterY;
    private float _inferenceRegionWidth;
    private float _inferenceRegionHeight;
    private float _inferenceScheduleCpuMs;
    private float _inferenceGpuReadbackWaitMs;
    private float _inferenceDecodeCpuMs;
    private bool _inferenceLatencyFirstSchedulingActive;
    private bool _inferenceStableDesktopSchedulingActive;
    private float _inferenceCompletionIntervalMs;
    private int _inferenceLanePromotionCount;
    private int _inferenceLaneDemotionCount;
    private int _inferenceSingleFlightProbeCount;

    private int _inferenceScheduledCount;
    private int _inferenceReadbackCompletedCount;
    private int _inferenceCompletedCount;
    private int _inferenceDroppedFreshCount;
    private int _inferenceRejectedPresenceCount;
    private int _inferenceRejectedInvalidCount;
    private int _inferenceDiscardedStaleCount;
    private int _inferenceDiscardedCrossSystemCount;
    private int _inferenceDiscardedStaleAnchorCount;
    private int _inferenceDiscardedStaleGenerationCount;
    private int _inferenceDiscardedStaleSourceCount;
    private int _inferenceSoftAnchorUpdateCount;
    private int _inferenceHardAnchorInvalidationCount;
    private int _inferenceSoftAnchorSupersededRoiUpdateCount;
    private float _inferenceDropRatio;
    private float _inferenceCompletionRatio;

    public static bool IsOperational =>
        _instance != null && _instance._runner != null;

    public static bool InferenceTelemetryOperational =>
        _instance != null && _instance._inferenceTracker != null;

    public static float FreshSourceRateHz =>
        _instance != null ? _instance._freshSourceRateHz : 0f;

    public static float SubmissionRateHz =>
        _instance != null ? _instance._submissionRateHz : 0f;

    public static float ResultRateHz =>
        _instance != null ? _instance._resultRateHz : 0f;

    public static float CanonicalAdoptionRateHz =>
        _instance != null ? _instance._adoptionRateHz : 0f;

    public static float ReadbackLatencyMs =>
        _instance != null ? _instance._readbackLatencyMs : 0f;

    public static float SourceToSubmissionGapHz =>
        _instance != null ? _instance._sourceToSubmissionGapHz : 0f;

    public static float SubmissionToResultGapHz =>
        _instance != null ? _instance._submissionToResultGapHz : 0f;

    public static float ResultToAdoptionGapHz =>
        _instance != null ? _instance._resultToAdoptionGapHz : 0f;

    public static float SubmissionEfficiency =>
        _instance != null ? _instance._submissionEfficiency : 0f;

    public static float ResultEfficiency =>
        _instance != null ? _instance._resultEfficiency : 0f;

    public static float AdoptionEfficiency =>
        _instance != null ? _instance._adoptionEfficiency : 0f;

    public static int AdoptionCount =>
        _instance != null ? _instance._adoptionCount : 0;

    public static int InferencePipelineDepth =>
        _instance != null ? _instance._inferencePipelineDepth : 0;

    public static int InferenceLaneLimit =>
        _instance != null ? _instance._inferenceLaneLimit : 0;

    public static int InferenceActiveLanes =>
        _instance != null ? _instance._inferenceActiveLanes : 0;

    public static float InferenceOldestPendingAgeMs =>
        _instance != null ? _instance._inferenceOldestPendingAgeMs : 0f;

    public static float InferenceLatencyMs =>
        _instance != null ? _instance._inferenceLatencyMs : 0f;

    public static float InferenceScheduleDelayMs =>
        _instance != null ? _instance._inferenceScheduleDelayMs : 0f;

    public static float InferenceSourceToCompletionAgeMs =>
        _instance != null ? _instance._inferenceSourceToCompletionAgeMs : 0f;

    public static float InferenceAcceptedSourceAgeMs =>
        _instance != null ? _instance._inferenceAcceptedSourceAgeMs : 0f;

    public static float InferenceRawPresenceLogit =>
        _instance != null ? _instance._inferenceRawPresenceLogit : 0f;

    public static float InferencePresence =>
        _instance != null ? _instance._inferencePresence : 0f;

    public static int InferenceConsecutiveFailures =>
        _instance != null ? _instance._inferenceConsecutiveFailures : 0;

    public static bool InferenceHasRegion =>
        _instance != null && _instance._inferenceHasRegion;

    public static bool InferenceTrackingHealthy =>
        _instance != null && _instance._inferenceTrackingHealthy;

    public static bool InferenceRegionRetentionActive =>
        _instance != null && _instance._inferenceRegionRetentionActive;

    public static float InferenceRegionTrustedAgeMs =>
        _instance != null ? _instance._inferenceRegionTrustedAgeMs : 0f;

    public static float InferenceRegionGraceRemainingMs =>
        _instance != null ? _instance._inferenceRegionGraceRemainingMs : 0f;

    public static float InferenceRegionRecoveryScale =>
        _instance != null ? _instance._inferenceRegionRecoveryScale : 0f;

    public static int InferenceRegionRetainedFailureCount =>
        _instance != null ? _instance._inferenceRegionRetainedFailureCount : 0;

    public static int InferenceRegionReleaseCount =>
        _instance != null ? _instance._inferenceRegionReleaseCount : 0;

    public static float InferenceRegionCenterX =>
        _instance != null ? _instance._inferenceRegionCenterX : 0f;

    public static float InferenceRegionCenterY =>
        _instance != null ? _instance._inferenceRegionCenterY : 0f;

    public static float InferenceRegionWidth =>
        _instance != null ? _instance._inferenceRegionWidth : 0f;

    public static float InferenceRegionHeight =>
        _instance != null ? _instance._inferenceRegionHeight : 0f;

    public static float InferenceScheduleCpuMs =>
        _instance != null ? _instance._inferenceScheduleCpuMs : 0f;

    public static float InferenceGpuReadbackWaitMs =>
        _instance != null ? _instance._inferenceGpuReadbackWaitMs : 0f;

    public static float InferenceDecodeCpuMs =>
        _instance != null ? _instance._inferenceDecodeCpuMs : 0f;

    public static bool InferenceLatencyFirstSchedulingActive =>
        _instance != null && _instance._inferenceLatencyFirstSchedulingActive;

    public static bool InferenceStableDesktopSchedulingActive =>
        _instance != null && _instance._inferenceStableDesktopSchedulingActive;

    public static float InferenceCompletionIntervalMs =>
        _instance != null ? _instance._inferenceCompletionIntervalMs : 0f;

    public static int InferenceLanePromotionCount =>
        _instance != null ? _instance._inferenceLanePromotionCount : 0;

    public static int InferenceLaneDemotionCount =>
        _instance != null ? _instance._inferenceLaneDemotionCount : 0;

    public static int InferenceSingleFlightProbeCount =>
        _instance != null ? _instance._inferenceSingleFlightProbeCount : 0;

    public static int InferenceScheduledCount =>
        _instance != null ? _instance._inferenceScheduledCount : 0;

    public static int InferenceReadbackCompletedCount =>
        _instance != null ? _instance._inferenceReadbackCompletedCount : 0;

    public static int InferenceCompletedCount =>
        _instance != null ? _instance._inferenceCompletedCount : 0;

    public static int InferenceDroppedFreshCount =>
        _instance != null ? _instance._inferenceDroppedFreshCount : 0;

    public static int InferenceRejectedPresenceCount =>
        _instance != null ? _instance._inferenceRejectedPresenceCount : 0;

    public static int InferenceRejectedInvalidCount =>
        _instance != null ? _instance._inferenceRejectedInvalidCount : 0;

    public static int InferenceDiscardedStaleCount =>
        _instance != null ? _instance._inferenceDiscardedStaleCount : 0;

    public static int InferenceDiscardedCrossSystemCount =>
        _instance != null ? _instance._inferenceDiscardedCrossSystemCount : 0;

    public static int InferenceDiscardedStaleAnchorCount =>
        _instance != null ? _instance._inferenceDiscardedStaleAnchorCount : 0;

    public static int InferenceDiscardedStaleGenerationCount =>
        _instance != null ? _instance._inferenceDiscardedStaleGenerationCount : 0;

    public static int InferenceDiscardedStaleSourceCount =>
        _instance != null ? _instance._inferenceDiscardedStaleSourceCount : 0;

    public static int InferenceSoftAnchorUpdateCount =>
        _instance != null ? _instance._inferenceSoftAnchorUpdateCount : 0;

    public static int InferenceHardAnchorInvalidationCount =>
        _instance != null ? _instance._inferenceHardAnchorInvalidationCount : 0;

    public static int InferenceSoftAnchorSupersededRoiUpdateCount =>
        _instance != null ? _instance._inferenceSoftAnchorSupersededRoiUpdateCount : 0;

    public static float InferenceDropRatio =>
        _instance != null ? _instance._inferenceDropRatio : 0f;

    public static float InferenceCompletionRatio =>
        _instance != null ? _instance._inferenceCompletionRatio : 0f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        EnsureInstance();
    }

    private static KiwiCommercialCadencePipelineTelemetry EnsureInstance()
    {
        if (_instance != null)
        {
            return _instance;
        }

        KiwiCommercialCadencePipelineTelemetry existing =
            FindFirstObjectByType<KiwiCommercialCadencePipelineTelemetry>(
                FindObjectsInactive.Include);

        if (existing != null)
        {
            _instance = existing;
            return existing;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<KiwiCommercialCadencePipelineTelemetry>();
        return _instance;
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        RefreshRunner();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }

    private void Update()
    {
        if (_runner == null)
        {
            RefreshRunner();
        }

        RefreshInferenceTrackerIfNeeded();
        SampleRunnerPipeline();
        SampleInferencePipeline();
        SampleCanonicalAdoption();
        RecalculateDerivedMetrics();
    }

    private void RefreshRunner()
    {
        _runner = FindFirstObjectByType<FaceLandmarkerRunner>(
            FindObjectsInactive.Include);

        _inferenceTracker = null;
        _inferenceTrackerField = null;

        if (_runner != null)
        {
            _inferenceTrackerField =
                typeof(FaceLandmarkerRunner).GetField(
                    "_sentisTracker",
                    BindingFlags.Instance | BindingFlags.NonPublic);
        }

        _nextTrackerRefreshTime = 0f;
        RefreshInferenceTrackerIfNeeded();
    }

    private void RefreshInferenceTrackerIfNeeded()
    {
        if (_runner == null || _inferenceTrackerField == null)
        {
            _inferenceTracker = null;
            return;
        }

        if (_inferenceTracker != null && Time.unscaledTime < _nextTrackerRefreshTime)
        {
            return;
        }

        _nextTrackerRefreshTime = Time.unscaledTime + 0.50f;

        object value = _inferenceTrackerField.GetValue(_runner);
        KiwiInferenceFaceTracker current = value as KiwiInferenceFaceTracker;

        if (!ReferenceEquals(current, _inferenceTracker))
        {
            _inferenceTracker = current;
        }
    }

    private void SampleRunnerPipeline()
    {
        if (_runner == null)
        {
            _freshSourceRateHz = 0f;
            _submissionRateHz = 0f;
            _resultRateHz = 0f;
            _readbackLatencyMs = 0f;
            return;
        }

        _freshSourceRateHz =
            SanitizeRate(_runner.LatestFreshSourceRateHz);
        _submissionRateHz =
            SanitizeRate(_runner.LatestSubmissionRateHz);
        _resultRateHz =
            SanitizeRate(_runner.LatestTrackingResultRateHz);
        _readbackLatencyMs =
            SanitizeNonNegative(_runner.LatestReadbackLatencyMs);
    }

    private void SampleInferencePipeline()
    {
        if (_inferenceTracker == null)
        {
            ResetInferenceMetrics();
            return;
        }

        _inferencePipelineDepth =
            Mathf.Max(0, _inferenceTracker.PipelineDepth);
        _inferenceLaneLimit =
            Mathf.Max(0, _inferenceTracker.SchedulingLaneLimit);
        _inferenceActiveLanes =
            Mathf.Max(0, _inferenceTracker.ActiveLaneCount);
        _inferenceOldestPendingAgeMs =
            SanitizeNonNegative(_inferenceTracker.OldestPendingAgeMs);
        _inferenceLatencyMs =
            SanitizeNonNegative(_inferenceTracker.LatestLatencyMs);
        _inferenceScheduleDelayMs =
            SanitizeNonNegative(_inferenceTracker.LatestScheduleDelayMs);
        _inferenceSourceToCompletionAgeMs =
            SanitizeNonNegative(_inferenceTracker.LatestSourceToCompletionAgeMs);
        _inferenceAcceptedSourceAgeMs =
            SanitizeNonNegative(_inferenceTracker.LatestAcceptedSourceAgeMs);

        _inferenceRawPresenceLogit =
            SanitizeFinite(_inferenceTracker.LatestRawPresenceLogit);
        _inferencePresence =
            Mathf.Clamp01(SanitizeFinite(_inferenceTracker.LatestPresence));
        _inferenceConsecutiveFailures =
            Mathf.Max(0, _inferenceTracker.ConsecutiveFailures);
        _inferenceHasRegion =
            _inferenceTracker.HasRegion;
        _inferenceTrackingHealthy =
            _inferenceTracker.IsTracking;
        _inferenceRegionRetentionActive =
            _inferenceTracker.RegionRetentionActive;
        _inferenceRegionTrustedAgeMs =
            SanitizeNonNegative(_inferenceTracker.RegionTrustedAgeMs);
        _inferenceRegionGraceRemainingMs =
            SanitizeNonNegative(_inferenceTracker.RegionGraceRemainingMs);
        _inferenceRegionRecoveryScale =
            Mathf.Clamp(
                SanitizeFinite(_inferenceTracker.RegionRecoveryScale),
                0f,
                4f);
        _inferenceRegionRetainedFailureCount =
            Mathf.Max(0, _inferenceTracker.RetainedRegionFailureCount);
        _inferenceRegionReleaseCount =
            Mathf.Max(0, _inferenceTracker.RegionReleaseCount);
        _inferenceRegionCenterX =
            SanitizeFinite(_inferenceTracker.RegionCenterXNormalized);
        _inferenceRegionCenterY =
            SanitizeFinite(_inferenceTracker.RegionCenterYNormalized);
        _inferenceRegionWidth =
            SanitizeNonNegative(_inferenceTracker.RegionWidthNormalized);
        _inferenceRegionHeight =
            SanitizeNonNegative(_inferenceTracker.RegionHeightNormalized);
        _inferenceScheduleCpuMs =
            SanitizeNonNegative(_inferenceTracker.LatestScheduleCpuMs);
        _inferenceGpuReadbackWaitMs =
            SanitizeNonNegative(_inferenceTracker.LatestGpuReadbackWaitMs);
        _inferenceDecodeCpuMs =
            SanitizeNonNegative(_inferenceTracker.LatestDecodeCpuMs);
        _inferenceLatencyFirstSchedulingActive =
            _inferenceTracker.LatencyFirstSchedulingActive;
        _inferenceStableDesktopSchedulingActive =
            _inferenceTracker.StableDesktopSchedulingActive;
        _inferenceCompletionIntervalMs =
            SanitizeNonNegative(_inferenceTracker.LatestCompletionIntervalMs);
        _inferenceLanePromotionCount =
            Mathf.Max(0, _inferenceTracker.LatencyFirstLanePromotionCount);
        _inferenceLaneDemotionCount =
            Mathf.Max(0, _inferenceTracker.LatencyFirstLaneDemotionCount);
        _inferenceSingleFlightProbeCount =
            Mathf.Max(0, _inferenceTracker.SingleFlightProbeCount);

        _inferenceScheduledCount =
            Mathf.Max(0, _inferenceTracker.ScheduledFrameCount);
        _inferenceReadbackCompletedCount =
            Mathf.Max(0, _inferenceTracker.ReadbackCompletedFrameCount);
        _inferenceCompletedCount =
            Mathf.Max(0, _inferenceTracker.CompletedFrameCount);
        _inferenceDroppedFreshCount =
            Mathf.Max(0, _inferenceTracker.DroppedFreshFrameCount);
        _inferenceRejectedPresenceCount =
            Mathf.Max(0, _inferenceTracker.RejectedPresenceFrameCount);
        _inferenceRejectedInvalidCount =
            Mathf.Max(0, _inferenceTracker.RejectedInvalidFrameCount);
        _inferenceDiscardedStaleCount =
            Mathf.Max(0, _inferenceTracker.DiscardedStaleFrameCount);
        _inferenceDiscardedCrossSystemCount =
            Mathf.Max(0, _inferenceTracker.DiscardedCrossSystemFrameCount);
        _inferenceDiscardedStaleAnchorCount =
            Mathf.Max(0, _inferenceTracker.DiscardedStaleAnchorFrameCount);
        _inferenceDiscardedStaleGenerationCount =
            Mathf.Max(0, _inferenceTracker.DiscardedStaleGenerationFrameCount);
        _inferenceDiscardedStaleSourceCount =
            Mathf.Max(0, _inferenceTracker.DiscardedStaleSourceFrameCount);
        _inferenceSoftAnchorUpdateCount =
            Mathf.Max(0, _inferenceTracker.SoftExternalAnchorUpdateCount);
        _inferenceHardAnchorInvalidationCount =
            Mathf.Max(0, _inferenceTracker.HardExternalAnchorInvalidationCount);
        _inferenceSoftAnchorSupersededRoiUpdateCount =
            Mathf.Max(0, _inferenceTracker.SoftAnchorSupersededRoiUpdateCount);

        int offered =
            _inferenceScheduledCount +
            _inferenceDroppedFreshCount;

        _inferenceDropRatio =
            offered > 0
                ? Mathf.Clamp01(
                    _inferenceDroppedFreshCount /
                    (float)offered)
                : 0f;

        _inferenceCompletionRatio =
            _inferenceScheduledCount > 0
                ? Mathf.Clamp01(
                    _inferenceCompletedCount /
                    (float)_inferenceScheduledCount)
                : 0f;
    }

    private void ResetInferenceMetrics()
    {
        _inferencePipelineDepth = 0;
        _inferenceLaneLimit = 0;
        _inferenceActiveLanes = 0;
        _inferenceOldestPendingAgeMs = 0f;
        _inferenceLatencyMs = 0f;
        _inferenceScheduleDelayMs = 0f;
        _inferenceSourceToCompletionAgeMs = 0f;
        _inferenceAcceptedSourceAgeMs = 0f;
        _inferenceRawPresenceLogit = 0f;
        _inferencePresence = 0f;
        _inferenceConsecutiveFailures = 0;
        _inferenceHasRegion = false;
        _inferenceTrackingHealthy = false;
        _inferenceRegionRetentionActive = false;
        _inferenceRegionTrustedAgeMs = 0f;
        _inferenceRegionGraceRemainingMs = 0f;
        _inferenceRegionRecoveryScale = 0f;
        _inferenceRegionRetainedFailureCount = 0;
        _inferenceRegionReleaseCount = 0;
        _inferenceRegionCenterX = 0f;
        _inferenceRegionCenterY = 0f;
        _inferenceRegionWidth = 0f;
        _inferenceRegionHeight = 0f;
        _inferenceScheduleCpuMs = 0f;
        _inferenceGpuReadbackWaitMs = 0f;
        _inferenceDecodeCpuMs = 0f;
        _inferenceLatencyFirstSchedulingActive = false;
        _inferenceStableDesktopSchedulingActive = false;
        _inferenceCompletionIntervalMs = 0f;
        _inferenceLanePromotionCount = 0;
        _inferenceLaneDemotionCount = 0;
        _inferenceSingleFlightProbeCount = 0;
        _inferenceScheduledCount = 0;
        _inferenceReadbackCompletedCount = 0;
        _inferenceCompletedCount = 0;
        _inferenceDroppedFreshCount = 0;
        _inferenceRejectedPresenceCount = 0;
        _inferenceRejectedInvalidCount = 0;
        _inferenceDiscardedStaleCount = 0;
        _inferenceDiscardedCrossSystemCount = 0;
        _inferenceDiscardedStaleAnchorCount = 0;
        _inferenceDiscardedStaleGenerationCount = 0;
        _inferenceDiscardedStaleSourceCount = 0;
        _inferenceSoftAnchorUpdateCount = 0;
        _inferenceHardAnchorInvalidationCount = 0;
        _inferenceSoftAnchorSupersededRoiUpdateCount = 0;
        _inferenceDropRatio = 0f;
        _inferenceCompletionRatio = 0f;
    }

    private void SampleCanonicalAdoption()
    {
        if (
            !KiwiCanonicalTrackingFrame.TryGetFrame(
                out KiwiTrackingFrame frame) ||
            !frame.isValid ||
            frame.canonicalFrameId == 0UL ||
            frame.canonicalFrameId == _lastCanonicalFrameId)
        {
            return;
        }

        _lastCanonicalFrameId = frame.canonicalFrameId;

        long observationTicks =
            frame.normalization.valid
                ? frame.normalization.observationHostTicks
                : 0L;

        if (
            observationTicks > 0L &&
            observationTicks == _lastCanonicalObservationTicks)
        {
            return;
        }

        _lastCanonicalObservationTicks = observationTicks;

        long now = Stopwatch.GetTimestamp();
        if (_lastAdoptionHostTicks > 0L)
        {
            double seconds =
                KiwiPrecisionTrackingMath.HostTicksToSeconds(
                    now - _lastAdoptionHostTicks);

            if (seconds > 0.0001 && seconds < 5.0)
            {
                float instantaneous =
                    (float)(1.0 / seconds);

                _adoptionRateHz =
                    _adoptionRateHz <= 0f
                        ? instantaneous
                        : Mathf.Lerp(
                            _adoptionRateHz,
                            instantaneous,
                            0.20f);
            }
        }

        _lastAdoptionHostTicks = now;
        _adoptionCount++;
    }

    private void RecalculateDerivedMetrics()
    {
        _sourceToSubmissionGapHz =
            Mathf.Max(0f, _freshSourceRateHz - _submissionRateHz);
        _submissionToResultGapHz =
            Mathf.Max(0f, _submissionRateHz - _resultRateHz);
        _resultToAdoptionGapHz =
            Mathf.Max(0f, _resultRateHz - _adoptionRateHz);

        _submissionEfficiency =
            SafeRatio(_submissionRateHz, _freshSourceRateHz);
        _resultEfficiency =
            SafeRatio(_resultRateHz, _submissionRateHz);
        _adoptionEfficiency =
            SafeRatio(_adoptionRateHz, _resultRateHz);
    }

    private static float SafeRatio(float numerator, float denominator)
    {
        if (denominator <= 0.001f)
        {
            return 0f;
        }

        return Mathf.Clamp01(numerator / denominator);
    }

    private static float SanitizeRate(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        return Mathf.Clamp(value, 0f, 1000f);
    }

    private static float SanitizeFinite(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        return value;
    }

    private static float SanitizeNonNegative(float value)
    {
        return Mathf.Max(
            0f,
            SanitizeFinite(value));
    }
}
