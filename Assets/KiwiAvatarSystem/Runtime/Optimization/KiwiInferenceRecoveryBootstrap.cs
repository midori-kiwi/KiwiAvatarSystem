using System;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// KiwiAvatarSystem v4.4 Inference Engine health policy.
///
/// The previous watchdog used changes in accepted presence as its main progress
/// signal. A tracker can still schedule and complete GPU work while every result
/// is rejected by the presence/geometry gate, so "p=0.00" did not distinguish
/// "not running" from "running but rejected".
///
/// v3.1 observes scheduled/completed counters from KiwiInferenceFaceTracker,
/// detects a stuck async readback, and adapts the presence gate only when a
/// high-quality MediaPipe anchor confirms that a face is actually present.
/// </summary>
[DefaultExecutionOrder(-32000)]
[DisallowMultipleComponent]
public sealed class KiwiInferenceRecoveryBootstrap : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Early Inference Recovery";

    [Header("Early hybrid preset")]
    public bool enableHybrid = true;

    [Range(320, 960)]
    public int mediaPipeInputWidth = 320;

    [Range(2f, 15f)]
    public float mediaPipeAuxRefreshHz = 5f;

    [Tooltip("After startup, KiwiMatureVTuberSupervisor owns the runtime auxiliary MediaPipe cadence. This bootstrap keeps only the startup fallback value.")]
    public bool deferRuntimeAuxCadenceToMatureSupervisor = true;

    [Range(0.10f, 0.70f)]
    public float inferencePresenceThreshold = 0.50f;

    [Header("Adaptive presence calibration")]
    public bool adaptPresenceThreshold = true;

    [Range(0.10f, 0.70f)]
    public float minimumAdaptivePresenceThreshold = 0.45f;

    [Range(0.40f, 1f)]
    public float minimumMediaPipeQualityForAdaptation = 0.72f;

    [Range(4, 40)]
    public int completedFramesBeforeAdaptation = 10;

    [Range(0.01f, 0.20f)]
    public float adaptiveThresholdSafetyMargin = 0.025f;

    [Header("Progress watchdog")]
    public bool enableRuntimeRecovery = true;

    [Tooltip("No tracker or no scheduled GPU work after this delay is considered unhealthy.")]
    [Range(1f, 10f)]
    public float noProgressRestartSeconds = 2.0f;

    [Tooltip("One async readback may not remain pending this long.")]
    [Range(0.20f, 2f)]
    public float maximumPendingReadbackSeconds = 0.75f;

    [Tooltip("A primary Inference snapshot older than this releases ownership.")]
    [Range(0.15f, 1f)]
    public float stalePrimaryTimeoutSeconds = 0.35f;

    [Range(1f, 20f)]
    public float retryIntervalSeconds = 3.5f;

    [Range(1, 6)]
    public int maximumRecoveryAttempts = 4;

    [Header("Diagnostics")]
    [SerializeField] private bool debugModelAssetLoaded;
    [SerializeField] private bool debugCropShaderLoaded;
    [SerializeField] private bool debugTrackerObjectExists;
    [SerializeField] private bool debugAnchorAvailable;
    [SerializeField] private int debugScheduledFrames;
    [SerializeField] private int debugReadbackCompletedFrames;
    [SerializeField] private int debugCompletedFrames;
    [SerializeField] private int debugDroppedFreshFrames;
    [SerializeField] private int debugPipelineDepth;
    [SerializeField] private int debugActiveLanes;
    [SerializeField] private float debugOldestPendingMs;
    [SerializeField] private float debugRawPresenceLogit;
    [SerializeField] private float debugRawPresence;
    [SerializeField] private float debugLivePresenceThreshold;
    [SerializeField] private float debugTrackerLatencyMs;
    [SerializeField] private float debugSecondsSinceCompletion;
    [SerializeField] private int debugRecoveryAttempts;
    [SerializeField] private string debugStatus = "Waiting";

    private FaceLandmarkerRunner _runner;
    private KiwiTrackingQuality10Controller _motionController;
    private KiwiMatureVTuberSupervisor _matureSupervisor;

    private double _watchStartedRealtime;
    private double _lastScheduleProgressRealtime;
    private double _lastCompletionProgressRealtime;
    private double _pendingStartedRealtime;
    private double _nextRecoveryRealtime;
    private double _nextSettingsApplyRealtime;
    private double _nextRunnerSearchRealtime;

    private int _lastScheduledFrames = -1;
    private int _lastCompletedFrames = -1;
    private int _completedSinceReset;

    private float _presenceEma;
    private bool _hasPresenceEma;

    private int _recoveryAttempts;

    private bool _reportedMissingModel;
    private bool _reportedMissingShader;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiInferenceRecoveryBootstrap>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiInferenceRecoveryBootstrap>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        ResetWatchdog();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _recoveryAttempts = 0;
        _reportedMissingModel = false;
        _reportedMissingShader = false;
        _nextRunnerSearchRealtime = 0.0;

        ResetWatchdog();
        ApplyEarlySettings(true);
    }

    private void Start()
    {
        ResetWatchdog();
        ApplyEarlySettings(true);
    }

    private void Update()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (_runner == null)
        {
            // Missing dependencies must not trigger a scene-wide object search
            // every render frame. Retry at a low cadence until the scene has
            // finished constructing the MediaPipe runner.
            if (now < _nextRunnerSearchRealtime)
            {
                return;
            }

            _nextRunnerSearchRealtime =
                now + 0.25;

            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);

            if (_runner == null)
            {
                debugStatus =
                    "Runner not found yet";

                return;
            }

            _nextRunnerSearchRealtime = 0.0;
        }

        bool settingsTick =
            now >=
            _nextSettingsApplyRealtime;

        if (settingsTick)
        {
            _nextSettingsApplyRealtime =
                now + 0.50;

            ApplyEarlySettings(false);
        }

        object tracker =
            GetPrivateField(
                _runner,
                "_sentisTracker");

        if (settingsTick)
        {
            UpdateAssetDiagnostics(
                tracker);
        }

        ObserveTrackerProgress(
            tracker,
            now);

        ApplyAdaptivePresenceThreshold(
            tracker);

        if (!enableRuntimeRecovery)
        {
            return;
        }

        if (
            _runner.InferenceEnginePrimaryActive &&
            IsPrimaryInferenceFresh()
        )
        {
            debugStatus =
                "Inference Engine primary";

            return;
        }

        if (
            _runner.InferenceEnginePrimaryActive &&
            !IsPrimaryInferenceFresh()
        )
        {
            SetPrivateField(
                _runner,
                "_sentisPrimaryActive",
                false);

            debugStatus =
                "Primary Inference stale";
        }

        string recoveryReason =
            GetRecoveryReason(
                tracker,
                now);

        if (
            string.IsNullOrEmpty(
                recoveryReason) ||
            now <
                _nextRecoveryRealtime ||
            _recoveryAttempts >=
                Mathf.Max(
                    1,
                    maximumRecoveryAttempts)
        )
        {
            return;
        }

        _nextRecoveryRealtime =
            now +
            retryIntervalSeconds;

        TryRecoverInferenceTracker(
            recoveryReason);
    }

    private void ResetWatchdog()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        _watchStartedRealtime =
            now;

        _lastScheduleProgressRealtime =
            now;

        _lastCompletionProgressRealtime =
            now;

        _pendingStartedRealtime =
            0.0;

        _nextRecoveryRealtime =
            now +
            noProgressRestartSeconds;

        _nextSettingsApplyRealtime =
            now;

        _lastScheduledFrames =
            -1;

        _lastCompletedFrames =
            -1;

        _completedSinceReset =
            0;

        _presenceEma =
            0f;

        _hasPresenceEma =
            false;
    }

    private void ApplyEarlySettings(
        bool force)
    {
        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (_runner == null)
        {
            return;
        }

        if (_motionController == null)
        {
            _motionController =
                FindFirstObjectByType<
                    KiwiTrackingQuality10Controller>(
                    FindObjectsInactive.Include);
        }

        if (_matureSupervisor == null)
        {
            _matureSupervisor =
                FindFirstObjectByType<
                    KiwiMatureVTuberSupervisor>(
                    FindObjectsInactive.Include);
        }

        _runner.enableSentisHybridTracking =
            enableHybrid;

        _runner.renderDebugLandmarkAnnotations =
            false;

        _runner.processOnlyFreshWebCamFrames =
            true;

        _runner.latestFrameOnlyLiveStream =
            true;

        _runner.downscaleTrackingInput =
            true;

        _runner.trackingInputMaxWidth =
            mediaPipeInputWidth;

        // Do not let a camera-specific 480px profile override the measured
        // 320px auxiliary path from the supplied recording.
        _runner.autoOptimizeCm831 =
            false;

        if (
            force ||
            !deferRuntimeAuxCadenceToMatureSupervisor ||
            _matureSupervisor == null
        )
        {
            _runner.sentisMediaPipeRefreshRateHz =
                mediaPipeAuxRefreshHz;
        }

        if (
            force ||
            !adaptPresenceThreshold
        )
        {
            _runner.sentisMinimumPresence =
                inferencePresenceThreshold;

            if (_motionController != null)
            {
                _motionController.inferencePresenceThreshold =
                    inferencePresenceThreshold;
            }
        }
        else if (_motionController != null)
        {
            // KiwiTrackingQuality10Controller mirrors its serialized threshold
            // into the live tracker every LateUpdate. Keep both owners on the
            // same adaptive value so the recovery policy is not undone later
            // in the frame.
            _motionController.inferencePresenceThreshold =
                _runner.sentisMinimumPresence;
        }

        SynchronizeLiveTrackerThreshold(
            GetPrivateField(
                _runner,
                "_sentisTracker"),
            _runner.sentisMinimumPresence);
    }

    private void ObserveTrackerProgress(
        object tracker,
        double now)
    {
        if (tracker == null)
        {
            debugTrackerObjectExists =
                false;

            return;
        }

        debugTrackerObjectExists =
            true;

        int scheduled =
            GetPublicIntProperty(
                tracker,
                "ScheduledFrameCount");

        int readbackCompleted =
            GetPublicIntProperty(
                tracker,
                "ReadbackCompletedFrameCount");

        // Compatibility fallback for a pre-v3.4 tracker.
        if (readbackCompleted <= 0)
        {
            readbackCompleted =
                GetPublicIntProperty(
                    tracker,
                    "CompletedFrameCount");
        }

        int completed =
            GetPublicIntProperty(
                tracker,
                "CompletedFrameCount");

        int dropped =
            GetPublicIntProperty(
                tracker,
                "DroppedFreshFrameCount");

        int pipelineDepth =
            GetPublicIntProperty(
                tracker,
                "PipelineDepth");

        int activeLanes =
            GetPublicIntProperty(
                tracker,
                "ActiveLaneCount");

        float oldestPendingMs =
            GetPublicFloatProperty(
                tracker,
                "OldestPendingAgeMs");

        float rawPresenceLogit =
            GetPublicFloatProperty(
                tracker,
                "LatestRawPresenceLogit");

        float presence =
            GetPublicFloatProperty(
                tracker,
                "LatestPresence");

        float latency =
            GetPublicFloatProperty(
                tracker,
                "LatestLatencyMs");

        bool pending =
            GetPublicBoolProperty(
                tracker,
                "IsAsyncReadbackPending");

        debugScheduledFrames =
            scheduled;

        debugReadbackCompletedFrames =
            readbackCompleted;

        debugCompletedFrames =
            completed;

        debugDroppedFreshFrames =
            dropped;

        debugPipelineDepth =
            pipelineDepth;

        debugActiveLanes =
            activeLanes;

        debugOldestPendingMs =
            oldestPendingMs;

        debugRawPresenceLogit =
            rawPresenceLogit;

        debugRawPresence =
            presence;

        debugTrackerLatencyMs =
            latency;

        if (
            scheduled !=
            _lastScheduledFrames
        )
        {
            _lastScheduledFrames =
                scheduled;

            _lastScheduleProgressRealtime =
                now;
        }

        if (
            readbackCompleted !=
            _lastCompletedFrames
        )
        {
            if (
                _lastCompletedFrames >=
                    0 &&
                readbackCompleted >
                    _lastCompletedFrames
            )
            {
                int delta =
                    readbackCompleted -
                    _lastCompletedFrames;

                _completedSinceReset +=
                    delta;
            }

            _lastCompletedFrames =
                readbackCompleted;

            _lastCompletionProgressRealtime =
                now;

            if (
                IsFinite(presence) &&
                presence >
                    0.001f
            )
            {
                _presenceEma =
                    _hasPresenceEma
                        ? Mathf.Lerp(
                            _presenceEma,
                            presence,
                            0.16f)
                        : presence;

                _hasPresenceEma =
                    true;
            }

            if (
                _completedSinceReset >=
                    12
            )
            {
                _recoveryAttempts =
                    0;
            }
        }

        _pendingStartedRealtime =
            pending
                ? now
                : 0.0;

        debugSecondsSinceCompletion =
            (float)(
                now -
                _lastCompletionProgressRealtime);
    }

    private void ApplyAdaptivePresenceThreshold(
        object tracker)
    {
        if (
            !adaptPresenceThreshold ||
            _runner == null ||
            tracker == null ||
            !_hasPresenceEma ||
            _completedSinceReset <
                Mathf.Max(
                    1,
                    completedFramesBeforeAdaptation)
        )
        {
            debugLivePresenceThreshold =
                _runner != null
                    ? _runner.sentisMinimumPresence
                    : inferencePresenceThreshold;

            return;
        }

        if (
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData mediaPipeOrCurrent) ||
            mediaPipeOrCurrent.geometryQuality <
                minimumMediaPipeQualityForAdaptation
        )
        {
            return;
        }

        float target =
            Mathf.Clamp(
                _presenceEma -
                adaptiveThresholdSafetyMargin,
                minimumAdaptivePresenceThreshold,
                inferencePresenceThreshold);

        // After the v3.2 sigmoid correction this is a true probability.
        // Never adapt from a stream that is already below the allowed floor.
        if (
            _presenceEma <
                minimumAdaptivePresenceThreshold
        )
        {
            target =
                inferencePresenceThreshold;
        }

        float current =
            _runner.sentisMinimumPresence;

        float next =
            target <
                current
                ? Mathf.MoveTowards(
                    current,
                    target,
                    0.015f)
                : Mathf.MoveTowards(
                    current,
                    target,
                    0.004f);

        _runner.sentisMinimumPresence =
            next;

        if (_motionController != null)
        {
            _motionController.inferencePresenceThreshold =
                next;
        }

        SynchronizeLiveTrackerThreshold(
            tracker,
            next);

        debugLivePresenceThreshold =
            next;
    }

    private string GetRecoveryReason(
        object tracker,
        double now)
    {
        if (
            _runner == null ||
            _runner.LatestFreshSourceRateHz <
                10f
        )
        {
            return string.Empty;
        }

        if (tracker == null)
        {
            if (
                now -
                _watchStartedRealtime >=
                noProgressRestartSeconds
            )
            {
                return
                    "tracker missing";
            }

            return string.Empty;
        }

        bool hasRegion =
            GetPublicBoolProperty(
                tracker,
                "HasRegion");

        if (!hasRegion)
        {
            // MediaPipe has not supplied a trustworthy ROI yet.
            return string.Empty;
        }

        if (
            debugScheduledFrames <=
                0 &&
            now -
                _watchStartedRealtime >=
                noProgressRestartSeconds
        )
        {
            return
                "no GPU schedule progress";
        }

        if (
            debugScheduledFrames >
                0 &&
            now -
                _lastScheduleProgressRealtime >=
                noProgressRestartSeconds
        )
        {
            return
                "GPU scheduling stalled";
        }

        bool pending =
            GetPublicBoolProperty(
                tracker,
                "IsAsyncReadbackPending");

        float oldestPendingMs =
            GetPublicFloatProperty(
                tracker,
                "OldestPendingAgeMs");

        if (
            pending &&
            oldestPendingMs >
                maximumPendingReadbackSeconds *
                1000f
        )
        {
            return
                "async pipeline lane stalled";
        }

        if (
            debugScheduledFrames >
                0 &&
            debugReadbackCompletedFrames <=
                0 &&
            now -
                _watchStartedRealtime >=
                noProgressRestartSeconds
        )
        {
            return
                "no GPU readback completion";
        }

        return string.Empty;
    }

    private bool IsPrimaryInferenceFresh()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.backend !=
                KiwiTrackingBackend.InferenceEngine ||
            data.arrivalHostTicks <=
                0L
        )
        {
            return false;
        }

        long now =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        double age =
            (now -
             data.arrivalHostTicks) /
            (double)
            System.Diagnostics.Stopwatch
                .Frequency;

        return
            age <=
            stalePrimaryTimeoutSeconds;
    }

    private void TryRecoverInferenceTracker(
        string reason)
    {
        if (_runner == null)
        {
            return;
        }

        ModelAsset model =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");

        Shader cropShader =
            Resources.Load<Shader>(
                "KiwiInferenceFaceCrop");

        debugModelAssetLoaded =
            model != null;

        debugCropShaderLoaded =
            cropShader != null;

        if (model == null)
        {
            debugStatus =
                "Inference model not loaded";

            ReportMissingModelOnce();
            return;
        }

        if (cropShader == null)
        {
            debugStatus =
                "Inference crop shader not loaded";

            if (!_reportedMissingShader)
            {
                _reportedMissingShader =
                    true;

                Debug.LogError(
                    "[Kiwi Inference Recovery] " +
                    "KiwiInferenceFaceCrop shader could not be loaded.",
                    this);
            }

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
                "Waiting for source texture";

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
                "Runner API mismatch";

            Debug.LogError(
                "[Kiwi Inference Recovery] " +
                "InitializeSentisTracker was not found.",
                this);

            _recoveryAttempts =
                maximumRecoveryAttempts;

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

        try
        {
            _recoveryAttempts++;

            debugRecoveryAttempts =
                _recoveryAttempts;

            initialize.Invoke(
                _runner,
                new object[]
                {
                    source,
                    flipX,
                    flipY
                });

            ApplyLatestMediaPipeAnchorImmediately();

            _lastScheduledFrames =
                -1;

            _lastCompletedFrames =
                -1;

            _completedSinceReset =
                0;

            _pendingStartedRealtime =
                0.0;

            _lastScheduleProgressRealtime =
                Time.realtimeSinceStartupAsDouble;

            _lastCompletionProgressRealtime =
                _lastScheduleProgressRealtime;

            debugStatus =
                "Tracker restarted: " +
                reason;

            Debug.Log(
                "[Kiwi Inference Recovery] Restarted Inference Engine (" +
                reason +
                ").",
                this);
        }
        catch (Exception exception)
        {
            debugStatus =
                "Recovery failed: " +
                exception.GetType().Name;

            Debug.LogError(
                "[Kiwi Inference Recovery] " +
                exception,
                this);
        }
    }

    private void ApplyLatestMediaPipeAnchorImmediately()
    {
        object tracker =
            GetPrivateField(
                _runner,
                "_sentisTracker");

        if (
            tracker == null ||
            !GetPrivateBoolField(
                _runner,
                "_hasLatestSentisAnchor")
        )
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
            !(rollObject is float roll)
        )
        {
            return;
        }

        MethodInfo applyAnchor =
            tracker.GetType()
                .GetMethod(
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

        debugAnchorAvailable =
            true;
    }

    private void UpdateAssetDiagnostics(
        object tracker)
    {
        debugModelAssetLoaded =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference") !=
            null;

        debugCropShaderLoaded =
            Resources.Load<Shader>(
                "KiwiInferenceFaceCrop") !=
            null;

        debugTrackerObjectExists =
            tracker != null;

        debugAnchorAvailable =
            GetPrivateBoolField(
                _runner,
                "_hasLatestSentisAnchor");
    }

    private void SynchronizeLiveTrackerThreshold(
        object tracker,
        float threshold)
    {
        if (tracker == null)
        {
            return;
        }

        PropertyInfo property =
            tracker.GetType()
                .GetProperty(
                    "MinimumPresence",
                    BindingFlags.Instance |
                    BindingFlags.Public);

        if (
            property != null &&
            property.CanWrite
        )
        {
            property.SetValue(
                tracker,
                threshold);
        }
    }

    private void ReportMissingModelOnce()
    {
        if (_reportedMissingModel)
        {
            return;
        }

        _reportedMissingModel =
            true;

        string modelPath =
            Path.Combine(
                Application.dataPath,
                "KiwiAvatarSystem",
                "Resources",
                "KiwiFaceLandmarkInference.onnx");

        string extra =
            string.Empty;

        try
        {
            if (File.Exists(modelPath))
            {
                long bytes =
                    new FileInfo(modelPath)
                        .Length;

                if (bytes < 1024L)
                {
                    extra =
                        " The ONNX file is only " +
                        bytes +
                        " bytes and appears to be a Git LFS pointer. " +
                        "Run `git lfs pull` in the repository, then let Unity reimport it.";
                }
                else
                {
                    extra =
                        " The ONNX exists on disk (" +
                        bytes +
                        " bytes) but Unity did not import it as ModelAsset.";
                }
            }
            else
            {
                extra =
                    " Missing file: " +
                    modelPath;
            }
        }
        catch
        {
        }

        Debug.LogError(
            "[Kiwi Inference Recovery] " +
            "KiwiFaceLandmarkInference ModelAsset could not be loaded." +
            extra,
            this);
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
            target.GetType()
                .GetField(
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

    private static bool IsFinite(
        float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }
}
