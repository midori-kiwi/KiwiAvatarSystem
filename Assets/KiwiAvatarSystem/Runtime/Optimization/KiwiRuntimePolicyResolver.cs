using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v5.1 runtime policy single-writer.
///
/// Producers submit intent here; this component is the only steady-state writer
/// for collision-prone tracking / Live2D runtime configuration:
///
/// - FaceLandmarkerRunner.trackingInputMaxWidth
/// - FaceLandmarkerRunner.sentisMediaPipeRefreshRateHz
/// - FaceLandmarkerRunner.sentisMinimumPresence
/// - KiwiFacePartLiveMotionBridge.trackingLongSide
/// - KiwiFacePartLiveMotionBridge.patchStridePixels
/// - KiwiFacePartLiveMotionBridge.restingSearchRadiusPixels
///
/// Commercial Profile keeps owning user motion/profile values because those
/// fields already have a single steady-state owner. The Quality Governor,
/// Mature Supervisor, Recovery Bootstrap and Quality10 controller submit policy
/// requests instead of racing on the actual runtime fields.
/// </summary>
[DefaultExecutionOrder(32600)]
[DisallowMultipleComponent]
public sealed class KiwiRuntimePolicyResolver : MonoBehaviour
{
    public enum RequestPriority
    {
        Preset = 100,
        Bootstrap = 200,
        Governor = 250,
        RuntimeAdaptive = 300,
        UserOverride = 400
    }

    private const string RuntimeObjectName =
        "[Kiwi] Runtime Policy Resolver";

    private struct IntRequest
    {
        public bool valid;
        public int value;
        public int priority;
        public string source;
    }

    private struct FloatRequest
    {
        public bool valid;
        public float value;
        public int priority;
        public string source;
    }

    private struct LeasedFloatRequest
    {
        public bool valid;
        public float value;
        public int priority;
        public string source;
        public double expiresRealtime;
    }

    private struct Live2DRequest
    {
        public bool valid;
        public int trackingLongSide;
        public int patchStridePixels;
        public int restingSearchRadiusPixels;
        public float auxiliaryCadenceScale;
        public int priority;
        public string source;
    }

    private static IntRequest _trackingInputWidth;
    private static FloatRequest _baselineMediaPipeRefreshHz;
    private static LeasedFloatRequest _runtimeMediaPipeRefreshHz;
    private static FloatRequest _baselinePresenceThreshold;
    private static LeasedFloatRequest _adaptivePresenceThreshold;
    private static FloatRequest _userPresenceOverride;
    private static Live2DRequest _live2DPolicy;

    private static int _policyVersion;
    private static string _lastPolicySource = "-";
    private static float _resolvedMediaPipeRefreshHz;
    private static float _resolvedPresenceThreshold;
    private static int _resolvedTrackingInputWidth;
    private static int _resolvedLiveTrackingLongSide;
    private static int _resolvedPatchStridePixels;
    private static int _resolvedRestingSearchRadiusPixels;
    private static float _resolvedAuxiliaryCadenceScale = 1f;

    private FaceLandmarkerRunner _runner;
    private KiwiFacePartLiveMotionBridge _liveMotion;

    private double _nextReferenceRefreshRealtime;
    private double _nextTrackerPresenceSyncRealtime;
    private object _lastSynchronizedTracker;

    [Header("Diagnostics")]
    [SerializeField] private int debugPolicyVersion;
    [SerializeField] private string debugLastPolicySource = "-";
    [SerializeField] private int debugTrackingInputWidth;
    [SerializeField] private float debugMediaPipeRefreshHz;
    [SerializeField] private float debugPresenceThreshold;
    [SerializeField] private int debugLiveTrackingLongSide;
    [SerializeField] private int debugPatchStridePixels;
    [SerializeField] private int debugRestingSearchRadiusPixels;
    [SerializeField] private float debugAuxiliaryCadenceScale = 1f;

    private static readonly FieldInfo SentisTrackerField =
        typeof(FaceLandmarkerRunner).GetField(
            "_sentisTracker",
            BindingFlags.Instance |
            BindingFlags.NonPublic);

    public static int PolicyVersion =>
        _policyVersion;

    public static string LastPolicySource =>
        _lastPolicySource;

    public static float ResolvedMediaPipeRefreshHz =>
        _resolvedMediaPipeRefreshHz;

    public static float ResolvedPresenceThreshold =>
        _resolvedPresenceThreshold;

    public static int ResolvedTrackingInputWidth =>
        _resolvedTrackingInputWidth;

    public static float ResolvedAuxiliaryCadenceScale =>
        _live2DPolicy.valid
            ? Mathf.Clamp(
                _live2DPolicy.auxiliaryCadenceScale,
                0.70f,
                1.30f)
            : 1f;

