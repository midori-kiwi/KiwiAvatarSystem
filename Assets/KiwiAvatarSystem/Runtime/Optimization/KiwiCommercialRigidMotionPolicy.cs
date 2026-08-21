using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v4.7 commercial rigid-motion policy.
///
/// This class owns no Transform and adds no second smoothing filter. It makes
/// the already-existing KiwiFaceMotion presentation path behave like a mature
/// realtime mocap pipeline by centralizing:
///
/// 1) one provider authority at every presentation phase,
/// 2) continuity-aware short-loss holding,
/// 3) measured source-age/cadence prediction gating,
/// 4) adaptive tuning of KiwiFaceMotion's EXISTING static position dead-zone.
///
/// Eye/Mouth data never feeds the avatar root through this class.
/// </summary>
public static class KiwiCommercialRigidMotionPolicy
{
    private static KiwiTrackingContinuityState _continuity;

    private static float _lastPredictionAllowance = 1f;
    private static bool _lastHoldActive;
    private static bool _lastLostActive;
    private static float _lastPositionDeadZoneMultiplier = 1f;
    private static float _lastEffectivePositionDeadZone;

    public static float LastPredictionAllowance =>
        _lastPredictionAllowance;

    public static bool LastHoldActive =>
        _lastHoldActive;

    public static bool LastLostActive =>
        _lastLostActive;

    public static float LastPositionDeadZoneMultiplier =>
        _lastPositionDeadZoneMultiplier;

