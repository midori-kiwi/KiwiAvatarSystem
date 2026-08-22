using System.Threading;
using UnityEngine;

/// <summary>
/// Source-owned CameraGeneration diagnostics/advance gate.
///
/// FaceLandmarkerRunner is the runtime source owner and is expected to call
/// Advance only when a real camera/source session boundary is observed.
/// Consumers (Inference, Live2D, canonical presentation) only compare the
/// published generation and must never advance it themselves.
/// </summary>
public enum KiwiCameraGenerationReason
{
    None = 0,
    SourceRunStarted = 1,
    SourceTextureChanged = 2,
    SourceDimensionsChanged = 3,
    SourceNameChanged = 4,
    WebCamRestarted = 5,
    SourceTimelineRegression = 6,
    ExternalAdvance = 7
}

public static class KiwiCameraGeneration
{
    private static readonly object Gate = new object();

    private static int _eventCount;
    private static int _restartCount;
    private static int _identityChangeCount;
    private static int _timelineRegressionCount;
    private static int _staleCallbackDropCount;
    private static int _unmatchedCallbackDropCount;

    private static KiwiCameraGenerationReason _lastReason;
    private static string _lastSourceName = string.Empty;
    private static int _lastTextureId;
    private static int _lastWidth;
    private static int _lastHeight;
    private static long _lastObservedTimestamp = -1L;
    private static int _lastGeneration = 1;

    public static int CurrentGeneration =>
        KiwiRuntimeGenerationContext.CameraGeneration;

    public static int EventCount =>
        Volatile.Read(ref _eventCount);

    public static int RestartCount =>
        Volatile.Read(ref _restartCount);

    public static int IdentityChangeCount =>
        Volatile.Read(ref _identityChangeCount);

    public static int TimelineRegressionCount =>
        Volatile.Read(ref _timelineRegressionCount);

    public static int StaleCallbackDropCount =>
        Volatile.Read(ref _staleCallbackDropCount);

    public static int UnmatchedCallbackDropCount =>
        Volatile.Read(ref _unmatchedCallbackDropCount);

    public static KiwiCameraGenerationReason LastReason
    {
        get
        {
            lock (Gate)
            {
                return _lastReason;
            }
        }
    }

    public static string LastSourceName
    {
        get
        {
            lock (Gate)
            {
                return _lastSourceName;
            }
        }
    }

    public static int LastTextureId
    {
        get
        {
            lock (Gate)
            {
                return _lastTextureId;
            }
        }
    }

    public static int LastWidth
    {
        get
        {
            lock (Gate)
            {
                return _lastWidth;
            }
        }
    }

    public static int LastHeight
    {
        get
        {
            lock (Gate)
            {
                return _lastHeight;
            }
        }
    }

    public static long LastObservedTimestamp
    {
        get
        {
            lock (Gate)
            {
                return _lastObservedTimestamp;
            }
        }
    }

    public static int LastGeneration
    {
        get
        {
            lock (Gate)
            {
                return _lastGeneration;
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetRuntimeState()
    {
        Volatile.Write(ref _eventCount, 0);
        Volatile.Write(ref _restartCount, 0);
        Volatile.Write(ref _identityChangeCount, 0);
        Volatile.Write(ref _timelineRegressionCount, 0);
        Volatile.Write(ref _staleCallbackDropCount, 0);
        Volatile.Write(ref _unmatchedCallbackDropCount, 0);

        lock (Gate)
        {
            _lastReason = KiwiCameraGenerationReason.None;
            _lastSourceName = string.Empty;
            _lastTextureId = 0;
            _lastWidth = 0;
            _lastHeight = 0;
            _lastObservedTimestamp = -1L;
            _lastGeneration = 1;
        }
    }

    public static int Advance(
        KiwiCameraGenerationReason reason,
        string sourceName,
        int textureId,
        int width,
        int height,
        long observedTimestamp = -1L)
    {
        int generation =
            KiwiRuntimeGenerationContext.AdvanceCameraGeneration();

        Interlocked.Increment(ref _eventCount);

        switch (reason)
        {
            case KiwiCameraGenerationReason.SourceTextureChanged:
            case KiwiCameraGenerationReason.SourceDimensionsChanged:
            case KiwiCameraGenerationReason.SourceNameChanged:
                Interlocked.Increment(ref _identityChangeCount);
                break;

            case KiwiCameraGenerationReason.WebCamRestarted:
                Interlocked.Increment(ref _restartCount);
                break;

            case KiwiCameraGenerationReason.SourceTimelineRegression:
                Interlocked.Increment(ref _timelineRegressionCount);
                break;
        }

        lock (Gate)
        {
            _lastReason = reason;
            _lastSourceName = sourceName ?? string.Empty;
            _lastTextureId = textureId;
            _lastWidth = Mathf.Max(0, width);
            _lastHeight = Mathf.Max(0, height);
            _lastObservedTimestamp = observedTimestamp;
            _lastGeneration = generation;
        }

        return generation;
    }

    public static void RecordStaleCallbackDrop()
    {
        Interlocked.Increment(ref _staleCallbackDropCount);
    }

    public static void RecordUnmatchedCallbackDrop()
    {
        Interlocked.Increment(ref _unmatchedCallbackDropCount);
    }
}