    public static bool HasLive2DPolicy =>
        _live2DPolicy.valid;

    // Phase 11 validation visibility. These expose only the already-resolved
    // values; they do not create a second policy owner or mutation path.
    public static int ResolvedLiveTrackingLongSide =>
        _resolvedLiveTrackingLongSide;

    public static int ResolvedLivePatchStridePixels =>
        _resolvedPatchStridePixels;

    public static int ResolvedLiveRestingSearchRadiusPixels =>
        _resolvedRestingSearchRadiusPixels;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        _trackingInputWidth = default;
        _baselineMediaPipeRefreshHz = default;
        _runtimeMediaPipeRefreshHz = default;
        _baselinePresenceThreshold = default;
        _adaptivePresenceThreshold = default;
        _userPresenceOverride = default;
        _live2DPolicy = default;

        _policyVersion = 0;
        _lastPolicySource = "-";
        _resolvedMediaPipeRefreshHz = 0f;
        _resolvedPresenceThreshold = 0f;
        _resolvedTrackingInputWidth = 0;
        _resolvedLiveTrackingLongSide = 0;
        _resolvedPatchStridePixels = 0;
        _resolvedRestingSearchRadiusPixels = 0;
        _resolvedAuxiliaryCadenceScale = 1f;
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiRuntimePolicyResolver>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<KiwiRuntimePolicyResolver>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);
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
        _liveMotion = null;
        _lastSynchronizedTracker = null;
        _nextReferenceRefreshRealtime = 0.0;
        _nextTrackerPresenceSyncRealtime = 0.0;

        RefreshReferences(true);
    }

    private void LateUpdate()
    {
        double now =
            Time.realtimeSinceStartupAsDouble;

        if (
            now >=
            _nextReferenceRefreshRealtime
        )
        {
            _nextReferenceRefreshRealtime =
                now + 1.0;

            RefreshReferences(false);
        }

        ResolveAndApply(now);
        UpdateDiagnostics();
    }

    public static void SubmitTrackingInputWidth(
        int value,
        RequestPriority priority,
        string source)
    {
        value =
            Mathf.Clamp(
                value,
                160,
                1920);

        Submit(
            ref _trackingInputWidth,
            value,
            priority,
            source);
    }

    public static void SubmitBaselineMediaPipeRefreshHz(
        float value,
        RequestPriority priority,
        string source)
    {
        value =
            Mathf.Clamp(
                value,
                2f,
                30f);

        Submit(
            ref _baselineMediaPipeRefreshHz,
            value,
            priority,
            source);
    }

    public static void SubmitRuntimeMediaPipeRefreshHz(
        float value,
        RequestPriority priority,
        string source,
        float leaseSeconds = 0.75f)
    {
        value =
            Mathf.Clamp(
                value,
                2f,
                30f);

        SubmitLeased(
            ref _runtimeMediaPipeRefreshHz,
            value,
            priority,
            source,
            leaseSeconds);
    }

    public static void SubmitBaselinePresenceThreshold(
        float value,
        RequestPriority priority,
        string source)
    {
        value =
            Mathf.Clamp(
                value,
                0.05f,
                0.99f);

        Submit(
            ref _baselinePresenceThreshold,
            value,
            priority,
            source);
    }

    public static void SubmitAdaptivePresenceThreshold(
        float value,
        RequestPriority priority,
        string source,
        float leaseSeconds = 1.0f)
    {
        value =
            Mathf.Clamp(
                value,
                0.05f,
                0.99f);

        SubmitLeased(
            ref _adaptivePresenceThreshold,
            value,
            priority,
            source,
            leaseSeconds);
    }

    public static void SubmitUserPresenceOverride(
        float value,
        string source)
    {
        value =
            Mathf.Clamp(
                value,
                0.05f,
                0.99f);

        Submit(
            ref _userPresenceOverride,
            value,
            RequestPriority.UserOverride,
            source);
    }

    public static void ClearUserPresenceOverride()
    {
        _userPresenceOverride = default;
    }

    public static void SubmitLive2DPolicy(
        int trackingLongSide,
        int patchStridePixels,
        int restingSearchRadiusPixels,
        float auxiliaryCadenceScale,
        RequestPriority priority,
        string source)
    {
        int priorityValue =
            (int)priority;

        if (
            _live2DPolicy.valid &&
            priorityValue <
                _live2DPolicy.priority
        )
        {
            return;
        }

        _live2DPolicy.valid = true;
        _live2DPolicy.trackingLongSide =
            Mathf.Clamp(
                trackingLongSide,
                128,
                2048);
        _live2DPolicy.patchStridePixels =
            Mathf.Clamp(
                patchStridePixels,
                1,
                16);
        _live2DPolicy.restingSearchRadiusPixels =
            Mathf.Clamp(
                restingSearchRadiusPixels,
                1,
                64);
        _live2DPolicy.auxiliaryCadenceScale =
            Mathf.Clamp(
                auxiliaryCadenceScale,
                0.70f,
                1.30f);
        _live2DPolicy.priority =
            priorityValue;
        _live2DPolicy.source =
            NormalizeSource(source);
    }

    private static void Submit(
        ref IntRequest request,
        int value,
        RequestPriority priority,
        string source)
    {
        int priorityValue =
            (int)priority;

        if (
            request.valid &&
            priorityValue <
                request.priority
        )
        {
            return;
        }

        request.valid = true;
        request.value = value;
        request.priority = priorityValue;
        request.source = NormalizeSource(source);
    }

    private static void Submit(
        ref FloatRequest request,
        float value,
        RequestPriority priority,
        string source)
    {
        int priorityValue =
            (int)priority;

        if (
            request.valid &&
            priorityValue <
                request.priority
        )
        {
            return;
        }

        request.valid = true;
        request.value = value;
        request.priority = priorityValue;
        request.source = NormalizeSource(source);
    }

    private static void SubmitLeased(
        ref LeasedFloatRequest request,
        float value,
        RequestPriority priority,
        string source,
        float leaseSeconds)
    {
        int priorityValue =
            (int)priority;

        double now =
            Time.realtimeSinceStartupAsDouble;

        bool currentExpired =
            request.valid &&
            request.expiresRealtime > 0.0 &&
            now >
                request.expiresRealtime;

        if (
            request.valid &&
            !currentExpired &&
            priorityValue <
                request.priority
        )
        {
            return;
        }

        request.valid = true;
        request.value = value;
        request.priority = priorityValue;
        request.source = NormalizeSource(source);
        request.expiresRealtime =
            now +
            Mathf.Clamp(
                leaseSeconds,
                0.10f,
                5f);
    }

    private void ResolveAndApply(
        double now)
    {
        bool changed = false;
        string changeSource = null;

        if (
            _runner != null &&
            _trackingInputWidth.valid
        )
        {
            int resolvedWidth =
                _trackingInputWidth.value;

            _resolvedTrackingInputWidth =
                resolvedWidth;

            if (
                _runner.trackingInputMaxWidth !=
                resolvedWidth
            )
            {
                _runner.trackingInputMaxWidth =
                    resolvedWidth;

                changed = true;
                changeSource =
                    _trackingInputWidth.source;
            }
        }

        FloatRequest refreshRequest =
            ResolveMediaPipeRefreshRequest(
                now);

        if (
            _runner != null &&
            refreshRequest.valid
        )
        {
            float resolvedRefresh =
                Mathf.Clamp(
                    refreshRequest.value,
                    2f,
                    30f);

            _resolvedMediaPipeRefreshHz =
                resolvedRefresh;

            if (
                !Mathf.Approximately(
                    _runner.sentisMediaPipeRefreshRateHz,
                    resolvedRefresh)
            )
            {
                _runner.sentisMediaPipeRefreshRateHz =
                    resolvedRefresh;

                changed = true;
                changeSource =
                    refreshRequest.source;
            }
        }

        FloatRequest presenceRequest =
            ResolvePresenceRequest(
                now);

        if (
            _runner != null &&
            presenceRequest.valid
        )
        {
            float resolvedPresence =
                Mathf.Clamp(
                    presenceRequest.value,
                    0.05f,
                    0.99f);

            _resolvedPresenceThreshold =
                resolvedPresence;

            bool fieldChanged =
                !Mathf.Approximately(
                    _runner.sentisMinimumPresence,
                    resolvedPresence);

            if (fieldChanged)
            {
                _runner.sentisMinimumPresence =
                    resolvedPresence;

                changed = true;
                changeSource =
                    presenceRequest.source;
            }

            if (
                fieldChanged ||
                now >=
                    _nextTrackerPresenceSyncRealtime
            )
            {
                _nextTrackerPresenceSyncRealtime =
                    now + 0.50;

                SynchronizeLiveTrackerPresence(
                    resolvedPresence);
            }
        }

        if (
            _liveMotion != null &&
            _live2DPolicy.valid
        )
        {
            bool liveConfigChanged =
                _liveMotion.trackingLongSide !=
                    _live2DPolicy.trackingLongSide ||
                _liveMotion.patchStridePixels !=
                    _live2DPolicy.patchStridePixels ||
                _liveMotion.restingSearchRadiusPixels !=
                    _live2DPolicy.restingSearchRadiusPixels;

            _resolvedLiveTrackingLongSide =
                _live2DPolicy.trackingLongSide;
            _resolvedPatchStridePixels =
                _live2DPolicy.patchStridePixels;
            _resolvedRestingSearchRadiusPixels =
                _live2DPolicy.restingSearchRadiusPixels;
            _resolvedAuxiliaryCadenceScale =
                _live2DPolicy.auxiliaryCadenceScale;

            if (liveConfigChanged)
            {
                // Advance BEFORE mutating the public config. Any callback from
                // the old Live2D configuration becomes stale immediately.
                KiwiRuntimeGenerationContext
                    .AdvanceConfigEpoch();

                _liveMotion.trackingLongSide =
                    _live2DPolicy.trackingLongSide;

                _liveMotion.patchStridePixels =
                    _live2DPolicy.patchStridePixels;

                _liveMotion.restingSearchRadiusPixels =
                    _live2DPolicy.restingSearchRadiusPixels;

                changed = true;
                changeSource =
                    _live2DPolicy.source;
            }
        }

        if (changed)
        {
            _policyVersion++;

            _lastPolicySource =
                string.IsNullOrEmpty(
                    changeSource)
                    ? "RuntimePolicyResolver"
                    : changeSource;
        }
    }

    private static FloatRequest
        ResolveMediaPipeRefreshRequest(
            double now)
    {
        bool runtimeValid =
            _runtimeMediaPipeRefreshHz.valid &&
            now <=
                _runtimeMediaPipeRefreshHz
                    .expiresRealtime;

        if (runtimeValid)
        {
            return new FloatRequest
            {
                valid = true,
                value =
                    _runtimeMediaPipeRefreshHz.value,
                priority =
                    _runtimeMediaPipeRefreshHz.priority,
                source =
                    _runtimeMediaPipeRefreshHz.source
            };
        }

        return
            _baselineMediaPipeRefreshHz;
    }

    private static FloatRequest
        ResolvePresenceRequest(
            double now)
    {
        if (_userPresenceOverride.valid)
        {
            return
                _userPresenceOverride;
        }

        bool adaptiveValid =
            _adaptivePresenceThreshold.valid &&
            now <=
                _adaptivePresenceThreshold
                    .expiresRealtime;

        if (adaptiveValid)
        {
            return new FloatRequest
            {
                valid = true,
                value =
                    _adaptivePresenceThreshold.value,
                priority =
                    _adaptivePresenceThreshold.priority,
                source =
                    _adaptivePresenceThreshold.source
            };
        }

        return
            _baselinePresenceThreshold;
    }

    private void SynchronizeLiveTrackerPresence(
        float value)
    {
        if (
            _runner == null ||
            SentisTrackerField == null
        )
        {
            return;
        }

        try
        {
            object tracker =
                SentisTrackerField.GetValue(
                    _runner);

            if (tracker == null)
            {
                _lastSynchronizedTracker =
                    null;

                return;
            }

            PropertyInfo minimumPresence =
                tracker.GetType().GetProperty(
                    "MinimumPresence",
                    BindingFlags.Instance |
                    BindingFlags.Public);

            if (
                minimumPresence == null ||
                !minimumPresence.CanWrite
            )
            {
                return;
            }

            bool trackerChanged =
                !ReferenceEquals(
                    tracker,
                    _lastSynchronizedTracker);

            float current =
                value;

            if (
                minimumPresence.CanRead &&
                minimumPresence.GetValue(
                    tracker) is float existing
            )
            {
                current =
                    existing;
            }

            if (
                trackerChanged ||
                !Mathf.Approximately(
                    current,
                    value)
            )
            {
                minimumPresence.SetValue(
                    tracker,
                    value);
            }

            _lastSynchronizedTracker =
                tracker;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning(
                "[KiwiAvatarSystem] Runtime Policy Resolver could not " +
                "synchronize the live inference presence threshold: " +
                exception.Message,
                this);
        }
    }

    private void RefreshReferences(
        bool force)
    {
        if (
            force ||
            _runner == null
        )
        {
            _runner =
                FindFirstObjectByType<
                    FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _liveMotion == null
        )
        {
            _liveMotion =
                FindFirstObjectByType<
                    KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugPolicyVersion =
            _policyVersion;

        debugLastPolicySource =
            _lastPolicySource;

        debugTrackingInputWidth =
            _resolvedTrackingInputWidth;

        debugMediaPipeRefreshHz =
            _resolvedMediaPipeRefreshHz;

        debugPresenceThreshold =
            _resolvedPresenceThreshold;

        debugLiveTrackingLongSide =
            _resolvedLiveTrackingLongSide;

        debugPatchStridePixels =
            _resolvedPatchStridePixels;

        debugRestingSearchRadiusPixels =
            _resolvedRestingSearchRadiusPixels;

        debugAuxiliaryCadenceScale =
            _resolvedAuxiliaryCadenceScale;
    }

    private static string NormalizeSource(
        string source)
    {
        return
            string.IsNullOrEmpty(source)
                ? "Unknown"
                : source;
    }
}
