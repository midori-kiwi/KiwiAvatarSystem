using System;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Early hybrid bootstrap and bounded self-recovery.
///
/// v2.6 specifically handles the failure visible in the 21:47 recording:
/// a tracker object can exist while Inference remains p=0.00 for tens of
/// seconds. v2.5 treated "tracker exists" as healthy and therefore did not
/// restart it.
///
/// v2.6 requires actual inference progress. A stalled tracker is rebuilt and
/// immediately seeded with the newest MediaPipe anchor.
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
    public float mediaPipeAuxRefreshHz = 6f;

    [Range(0.10f, 0.70f)]
    public float inferencePresenceThreshold = 0.30f;

    [Header("Progress watchdog")]
    public bool enableRuntimeRecovery = true;

    [Tooltip("No p>0 result by this time means initialization is stalled even when the tracker object exists.")]
    [Range(1f, 10f)]
    public float noProgressRestartSeconds = 2.5f;

    [Tooltip("A non-primary tracker with unchanged presence for this long is rebuilt once more.")]
    [Range(2f, 20f)]
    public float staleProgressRestartSeconds = 6f;

    [Tooltip("If Inference is marked primary but its last atomic result is this old, release ownership and recover.")]
    [Range(0.15f, 1.0f)]
    public float stalePrimaryTimeoutSeconds = 0.35f;

    [Range(2f, 20f)]
    public float retryIntervalSeconds = 4f;

    [Range(1, 4)]
    public int maximumRecoveryAttempts = 3;

    [Header("Diagnostics")]
    [SerializeField] private bool debugModelAssetLoaded;
    [SerializeField] private bool debugCropShaderLoaded;
    [SerializeField] private bool debugTrackerObjectExists;
    [SerializeField] private bool debugAnchorAvailable;
    [SerializeField] private int debugRecoveryAttempts;
    [SerializeField] private float debugLastPresence;
    [SerializeField] private float debugSecondsWithoutProgress;
    [SerializeField] private string debugStatus = "Waiting";

    private FaceLandmarkerRunner _runner;

    private double _watchStartedRealtime;
    private double _lastProgressRealtime;
    private double _nextRecoveryRealtime;

    private float _lastObservedPresence = -1f;
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

        host.AddComponent<
            KiwiInferenceRecoveryBootstrap>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        ResetWatchdog();
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -=
            HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _runner = null;
        _recoveryAttempts = 0;
        _reportedMissingModel = false;
        _reportedMissingShader = false;

        ResetWatchdog();
        ApplyEarlySettings();
    }

    private void Start()
    {
        ResetWatchdog();
        ApplyEarlySettings();
    }

    private void Update()
    {
        if (_runner == null)
        {
            ApplyEarlySettings();

            if (_runner == null)
            {
                return;
            }
        }

        ApplyEarlySettings();
        UpdateAssetDiagnostics();
        ObserveInferenceProgress();

        if (!enableRuntimeRecovery)
        {
            return;
        }

        double now =
            Time.realtimeSinceStartupAsDouble;

        if (_runner.InferenceEnginePrimaryActive)
        {
            if (!IsPrimaryInferenceStale())
            {
                debugStatus =
                    "Inference Engine primary";

                return;
            }

            debugStatus =
                "Primary Inference stale - recovering";

            SetPrivateField(
                _runner,
                "_sentisPrimaryActive",
                false);
        }

        double noProgressSeconds =
            now -
            _lastProgressRealtime;

        debugSecondsWithoutProgress =
            (float)noProgressSeconds;

        bool neverProducedPresence =
            _runner.LatestInferenceEnginePresence <=
                0.001f &&
            now -
            _watchStartedRealtime >=
                noProgressRestartSeconds;

        bool staleNonPrimary =
            _runner.LatestInferenceEnginePresence >
                0.001f &&
            noProgressSeconds >=
                staleProgressRestartSeconds;

        if (
            !neverProducedPresence &&
            !staleNonPrimary
        )
        {
            return;
        }

        if (
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
            staleNonPrimary
                ? "stale non-primary"
                : "no inference progress");
    }

    private void ResetWatchdog()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        _watchStartedRealtime =
            now;

        _lastProgressRealtime =
            now;

        _nextRecoveryRealtime =
            now +
            noProgressRestartSeconds;

        _lastObservedPresence =
            -1f;

        debugSecondsWithoutProgress =
            0f;
    }

    private void ApplyEarlySettings()
    {
        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (_runner == null)
        {
            debugStatus =
                "Runner not found yet";

            return;
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

        _runner.autoOptimizeCm831 =
            false;

        _runner.sentisMediaPipeRefreshRateHz =
            mediaPipeAuxRefreshHz;

        _runner.sentisMinimumPresence =
            inferencePresenceThreshold;

        SynchronizeLiveTrackerThreshold();

        if (
            string.IsNullOrEmpty(
                debugStatus) ||
            debugStatus == "Waiting" ||
            debugStatus ==
                "Runner not found yet"
        )
        {
            debugStatus =
                "Hybrid preset active";
        }
    }

    private void SynchronizeLiveTrackerThreshold()
    {
        object tracker =
            GetPrivateField(
                _runner,
                "_sentisTracker");

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
                inferencePresenceThreshold);
        }
    }

    private void UpdateAssetDiagnostics()
    {
        ModelAsset model =
            Resources.Load<ModelAsset>(
                "KiwiFaceLandmarkInference");

        Shader shader =
            Resources.Load<Shader>(
                "KiwiInferenceFaceCrop");

        debugModelAssetLoaded =
            model != null;

        debugCropShaderLoaded =
            shader != null;

        debugTrackerObjectExists =
            GetPrivateField(
                _runner,
                "_sentisTracker") != null;

        debugAnchorAvailable =
            GetPrivateBool(
                _runner,
                "_hasLatestSentisAnchor");
    }

    private void ObserveInferenceProgress()
    {
        float presence =
            _runner != null
                ? _runner.LatestInferenceEnginePresence
                : 0f;

        debugLastPresence =
            presence;

        bool newProgress =
            presence > 0.001f &&
            (
                _lastObservedPresence < 0f ||
                Mathf.Abs(
                    presence -
                    _lastObservedPresence) >
                    0.002f
            );

        if (newProgress)
        {
            _lastProgressRealtime =
                Time.realtimeSinceStartupAsDouble;

            _lastObservedPresence =
                presence;
        }
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
                _reportedMissingShader = true;

                Debug.LogError(
                    "[Kiwi Inference Recovery] " +
                    "Resources.Load<Shader>(\"KiwiInferenceFaceCrop\") failed.",
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
                "Waiting for Runner source texture";

            return;
        }

        bool flipX =
            GetPrivateBool(
                _runner,
                "_sentisFlipHorizontally");

        bool flipY =
            GetPrivateBool(
                _runner,
                "_sentisFlipVertically");

        MethodInfo initialize =
            typeof(FaceLandmarkerRunner)
                .GetMethod(
                    "InitializeSentisTracker",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);

        if (initialize == null)
        {
            debugStatus =
                "InitializeSentisTracker missing";

            Debug.LogError(
                "[Kiwi Inference Recovery] " +
                "Runner API is incompatible with the v2.6 recovery layer.",
                this);

            _recoveryAttempts =
                Mathf.Max(
                    _recoveryAttempts,
                    maximumRecoveryAttempts);

            return;
        }

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

            SetPrivateField(
                _runner,
                "_sentisPublishFailureStreak",
                0);

            ApplyLatestMediaPipeAnchorImmediately();

            _lastObservedPresence =
                -1f;

            _lastProgressRealtime =
                Time.realtimeSinceStartupAsDouble;

            debugTrackerObjectExists =
                GetPrivateField(
                    _runner,
                    "_sentisTracker") != null;

            debugStatus =
                debugTrackerObjectExists
                    ? "Tracker restarted: " +
                      reason
                    : "Restart returned no tracker";

            Debug.Log(
                "[Kiwi Inference Recovery] " +
                "Restarted the Inference Engine tracker (" +
                reason +
                ") and seeded the newest MediaPipe ROI. " +
                "Inference ms / p= should update within a few camera frames.",
                this);
        }
        catch (Exception exception)
        {
            debugStatus =
                "Recovery threw: " +
                exception.GetType().Name;

            Debug.LogError(
                "[Kiwi Inference Recovery] " +
                "Failed to restart tracker: " +
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

        if (tracker == null)
        {
            return;
        }

        if (
            !GetPrivateBool(
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

        // Keep the Runner's "already applied" stamp coherent with the seed.
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

    private bool IsPrimaryInferenceStale()
    {
        if (
            _runner == null ||
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data))
        {
            return true;
        }

        if (
            data.backend !=
                KiwiTrackingBackend.InferenceEngine ||
            data.arrivalHostTicks <= 0L)
        {
            return true;
        }

        long nowTicks =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        double ageSeconds =
            (nowTicks -
             data.arrivalHostTicks) /
            (double)
            System.Diagnostics.Stopwatch.Frequency;

        return
            ageSeconds >
            stalePrimaryTimeoutSeconds;
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
                        " bytes, which looks like a Git LFS pointer. " +
                        "Run `git lfs pull` in the repository and let Unity reimport it.";
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
            "Resources.Load<ModelAsset>(\"KiwiFaceLandmarkInference\") failed." +
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

    private static bool GetPrivateBool(
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

        if (field == null)
        {
            return;
        }

        field.SetValue(
            target,
            value);
    }
}
