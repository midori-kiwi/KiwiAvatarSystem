using System;
using System.Diagnostics;
using UnityEngine;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Input timestamp domain declared by a tracking-provider adapter.
///
/// Canonical rigid consumers never subtract provider-native timestamps directly.
/// KiwiTrackingProviderHub maps every accepted frame onto the local
/// System.Diagnostics.Stopwatch domain first.
/// </summary>
public enum KiwiTrackingTimebase
{
    LegacyUnspecified = 0,
    HostStopwatchTicks = 1,
    ProviderMonotonicMilliseconds = 2,
    ProviderMonotonicMicroseconds = 3,
    ProviderMonotonicNanoseconds = 4,
    ArrivalHostOnly = 5
}

/// <summary>
/// Quality of the canonical observation time published by the provider hub.
/// </summary>
public enum KiwiTrackingTimestampQuality
{
    ArrivalFallback = 0,
    ProviderMapped = 1,
    ExactHostObservation = 2
}

/// <summary>
/// Horizontal image convention of an adapter's normalized 2D face geometry.
/// Y remains the existing Kiwi / MediaPipe top-left normalized convention.
///
/// CanonicalPresentation preserves the legacy SubmitExternalFrame contract:
/// the adapter already supplies the same horizontal convention currently used
/// by Kiwi's presentation camera/Runner.
/// </summary>
public enum KiwiTrackingHorizontalConvention
{
    CanonicalPresentation = 0,
    Unmirrored = 1,
    Mirrored = 2
}

/// <summary>
/// Optional explicit contract for external tracking adapters.
///
/// sourceTimestamp is interpreted only according to timebase. For provider
/// monotonic clocks, zero is a valid source timestamp. Use LegacyUnspecified
/// when upgrading an old adapter that already supplied Kiwi-normalized data.
/// arrivalHostTicks, when supplied, MUST be local Stopwatch ticks.
/// </summary>
[Serializable]
public struct KiwiExternalTrackingFrameContract
{
    public KiwiTrackingTimebase timebase;
    public long sourceTimestamp;
    public long arrivalHostTicks;
    public KiwiTrackingHorizontalConvention horizontalConvention;

    public static KiwiExternalTrackingFrameContract LegacyNormalized(
        FacePrecisionTrackingData data)
    {
        return new KiwiExternalTrackingFrameContract
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
                KiwiTrackingHorizontalConvention.CanonicalPresentation
        };
    }

    public static KiwiExternalTrackingFrameContract HostObserved(
        long observationHostTicks,
        long arrivalHostTicks,
        bool inputHorizontallyMirrored)
    {
        return new KiwiExternalTrackingFrameContract
        {
            timebase = KiwiTrackingTimebase.HostStopwatchTicks,
            sourceTimestamp = observationHostTicks,
            arrivalHostTicks = arrivalHostTicks,
            horizontalConvention = inputHorizontallyMirrored
                ? KiwiTrackingHorizontalConvention.Mirrored
                : KiwiTrackingHorizontalConvention.Unmirrored
        };
    }

    public static KiwiExternalTrackingFrameContract ProviderMonotonic(
        KiwiTrackingTimebase timebase,
        long sourceTimestamp,
        long arrivalHostTicks,
        bool inputHorizontallyMirrored)
    {
        return new KiwiExternalTrackingFrameContract
        {
            timebase = timebase,
            sourceTimestamp = sourceTimestamp,
            arrivalHostTicks = arrivalHostTicks,
            horizontalConvention = inputHorizontallyMirrored
                ? KiwiTrackingHorizontalConvention.Mirrored
                : KiwiTrackingHorizontalConvention.Unmirrored
        };
    }
}

/// <summary>
/// Immutable normalization metadata paired with one hub-selected rigid frame.
/// FacePrecisionTrackingData remains source-compatible with the completed
/// project; this metadata carries the explicit timebase/handedness contract.
/// </summary>
public readonly struct KiwiTrackingNormalizedFrameMetadata
{
    public readonly bool valid;
    public readonly KiwiTrackingTimebase sourceTimebase;
    public readonly KiwiTrackingTimestampQuality timestampQuality;
    public readonly KiwiTrackingHorizontalConvention sourceHorizontalConvention;
    public readonly bool canonicalInputHorizontallyMirrored;
    public readonly bool horizontalTransformApplied;
    public readonly long providerSourceTimestamp;
    public readonly ulong providerSourceFrameId;
    public readonly long observationHostTicks;
    public readonly long arrivalHostTicks;
    public readonly int timebaseResetCount;

    public KiwiTrackingNormalizedFrameMetadata(
        bool valid,
        KiwiTrackingTimebase sourceTimebase,
        KiwiTrackingTimestampQuality timestampQuality,
        KiwiTrackingHorizontalConvention sourceHorizontalConvention,
        bool canonicalInputHorizontallyMirrored,
        bool horizontalTransformApplied,
        long providerSourceTimestamp,
        ulong providerSourceFrameId,
        long observationHostTicks,
        long arrivalHostTicks,
        int timebaseResetCount)
    {
        this.valid = valid;
        this.sourceTimebase = sourceTimebase;
        this.timestampQuality = timestampQuality;
        this.sourceHorizontalConvention = sourceHorizontalConvention;
        this.canonicalInputHorizontallyMirrored =
            canonicalInputHorizontallyMirrored;
        this.horizontalTransformApplied = horizontalTransformApplied;
        this.providerSourceTimestamp = providerSourceTimestamp;
        this.providerSourceFrameId = providerSourceFrameId;
        this.observationHostTicks = observationHostTicks;
        this.arrivalHostTicks = arrivalHostTicks;
        this.timebaseResetCount = timebaseResetCount;
    }
}

