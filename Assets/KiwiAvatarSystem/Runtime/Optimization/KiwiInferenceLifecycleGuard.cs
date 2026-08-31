using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// KiwiAvatarSystem v44.14 inference shutdown render-target hygiene guard.
///
/// This component leaves the existing inference policy/calibration bootstrap
/// active, but disables its destructive hard-restart path and owns recovery.
/// Recovery is two-stage:
/// 1) non-destructive tracker Reset() retires the current generation while
///    already submitted GPU readbacks remain alive and drain normally;
/// 2) only if no readback progress occurs during an additional bounded grace
///    period is the existing runner hard-rebuild entry point invoked.
///
/// v44.13 additionally closes the Windows Standalone shutdown ownership gap:
/// when Unity announces application quit, the guard invokes the runner's
/// existing DisposeSentisTracker() exactly once before graphics teardown.
/// This releases Worker/input/crop resources through their current owner path.
/// No global AsyncGPUReadback.WaitAllRequests or other blocking GPU wait is
/// introduced.
///
/// No tracking confidence, ROI geometry, model, decode, camera, mesh, or
/// presentation policy is changed here.
/// </summary>
[DefaultExecutionOrder(-31990)]
[DisallowMultipleComponent]
public sealed class KiwiInferenceLifecycleGuard : MonoBehaviour
{
    public const string Contract =
        "KIWI_V5_1_PHASE16_20_45_V44_14_SHUTDOWN_RENDER_TARGET_HYGIENE";

    private const string RuntimeObjectName =
        "[Kiwi] Inference Lifecycle Guard v44.14";

    [Header("Lifecycle recovery")]
    [Tooltip("After a non-destructive Reset(), wait this long for old readbacks to drain before allowing a hard rebuild.")]
    [Range(1f, 10f)]
    public float hardRebuildGraceSeconds = 3.0f;

    [Tooltip("A genuinely missing tracker has no pending worker output to preserve, so it may be rebuilt after the legacy no-progress delay.")]
    public bool rebuildMissingTracker = true;

    [Header("Diagnostics")]
    [SerializeField] private string debugStatus = "Waiting";
    [SerializeField] private bool debugLegacyHardRecoveryDisabled;
    [SerializeField] private int debugSoftRetireCount;
    [SerializeField] private int debugSoftRecoveredCount;
    [SerializeField] private int debugHardRebuildCount;
    [SerializeField] private int debugScheduledFrames;
    [SerializeField] private int debugReadbackCompletedFrames;
    [SerializeField] private int debugActiveLanes;
    [SerializeField] private float debugOldestPendingMs;
    [SerializeField] private float debugSecondsSinceFirstSchedule;
    [SerializeField] private int debugShutdownCleanupCount;
    [SerializeField] private bool debugShutdownCleanupSucceeded;
    [SerializeField] private int debugShutdownActiveLanes;
    [SerializeField] private int debugShutdownPendingLanes;
    [SerializeField] private float debugShutdownDisposeMs;
    [SerializeField] private bool debugShutdownHadActiveRenderTexture;
    [SerializeField] private string debugShutdownActiveRenderTextureName;
    [SerializeField] private int debugShutdownActiveRenderTextureId;
    [SerializeField] private bool debugShutdownRenderTargetCleared;

    private FaceLandmarkerRunner _runner;
    private KiwiInferenceRecoveryBootstrap _legacy;
    private object _tracker;

    private int _lastScheduledFrames = -1;
    private int _lastReadbackCompletedFrames = -1;
    private int _softRetireScheduledFrames;
    private int _softRetireReadbackFrames;

    private double _trackerObservedRealtime;
    private double _regionAvailableRealtime;
    private double _firstScheduleRealtime;
    private double _lastScheduleProgressRealtime;
    private double _lastReadbackProgressRealtime;
    private double _softRetireRealtime;
    private double _trackerMissingRealtime;
    private double _nextHardRebuildRealtime;