    public static float LastEffectivePositionDeadZone =>
        _lastEffectivePositionDeadZone;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        _continuity = null;
        _lastPredictionAllowance = 1f;
        _lastHoldActive = false;
        _lastLostActive = false;
        _lastPositionDeadZoneMultiplier = 1f;
        _lastEffectivePositionDeadZone = 0f;
    }

    /// <summary>
    /// The Provider Hub is authoritative whenever installed. Runner-direct is a
    /// compatibility fallback only for older scenes without a Hub.
    /// </summary>
    public static bool TryGetAuthoritativeFrame(
        FaceLandmarkerRunner runner,
        out FacePrecisionTrackingData data)
    {
        data = default;

        if (KiwiTrackingProviderHub.HasRuntimeInstance)
        {
            return
                KiwiTrackingProviderHub.TryGetCurrentRigidFrame(
                    out data);
        }

        return
            runner != null &&
            runner.TryGetLatestPrecisionTrackingData(
                out data);
    }

    /// <summary>
    /// A short processing gap holds the last trusted rigid pose. Only continuity
    /// Lost allows KiwiFaceMotion to return toward neutral.
    /// </summary>
    public static void ResolveLossPolicy(
        bool fallbackTrackingLost,
        out bool holdRigidPose,
        out bool trackingLost)
    {
        RefreshReferences();

        holdRigidPose = false;
        trackingLost = fallbackTrackingLost;

        if (_continuity == null)
        {
            _lastHoldActive = false;
            _lastLostActive = trackingLost;
            return;
        }

        switch (_continuity.State)
        {
            case KiwiTrackingContinuityState.ContinuityState.Holding:
                holdRigidPose = true;
                trackingLost = false;
                break;

            case KiwiTrackingContinuityState.ContinuityState.Lost:
                holdRigidPose = false;
                trackingLost = true;
                break;

            case KiwiTrackingContinuityState.ContinuityState.Starting:
                holdRigidPose = false;
                trackingLost = fallbackTrackingLost;
                break;

            default:
                holdRigidPose = false;
                trackingLost = false;
                break;
        }

        _lastHoldActive = holdRigidPose;
        _lastLostActive = trackingLost;
    }

    /// <summary>
    /// Adapts KiwiFaceMotion's existing static position dead-zone instead of
    /// stacking a new low-pass/dead-zone stage. The multiplier increases only
    /// when measured cadence/source freshness or geometry quality is degraded.
    /// Real motion still releases through the original raw-speed/error logic.
    /// </summary>
    public static float GetAdaptivePositionDeadZone(
        float configuredDeadZone,
        float quality)
    {
        RefreshReferences();

        float sourceAge = 0f;
        float jitter = 0f;
        KiwiTrackingContinuityState.ContinuityState state =
            KiwiTrackingContinuityState.ContinuityState.Stable;

        if (_continuity != null)
        {
            sourceAge =
                Mathf.Max(
                    0f,
                    _continuity.SourceAgeSeconds);

            jitter =
                Mathf.Max(
                    0f,
                    _continuity.CadenceJitterRatio);

            state =
                _continuity.State;
        }

        float sourceRisk =
            Mathf.InverseLerp(
                0.110f,
                0.230f,
                sourceAge);

        float cadenceRisk =
            Mathf.InverseLerp(
                0.22f,
                0.72f,
                jitter);

        float qualityRisk =
            1f -
            Mathf.InverseLerp(
                0.35f,
                0.82f,
                Mathf.Clamp01(quality));

        float risk =
            Mathf.Max(
                sourceRisk,
                Mathf.Max(
                    cadenceRisk,
                    qualityRisk));

        if (state == KiwiTrackingContinuityState.ContinuityState.Degraded)
        {
            risk = Mathf.Max(risk, 0.55f);
        }
        else if (state == KiwiTrackingContinuityState.ContinuityState.Reacquiring)
        {
            risk = Mathf.Max(risk, 0.45f);
        }

        // Keep this bounded: it is a rest-noise policy, not a way to hide a
        // broken tracker. Catastrophic motion remains handled by authority,
        // freshness and outlier logic.
        float multiplier =
            Mathf.Lerp(
                1f,
                1.85f,
                Mathf.Clamp01(risk));

        _lastPositionDeadZoneMultiplier = multiplier;
        _lastEffectivePositionDeadZone =
            Mathf.Max(
                0f,
                configuredDeadZone) *
            multiplier;

        return _lastEffectivePositionDeadZone;
    }

    /// <summary>
    /// Prediction is useful only while the observation is fresh and cadence is
    /// regular. This returns a gain only; it adds no buffer and no delay.
    /// </summary>
    public static float GetPredictionAllowance(
        float sampleAgeSeconds)
    {
        RefreshReferences();

        float allowance = 1f;
        float sourceAge = Mathf.Max(0f, sampleAgeSeconds);
        float jitter = 0f;

        if (_continuity != null)
        {
            allowance *=
                _continuity.PredictionAllowance;

            sourceAge =
                Mathf.Max(
                    sourceAge,
                    _continuity.SourceAgeSeconds);

            jitter =
                Mathf.Max(
                    0f,
                    _continuity.CadenceJitterRatio);

            if (
                _continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Holding ||
                _continuity.State ==
                    KiwiTrackingContinuityState.ContinuityState.Lost)
            {
                _lastPredictionAllowance = 0f;
                return 0f;
            }
        }

        if (
            KiwiLatencyBudgetController.TryGetRuntimeBudget(
                out float budgetSourceAge,
                out float predictionBudget,
                out float strengthMultiplier,
                out float budgetJitter)
        )
        {
            sourceAge =
                Mathf.Max(
                    sourceAge,
                    budgetSourceAge);

            jitter =
                Mathf.Max(
                    jitter,
                    budgetJitter);

            allowance *=
                Mathf.Clamp01(
                    strengthMultiplier);

            if (predictionBudget <= 0.0001f)
            {
                allowance = 0f;
            }
        }

        float freshness =
            1f -
            Mathf.InverseLerp(
                0.120f,
                0.235f,
                sourceAge);

        float cadenceRegularity =
            1f -
            Mathf.InverseLerp(
                0.25f,
                0.75f,
                jitter);

        allowance *=
            Mathf.Clamp01(
                Mathf.Min(
                    freshness,
                    Mathf.Lerp(
                        0.35f,
                        1f,
                        cadenceRegularity)));

        _lastPredictionAllowance =
            Mathf.Clamp01(
                allowance);

        return _lastPredictionAllowance;
    }

    private static void RefreshReferences()
    {
        if (_continuity == null)
        {
            _continuity =
                Object.FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }
    }
}