/// <summary>
/// Pure helpers for the Phase 9 tracking normalization contract.
/// No provider selection or Transform ownership lives here.
/// </summary>
public static class KiwiTrackingNormalizationMath
{
    public static long CurrentHostTicks()
    {
        return Stopwatch.GetTimestamp();
    }

    public static double SourceUnitsToHostTicks(
        KiwiTrackingTimebase timebase)
    {
        double frequency = Stopwatch.Frequency;

        switch (timebase)
        {
            case KiwiTrackingTimebase.ProviderMonotonicMilliseconds:
                return frequency / 1000.0;

            case KiwiTrackingTimebase.ProviderMonotonicMicroseconds:
                return frequency / 1000000.0;

            case KiwiTrackingTimebase.ProviderMonotonicNanoseconds:
                return frequency / 1000000000.0;

            case KiwiTrackingTimebase.HostStopwatchTicks:
                return 1.0;

            default:
                return 0.0;
        }
    }

    public static long HostTicksToMilliseconds(
        long hostTicks)
    {
        if (hostTicks <= 0L)
        {
            return 0L;
        }

        return (long)Math.Floor(
            hostTicks * 1000.0 /
            Stopwatch.Frequency);
    }

    public static void MirrorHorizontal(
        ref FacePrecisionTrackingData data)
    {
        data.faceCenter = MirrorPoint(data.faceCenter);
        data.rightEyeCenter = MirrorPoint(data.rightEyeCenter);
        data.leftEyeCenter = MirrorPoint(data.leftEyeCenter);
        data.eyeCenter = MirrorPoint(data.eyeCenter);
        data.chin = MirrorPoint(data.chin);
        data.nose = MirrorPoint(data.nose);
        data.cheekCenter = MirrorPoint(data.cheekCenter);
        data.forehead = MirrorPoint(data.forehead);

        // Reflection across camera X conjugates the rotation matrix by
        // diag(-1,+1,+1). In quaternion form pitch/X is retained while
        // yaw/Y and roll/Z change sign. This matches the completed
        // KiwiPrecisionTrackingMath mirror behavior without Euler conversion.
        Quaternion q = data.faceRotation;
        data.faceRotation = NormalizeQuaternionSafe(
            new Quaternion(
                q.x,
                -q.y,
                -q.z,
                q.w));
    }

    public static bool RequiresHorizontalMirror(
        KiwiTrackingHorizontalConvention sourceConvention,
        bool canonicalInputHorizontallyMirrored)
    {
        if (
            sourceConvention ==
                KiwiTrackingHorizontalConvention.CanonicalPresentation)
        {
            return false;
        }

        bool sourceMirrored =
            sourceConvention ==
                KiwiTrackingHorizontalConvention.Mirrored;

        return
            sourceMirrored !=
            canonicalInputHorizontallyMirrored;
    }

    private static Vector2 MirrorPoint(
        Vector2 point)
    {
        // Preserve zero as the project's existing "point unavailable" sentinel.
        if (point == Vector2.zero)
        {
            return point;
        }

        return new Vector2(
            1f - point.x,
            point.y);
    }

    private static Quaternion NormalizeQuaternionSafe(
        Quaternion value)
    {
        float magnitude = Mathf.Sqrt(
            value.x * value.x +
            value.y * value.y +
            value.z * value.z +
            value.w * value.w);

        if (
            magnitude <= 0.000001f ||
            float.IsNaN(magnitude) ||
            float.IsInfinity(magnitude))
        {
            return Quaternion.identity;
        }

        float inverse = 1f / magnitude;

        return new Quaternion(
            value.x * inverse,
            value.y * inverse,
            value.z * inverse,
            value.w * inverse);
    }
}
