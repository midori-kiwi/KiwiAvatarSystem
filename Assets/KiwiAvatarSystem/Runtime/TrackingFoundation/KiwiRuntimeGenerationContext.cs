using System.Threading;
using UnityEngine;

/// <summary>
/// Cross-system generation contract for KiwiAvatarSystem.
///
/// Existing subsystem-local generations remain authoritative for their own
/// resource lifetimes. This class adds only the shared identity needed to
/// reject asynchronous work that belongs to an older camera/provider/model/
/// configuration/semantic transaction.
///
/// All counters are monotonic inside one play session. They intentionally do
/// not own presentation state, Transforms, calibration values, or tracking
/// policy.
/// </summary>
public static class KiwiRuntimeGenerationContext
{
    public readonly struct Snapshot
    {
        public readonly int cameraGeneration;
        public readonly int providerGeneration;
        public readonly int modelGeneration;
        public readonly int configEpoch;
        public readonly int calibrationGeneration;
        public readonly int trackingSessionGeneration;
        public readonly long observationSequence;
        public readonly long semanticTransactionSequence;

        public Snapshot(
            int cameraGeneration,
            int providerGeneration,
            int modelGeneration,
            int configEpoch,
            int calibrationGeneration,
            int trackingSessionGeneration,
            long observationSequence,
            long semanticTransactionSequence)
        {
            this.cameraGeneration = cameraGeneration;
            this.providerGeneration = providerGeneration;
            this.modelGeneration = modelGeneration;
            this.configEpoch = configEpoch;
            this.calibrationGeneration = calibrationGeneration;
            this.trackingSessionGeneration = trackingSessionGeneration;
            this.observationSequence = observationSequence;
            this.semanticTransactionSequence = semanticTransactionSequence;
        }
    }

    private static int _cameraGeneration = 1;
    private static int _providerGeneration = 1;
    private static int _modelGeneration = 1;
    private static int _configEpoch = 1;
    private static int _calibrationGeneration = 1;
    private static int _trackingSessionGeneration = 1;

    private static long _observationSequence;
    private static long _semanticTransactionSequence;

    public static int CameraGeneration => Volatile.Read(ref _cameraGeneration);
    public static int ProviderGeneration => Volatile.Read(ref _providerGeneration);
    public static int ModelGeneration => Volatile.Read(ref _modelGeneration);
    public static int ConfigEpoch => Volatile.Read(ref _configEpoch);
    public static int CalibrationGeneration => Volatile.Read(ref _calibrationGeneration);
    public static int TrackingSessionGeneration => Volatile.Read(ref _trackingSessionGeneration);
    public static long ObservationSequence => Interlocked.Read(ref _observationSequence);
    public static long SemanticTransactionSequence => Interlocked.Read(ref _semanticTransactionSequence);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        Volatile.Write(ref _cameraGeneration, 1);
        Volatile.Write(ref _providerGeneration, 1);
        Volatile.Write(ref _modelGeneration, 1);
        Volatile.Write(ref _configEpoch, 1);
        Volatile.Write(ref _calibrationGeneration, 1);
        Volatile.Write(ref _trackingSessionGeneration, 1);
        Interlocked.Exchange(ref _observationSequence, 0L);
        Interlocked.Exchange(ref _semanticTransactionSequence, 0L);
    }

    public static Snapshot Capture()
    {
        return new Snapshot(
            CameraGeneration,
            ProviderGeneration,
            ModelGeneration,
            ConfigEpoch,
            CalibrationGeneration,
            TrackingSessionGeneration,
            ObservationSequence,
            SemanticTransactionSequence);
    }

    public static bool IsAsyncIdentityCurrent(
        int cameraGeneration,
        int providerGeneration,
        int modelGeneration,
        int configEpoch,
        int trackingSessionGeneration,
        long semanticTransactionSequence)
    {
        return
            cameraGeneration == CameraGeneration &&
            providerGeneration == ProviderGeneration &&
            modelGeneration == ModelGeneration &&
            configEpoch == ConfigEpoch &&
            trackingSessionGeneration == TrackingSessionGeneration &&
            semanticTransactionSequence == SemanticTransactionSequence;
    }

    /// <summary>
    /// Identity gate for camera/inference jobs that are independent from the
    /// currently selected provider, avatar model, and semantic face-part state.
    /// Keeping this narrower than IsAsyncIdentityCurrent prevents a legitimate
    /// future provider candidate from being discarded merely because the active
    /// provider or model changed while inference was in flight.
    /// </summary>
    public static bool IsCameraSessionIdentityCurrent(
        int cameraGeneration,
        int trackingSessionGeneration)
    {
        return
            cameraGeneration == CameraGeneration &&
            trackingSessionGeneration == TrackingSessionGeneration;
    }

    /// <summary>
    /// Identity gate for asynchronous presentation work. Unlike raw inference,
    /// face-part presentation depends on the currently active calibration basis,
    /// so a calibration-generation change must retire work submitted under the
    /// previous neutral/attachment solve.
    /// </summary>
    public static bool IsPresentationAsyncIdentityCurrent(
        int cameraGeneration,
        int providerGeneration,
        int modelGeneration,
        int configEpoch,
        int calibrationGeneration,
        int trackingSessionGeneration,
        long semanticTransactionSequence)
    {
        return
            cameraGeneration == CameraGeneration &&
            providerGeneration == ProviderGeneration &&
            modelGeneration == ModelGeneration &&
            configEpoch == ConfigEpoch &&
            calibrationGeneration == CalibrationGeneration &&
            trackingSessionGeneration == TrackingSessionGeneration &&
            semanticTransactionSequence == SemanticTransactionSequence;
    }

    // Low-level compatibility primitive. Runtime camera/source ownership belongs
    // to KiwiCameraGeneration / FaceLandmarkerRunner; consumers must not advance
    // CameraGeneration when they merely observe a texture change.
    public static int AdvanceCameraGeneration() => IncrementPositive(ref _cameraGeneration);
    public static int AdvanceProviderGeneration() => IncrementPositive(ref _providerGeneration);
    public static int AdvanceModelGeneration() => IncrementPositive(ref _modelGeneration);
    public static int AdvanceConfigEpoch() => IncrementPositive(ref _configEpoch);
    public static int AdvanceCalibrationGeneration() => IncrementPositive(ref _calibrationGeneration);
    public static int AdvanceTrackingSessionGeneration() => IncrementPositive(ref _trackingSessionGeneration);
    public static long NextObservationSequence() => IncrementPositive(ref _observationSequence);
    public static long AdvanceSemanticTransaction() => IncrementPositive(ref _semanticTransactionSequence);

    private static int IncrementPositive(ref int value)
    {
        int next = Interlocked.Increment(ref value);
        if (next > 0) return next;
        Interlocked.Exchange(ref value, 1);
        return 1;
    }

    private static long IncrementPositive(ref long value)
    {
        long next = Interlocked.Increment(ref value);
        if (next > 0L) return next;
        Interlocked.Exchange(ref value, 1L);
        return 1L;
    }
}