    private bool _softRetirePending;
    private bool _loggedContract;
    private bool _shutdownCleanupDone;
    private bool _applicationQuitting;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiInferenceLifecycleGuard>(
                FindObjectsInactive.Include) != null)
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiInferenceLifecycleGuard>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        Application.quitting -= HandleApplicationQuitting;
        Application.quitting += HandleApplicationQuitting;

        ResetObservationState(
            Time.realtimeSinceStartupAsDouble,
            true);
    }

    private void Start()
    {
        RefreshReferences(true);
        DisableLegacyHardRecovery();
        LogContractOnce();
    }

    private void OnApplicationQuit()
    {
        _applicationQuitting = true;
        ExecuteShutdownCleanup(
            "OnApplicationQuit");
    }

    private void HandleApplicationQuitting()
    {
        _applicationQuitting = true;
        ExecuteShutdownCleanup(
            "Application.quitting");
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        Application.quitting -= HandleApplicationQuitting;

        if (_applicationQuitting)
        {
            ExecuteShutdownCleanup(
                "OnDestroyFallback");
        }
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _legacy = null;
        _tracker = null;

        ResetObservationState(
            Time.realtimeSinceStartupAsDouble,
            true);

        RefreshReferences(true);
        DisableLegacyHardRecovery();
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        RefreshReferences(false);
        DisableLegacyHardRecovery();

        if (_runner == null)
        {
            debugStatus =
                "Runner not found";
            return;
        }

        object tracker =
            GetPrivateField(
                _runner,
                "_sentisTracker");

        if (!ReferenceEquals(tracker, _tracker))
        {
            _tracker = tracker;
            ResetObservationState(now, false);
        }

        if (tracker == null)
        {
            ObserveMissingTracker(now);
            return;
        }

        _trackerMissingRealtime = 0.0;

        ObserveTrackerProgress(
            tracker,
            now);

        EvaluateRecovery(
            tracker,
            now);
    }

    private void RefreshReferences(bool force)
    {
        if (force || _legacy == null)
        {
            _legacy =
                FindFirstObjectByType<
                    KiwiInferenceRecoveryBootstrap>(
                        FindObjectsInactive.Include);
        }

        if (force || _runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }
    }

    private void DisableLegacyHardRecovery()
    {
        if (_legacy == null)
        {
            return;
        }

        if (_legacy.enableRuntimeRecovery)
        {
            _legacy.enableRuntimeRecovery = false;
        }

        debugLegacyHardRecoveryDisabled =
            !_legacy.enableRuntimeRecovery;
    }

    private void ResetObservationState(
        double now,
        bool resetCounts)
    {
        _lastScheduledFrames = -1;
        _lastReadbackCompletedFrames = -1;
        _softRetireScheduledFrames = 0;
        _softRetireReadbackFrames = 0;

        _trackerObservedRealtime = now;
        _regionAvailableRealtime = 0.0;
        _firstScheduleRealtime = 0.0;
        _lastScheduleProgressRealtime = now;
        _lastReadbackProgressRealtime = now;
        _softRetireRealtime = 0.0;
        _trackerMissingRealtime = 0.0;
        _nextHardRebuildRealtime = 0.0;

        _softRetirePending = false;

        debugScheduledFrames = 0;
        debugReadbackCompletedFrames = 0;
        debugActiveLanes = 0;
        debugOldestPendingMs = 0f;
        debugSecondsSinceFirstSchedule = 0f;
        debugStatus = "Observing";

        if (resetCounts)
        {
            debugSoftRetireCount = 0;
            debugSoftRecoveredCount = 0;
            debugHardRebuildCount = 0;
        }
    }

    private void ObserveTrackerProgress(
        object tracker,
        double now)
    {
        int scheduled =
            GetPublicIntProperty(
                tracker,
                "ScheduledFrameCount");

        int readbackCompleted =
            GetPublicIntProperty(
                tracker,
                "ReadbackCompletedFrameCount");

        if (readbackCompleted <= 0)
        {
            readbackCompleted =
                GetPublicIntProperty(
                    tracker,
                    "CompletedFrameCount");
        }

        int activeLanes =
            GetPublicIntProperty(
                tracker,
                "ActiveLaneCount");

        float oldestPendingMs =
            GetPublicFloatProperty(
                tracker,
                "OldestPendingAgeMs");

        bool hasRegion =
            GetPublicBoolProperty(
                tracker,
                "HasRegion");

        debugScheduledFrames = scheduled;
        debugReadbackCompletedFrames = readbackCompleted;
        debugActiveLanes = activeLanes;
        debugOldestPendingMs = oldestPendingMs;

        if (hasRegion)
        {
            if (_regionAvailableRealtime <= 0.0)
            {
                _regionAvailableRealtime = now;
            }
        }
        else
        {
            _regionAvailableRealtime = 0.0;
        }

        if (scheduled != _lastScheduledFrames)
        {
            bool advanced =
                _lastScheduledFrames >= 0 &&
                scheduled > _lastScheduledFrames;

            _lastScheduledFrames = scheduled;
            _lastScheduleProgressRealtime = now;

            if (
                scheduled > 0 &&
                _firstScheduleRealtime <= 0.0)
            {
                _firstScheduleRealtime = now;
            }

            if (advanced && _softRetirePending)
            {
                // New work after a soft retirement is a useful health signal.
                // Keep waiting for an actual readback before declaring recovery,
                // but extend the hard-rebuild grace from this forward progress.
                _softRetireRealtime = now;
            }
        }

        if (readbackCompleted != _lastReadbackCompletedFrames)
        {
            bool advanced =
                _lastReadbackCompletedFrames >= 0 &&
                readbackCompleted > _lastReadbackCompletedFrames;

            _lastReadbackCompletedFrames = readbackCompleted;
            _lastReadbackProgressRealtime = now;

            if (advanced && _softRetirePending)
            {
                _softRetirePending = false;
                _softRetireRealtime = 0.0;
                debugSoftRecoveredCount++;
                debugStatus =
                    "Soft recovery drained pending GPU work";

                Debug.Log(
                    "[KiwiInferenceLifecycleV44_14] SOFT_RECOVERED" +
                    " scheduled=" + scheduled +
                    " readbackCompleted=" + readbackCompleted +
                    " activeLanes=" + activeLanes +
                    " oldestPendingMs=" +
                    oldestPendingMs.ToString("F1"));
            }
        }

        debugSecondsSinceFirstSchedule =
            _firstScheduleRealtime > 0.0
                ? (float)(now - _firstScheduleRealtime)
                : 0f;
    }

    private void EvaluateRecovery(
        object tracker,
        double now)
    {
        if (
            _runner.LatestFreshSourceRateHz < 10f)
        {
            debugStatus =
                "Fresh source not ready";
            return;
        }

        bool hasRegion =
            GetPublicBoolProperty(
                tracker,
                "HasRegion");

        if (!hasRegion)
        {
            debugStatus =
                _softRetirePending
                    ? "Waiting for trusted ROI after soft retire"
                    : "Waiting for trusted ROI";
            return;
        }

        float noProgressSeconds =
            ResolveLegacyNoProgressSeconds();

        float maximumPendingSeconds =
            ResolveLegacyMaximumPendingSeconds();

        if (_softRetirePending)
        {
            if (
                now - _softRetireRealtime >=
                    Mathf.Max(1f, hardRebuildGraceSeconds))
            {
                int activeLanes =
                    GetPublicIntProperty(
                        tracker,
                        "ActiveLaneCount");

                float oldestPendingMs =
                    GetPublicFloatProperty(
                        tracker,
                        "OldestPendingAgeMs");

                bool noReadbackProgress =
                    debugReadbackCompletedFrames <=
                        _softRetireReadbackFrames;

                bool noScheduleProgress =
                    debugScheduledFrames <=
                        _softRetireScheduledFrames;

                bool physicallyStalled =
                    activeLanes > 0 &&
                    oldestPendingMs >
                        maximumPendingSeconds * 1000f;

                if (
                    noReadbackProgress &&
                    (physicallyStalled || noScheduleProgress))
                {
                    TryHardRebuild(
                        "soft-retire grace expired",
                        now);
                }
                else
                {
                    // Some forward progress exists. Do not destroy Worker-owned
                    // output references that may still be completing.
                    _softRetireRealtime = now;
                    _softRetireScheduledFrames =
                        debugScheduledFrames;
                    _softRetireReadbackFrames =
                        debugReadbackCompletedFrames;
                }
            }

            return;
        }

        // Startup no-completion is measured from the FIRST ACTUAL SCHEDULE,
        // never from scene/bootstrap creation. This removes the previous warmup
        // false-positive while preserving the existing no-progress duration.
        if (
            debugScheduledFrames > 0 &&
            debugReadbackCompletedFrames <= 0 &&
            _firstScheduleRealtime > 0.0 &&
            now - _firstScheduleRealtime >=
                noProgressSeconds)
        {
            BeginSoftRetire(
                tracker,
                "no GPU readback completion since first schedule",
                now);
            return;
        }

        if (
            debugActiveLanes > 0 &&
            debugOldestPendingMs >
                maximumPendingSeconds * 1000f)
        {
            BeginSoftRetire(
                tracker,
                "physical async lane age exceeded",
                now);
            return;
        }

        if (
            debugScheduledFrames <= 0 &&
            _regionAvailableRealtime > 0.0 &&
            now - _regionAvailableRealtime >=
                noProgressSeconds)
        {
            BeginSoftRetire(
                tracker,
                "no GPU schedule progress after trusted ROI",
                now);
            return;
        }

        if (
            debugScheduledFrames > 0 &&
            debugActiveLanes <= 0 &&
            now - _lastScheduleProgressRealtime >=
                noProgressSeconds)
        {
            BeginSoftRetire(
                tracker,
                "GPU scheduling stalled with no active lane",
                now);
            return;
        }

        debugStatus =
            "Inference lifecycle healthy";
    }

    private void BeginSoftRetire(
        object tracker,
        string reason,
        double now)
    {
        if (
            tracker == null ||
            _softRetirePending)
        {
            return;
        }

        MethodInfo reset =
            tracker.GetType().GetMethod(
                "Reset",
                BindingFlags.Instance |
                BindingFlags.Public);

        if (reset == null)
        {
            TryHardRebuild(
                "tracker Reset API missing: " + reason,
                now);
            return;
        }

        try
        {
            reset.Invoke(
                tracker,
                null);

            ApplyLatestMediaPipeAnchorImmediately(
                tracker);

            _softRetirePending = true;
            _softRetireRealtime = now;
            _softRetireScheduledFrames =
                debugScheduledFrames;
            _softRetireReadbackFrames =
                debugReadbackCompletedFrames;

            debugSoftRetireCount++;
            debugStatus =
                "Soft-retired: " + reason;

            Debug.Log(
                "[KiwiInferenceLifecycleV44_14] SOFT_RETIRE" +
                " reason=" + reason +
                " scheduled=" + debugScheduledFrames +
                " readbackCompleted=" +
                debugReadbackCompletedFrames +
                " activeLanes=" + debugActiveLanes +
                " oldestPendingMs=" +
                debugOldestPendingMs.ToString("F1") +
                " action=RESET_GENERATION_DRAIN");
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                "[KiwiInferenceLifecycleV44_14] " +
                "soft retire failed: " +
                exception.GetType().Name +
                " " + exception.Message,
                this);

            TryHardRebuild(
                "soft retire exception: " + reason,
                now);
        }
    }

    private void ObserveMissingTracker(double now)
    {
        if (
            !rebuildMissingTracker ||
            _runner == null ||
            _runner.LatestFreshSourceRateHz < 10f)
        {
            debugStatus =
                "Tracker missing; waiting";
            return;
        }

        if (_trackerMissingRealtime <= 0.0)
        {
            _trackerMissingRealtime = now;
            debugStatus =
                "Tracker missing; grace";
            return;
        }

        if (
            now - _trackerMissingRealtime >=
                ResolveLegacyNoProgressSeconds())
        {
            TryHardRebuild(
                "tracker missing",
                now);
        }
    }

    private void TryHardRebuild(
        string reason,
        double now)
    {
        if (
            _runner == null ||
            now < _nextHardRebuildRealtime)
        {
            return;
        }

        int maximumAttempts =
            _legacy != null
                ? Mathf.Max(
                    1,
                    _legacy.maximumRecoveryAttempts)
                : 4;

        if (debugHardRebuildCount >= maximumAttempts)
        {
            debugStatus =
                "Hard rebuild attempt limit reached";
            return;
        }

        Texture source =
            GetPrivateField(
                _runner,
                "_sentisSourceTexture")
            as Texture;

        if (source == null)
        {
            debugStatus =
                "Hard rebuild waiting for source texture";
            return;
        }

        MethodInfo initialize =
            typeof(FaceLandmarkerRunner)
                .GetMethod(
                    "InitializeSentisTracker",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

        if (initialize == null)
        {
            debugStatus =
                "Runner InitializeSentisTracker API missing";
            return;
        }

        bool flipX =
            GetPrivateBoolField(
                _runner,
                "_sentisFlipHorizontally");

        bool flipY =
            GetPrivateBoolField(
                _runner,
                "_sentisFlipVertically");

        float retrySeconds =
            _legacy != null
                ? Mathf.Max(
                    1f,
                    _legacy.retryIntervalSeconds)
                : 3.5f;

        _nextHardRebuildRealtime =
            now + retrySeconds;

        const string recoveryDomainSource =
            "InferenceLifecycleGuardV44_12";

        KiwiRecoveryDomainCoordinator.BeginGlobalRecovery(
            recoveryDomainSource,
            KiwiRecoveryDomainCoordinator.GlobalRecoveryReason
                .InferencePipelineStalled |
            KiwiRecoveryDomainCoordinator.GlobalRecoveryReason
                .InferenceRestart);

        try
        {
            initialize.Invoke(
                _runner,
                new object[]
                {
                    source,
                    flipX,
                    flipY
                });

            object rebuiltTracker =
                GetPrivateField(
                    _runner,
                    "_sentisTracker");

            ApplyLatestMediaPipeAnchorImmediately(
                rebuiltTracker);

            debugHardRebuildCount++;
            debugStatus =
                "Hard rebuilt: " + reason;

            KiwiRecoveryDomainCoordinator.CompleteGlobalRecovery(
                recoveryDomainSource,
                true);

            Debug.LogWarning(
                "[KiwiInferenceLifecycleV44_14] HARD_REBUILD" +
                " reason=" + reason +
                " count=" + debugHardRebuildCount +
                " afterSoft=" +
                (_softRetirePending ? "1" : "0"),
                this);

            _tracker = rebuiltTracker;
            ResetObservationState(now, false);
        }
        catch (Exception exception)
        {
            KiwiRecoveryDomainCoordinator.CompleteGlobalRecovery(
                recoveryDomainSource,
                false);

            debugStatus =
                "Hard rebuild failed: " +
                exception.GetType().Name;

            Debug.LogError(
                "[KiwiInferenceLifecycleV44_14] " +
                exception,
                this);
        }
    }

    private void ApplyLatestMediaPipeAnchorImmediately(
        object tracker)
    {
        if (
            tracker == null ||
            _runner == null ||
            !GetPrivateBoolField(
                _runner,
                "_hasLatestSentisAnchor"))
        {
            return;
        }

        object regionObject =
            GetPrivateField(
                _runner,
                "_latestSentisAnchorRegion");

        object rollObject =
            GetPrivateField(
                _runner,
                "_latestSentisAnchorRollRadians");

        if (
            !(regionObject is Rect region) ||
            !(rollObject is float roll))
        {
            return;
        }

        MethodInfo applyAnchor =
            tracker.GetType().GetMethod(
                "ApplyExternalAnchor",
                BindingFlags.Instance |
                BindingFlags.Public);

        if (applyAnchor == null)
        {
            return;
        }

        applyAnchor.Invoke(
            tracker,
            new object[]
            {
                region,
                roll,
                true
            });

        object timestamp =
            GetPrivateField(
                _runner,
                "_lastSentisAnchorTimestamp");

        SetPrivateField(
            _runner,
            "_lastSentisAnchorTimestampApplied",
            timestamp);
    }

    private float ResolveLegacyNoProgressSeconds()
    {
        return
            _legacy != null
                ? Mathf.Max(
                    1f,
                    _legacy.noProgressRestartSeconds)
                : 2f;
    }

    private float ResolveLegacyMaximumPendingSeconds()
    {
        return
            _legacy != null
                ? Mathf.Max(
                    0.2f,
                    _legacy.maximumPendingReadbackSeconds)
                : 0.75f;
    }

    private void ExecuteShutdownCleanup(
        string source)
    {
        if (_shutdownCleanupDone)
        {
            return;
        }

        _shutdownCleanupDone = true;

        // v44.14: Unity requires callers that change RenderTexture.active to
        // restore/clear the render target. Historical Kiwi standalone logs have
        // emitted "Releasing render texture that is set to be RenderTexture.active!"
        // at shutdown since before v44.13. Application quit is the safest point
        // to normalize the global render-target state because no runtime frame
        // should continue after this cleanup begins.
        RenderTexture activeRenderTexture =
            RenderTexture.active;

        debugShutdownHadActiveRenderTexture =
            activeRenderTexture != null;

        debugShutdownActiveRenderTextureName =
            activeRenderTexture != null
                ? activeRenderTexture.name
                : string.Empty;

        debugShutdownActiveRenderTextureId =
            activeRenderTexture != null
                ? activeRenderTexture.GetInstanceID()
                : 0;

        if (activeRenderTexture != null)
        {
            RenderTexture.active =
                null;

            debugShutdownRenderTargetCleared =
                RenderTexture.active == null;
        }
        else
        {
            debugShutdownRenderTargetCleared =
                true;
        }

        Debug.Log(
            "[KiwiInferenceLifecycleV44_14] SHUTDOWN_RENDER_TARGET" +
            " hadActive=" +
            (debugShutdownHadActiveRenderTexture ? "1" : "0") +
            " activeName='" +
            debugShutdownActiveRenderTextureName +
            "'" +
            " activeId=" +
            debugShutdownActiveRenderTextureId +
            " cleared=" +
            (debugShutdownRenderTargetCleared ? "1" : "0") +
            " restoreAfterQuit=0");

        RefreshReferences(true);
        DisableLegacyHardRecovery();

        object tracker =
            _runner != null
                ? GetPrivateField(
                    _runner,
                    "_sentisTracker")
                : null;

        CountTrackerLanes(
            tracker,
            out int activeLanes,
            out int pendingLanes);

        debugShutdownActiveLanes =
            activeLanes;
        debugShutdownPendingLanes =
            pendingLanes;

        Debug.Log(
            "[KiwiInferenceLifecycleV44_14] SHUTDOWN_CLEANUP_BEGIN" +
            " source=" + source +
            " runner=" + (_runner != null ? "1" : "0") +
            " tracker=" + (tracker != null ? "1" : "0") +
            " activeLanes=" + activeLanes +
            " pendingLanes=" + pendingLanes +
            " blockingGpuWait=0");

        bool invoked =
            false;

        bool trackerCleared =
            tracker == null;

        long started =
            System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            if (
                _runner != null &&
                tracker != null)
            {
                MethodInfo dispose =
                    typeof(FaceLandmarkerRunner)
                        .GetMethod(
                            "DisposeSentisTracker",
                            BindingFlags.Instance |
                            BindingFlags.NonPublic);

                if (dispose == null)
                {
                    debugStatus =
                        "Shutdown DisposeSentisTracker API missing";

                    Debug.LogError(
                        "[KiwiInferenceLifecycleV44_14] " +
                        "DisposeSentisTracker API missing.",
                        this);
                }
                else
                {
                    dispose.Invoke(
                        _runner,
                        null);

                    invoked =
                        true;

                    trackerCleared =
                        GetPrivateField(
                            _runner,
                            "_sentisTracker") == null;
                }
            }
        }
        catch (Exception exception)
        {
            debugStatus =
                "Shutdown cleanup failed: " +
                exception.GetType().Name;

            Debug.LogError(
                "[KiwiInferenceLifecycleV44_14] " +
                "shutdown cleanup exception=" +
                exception,
                this);
        }

        long finished =
            System.Diagnostics.Stopwatch.GetTimestamp();

        double elapsedMs =
            (finished - started) *
            1000.0 /
            System.Diagnostics.Stopwatch.Frequency;

        debugShutdownDisposeMs =
            (float)elapsedMs;

        debugShutdownCleanupCount++;

        debugShutdownCleanupSucceeded =
            trackerCleared;

        if (trackerCleared)
        {
            _tracker = null;
        }

        Debug.Log(
            "[KiwiInferenceLifecycleV44_14] SHUTDOWN_CLEANUP_END" +
            " source=" + source +
            " disposeInvoked=" + (invoked ? "1" : "0") +
            " trackerCleared=" + (trackerCleared ? "1" : "0") +
            " disposeMs=" + elapsedMs.ToString("F3") +
            " blockingGpuWait=0");
    }

    private static void CountTrackerLanes(
        object tracker,
        out int activeLanes,
        out int pendingLanes)
    {
        activeLanes =
            0;
        pendingLanes =
            0;

        if (tracker == null)
        {
            return;
        }

        object lanesObject =
            GetPrivateField(
                tracker,
                "_lanes");

        if (!(lanesObject is Array lanes))
        {
            return;
        }

        for (
            int i = 0;
            i < lanes.Length;
            i++)
        {
            object lane =
                lanes.GetValue(i);

            if (lane == null)
            {
                continue;
            }

            activeLanes++;

            FieldInfo pendingField =
                lane.GetType().GetField(
                    "readbackPending",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            if (
                pendingField != null &&
                pendingField.GetValue(lane) is bool isPending &&
                isPending)
            {
                pendingLanes++;
            }
        }
    }

    private void LogContractOnce()
    {
        if (_loggedContract)
        {
            return;
        }

        _loggedContract = true;

        Debug.Log(
            "[KiwiInferenceLifecycleV44_14] contract=" +
            Contract +
            " legacyHardRecovery=DISABLED" +
            " softRetire=RESET_GENERATION_DRAIN" +
            " hardRebuildGraceSeconds=" +
            hardRebuildGraceSeconds.ToString("F2") +
            " blockingGpuWait=0" +
            " trackingThresholdChange=0" +
            " shutdownDispose=RUNNER_OWNED_DISPOSE_SENTIS_TRACKER" +
            " shutdownWaitAllRequests=0" +
            " shutdownRenderTarget=SET_ACTIVE_NULL_BEFORE_DISPOSE");
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
            target.GetType().GetField(
                fieldName,
                BindingFlags.Instance |
                BindingFlags.NonPublic);

        return
            field != null
                ? field.GetValue(target)
                : null;
    }

    private static bool GetPrivateBoolField(
        object target,
        string fieldName)
    {
        object value =
            GetPrivateField(
                target,
                fieldName);

        return
            value is bool boolean &&
            boolean;
    }

    private static void SetPrivateField(
        object target,
        string fieldName,
        object value)
    {
        if (target == null)
        {
            return;
        }

        FieldInfo field =
            target.GetType().GetField(
                fieldName,
                BindingFlags.Instance |
                BindingFlags.NonPublic);

        if (field != null)
        {
            field.SetValue(
                target,
                value);
        }
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
            target.GetType().GetProperty(
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
