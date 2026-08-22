using UnityEngine;

/// <summary>
/// v5.1 Phase 16 frame-accurate display continuity math.
///
/// This does not own Root pose and does not add another temporal filter.
/// KiwiFaceMotion remains the sole Rigid Pose Authority. The helper only
/// bounds the EXISTING display-rate resampling response when accepted tracking
/// samples arrive sparsely or after an explicit timing/provider discontinuity.
/// </summary>
public static class KiwiFrameContinuityMath
{
    public static float ResolveEffectiveSampleInterval(
        float acceptedSampleInterval,
        float measuredTrackingRateHz)
    {
        float accepted =
            acceptedSampleInterval > 0f
                ? acceptedSampleInterval
                : 1f / 30f;

        float measured =
            measuredTrackingRateHz > 0.5f
                ? 1f / measuredTrackingRateHz
                : accepted;

        // Provider/timebase changes deliberately seed KiwiFaceMotion with a
        // nominal 1/30 s interval. Taking the slower observed interval keeps
        // the display guard tied to real output cadence instead of that seed.
        return Mathf.Clamp(
            Mathf.Max(accepted, measured),
            1f / 240f,
            0.50f);
    }

    public static bool IsSparseCadence(
        float effectiveSampleInterval,
        float directBypassMinimumTrackingRateHz)
    {
        float thresholdHz =
            Mathf.Max(1f, directBypassMinimumTrackingRateHz);

        return effectiveSampleInterval > 1f / thresholdHz;
    }

    public static float CalculateResponseCap(
        float effectiveSampleInterval,
        float convergencePerAcceptedSample,
        bool discontinuityGuardActive,
        float discontinuityResponseCap)
    {
        float interval =
            Mathf.Clamp(
                effectiveSampleInterval,
                1f / 240f,
                0.50f);

        float convergence =
            Mathf.Clamp(
                convergencePerAcceptedSample,
                0.50f,
                0.99f);

        // x(t) = 1 - exp(-response * t).
        // Solve response so the display closes the configured fraction of the
        // correction before the next expected accepted sample. This provides
        // deterministic catch-up without a permanent one-frame buffer.
        float response =
            -Mathf.Log(1f - convergence) /
            interval;

        if (discontinuityGuardActive)
        {
            response =
                Mathf.Min(
                    response,
                    Mathf.Max(1f, discontinuityResponseCap));
        }

        return Mathf.Clamp(
            response,
            4f,
            240f);
    }
}

/// <summary>
/// Read-only runtime diagnostics written by KiwiFaceMotion after the Phase 16
/// migration. Other systems may observe this state but must not steer Root.
/// </summary>
public static class KiwiFrameContinuityDiagnostics
{
    private static bool _enabled;
    private static bool _directBypassSuppressed;
    private static bool _discontinuityGuardActive;
    private static float _trackingRateHz;
    private static float _effectiveSampleInterval;
    private static float _responseCap;
    private static int _unityFrame;

    public static bool Enabled => _enabled;
    public static bool DirectBypassSuppressed => _directBypassSuppressed;
    public static bool DiscontinuityGuardActive => _discontinuityGuardActive;
    public static float TrackingRateHz => _trackingRateHz;
    public static float EffectiveSampleInterval => _effectiveSampleInterval;
    public static float ResponseCap => _responseCap;
    public static int UnityFrame => _unityFrame;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetRuntimeState()
    {
        _enabled = false;
        _directBypassSuppressed = false;
        _discontinuityGuardActive = false;
        _trackingRateHz = 0f;
        _effectiveSampleInterval = 0f;
        _responseCap = 0f;
        _unityFrame = -1;
    }

    public static void Report(
        bool enabled,
        bool directBypassSuppressed,
        bool discontinuityGuardActive,
        float trackingRateHz,
        float effectiveSampleInterval,
        float responseCap)
    {
        _enabled = enabled;
        _directBypassSuppressed = directBypassSuppressed;
        _discontinuityGuardActive = discontinuityGuardActive;
        _trackingRateHz = Mathf.Max(0f, trackingRateHz);
        _effectiveSampleInterval = Mathf.Max(0f, effectiveSampleInterval);
        _responseCap = Mathf.Max(0f, responseCap);
        _unityFrame = Time.frameCount;
    }
}
