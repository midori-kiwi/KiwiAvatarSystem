using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v3.7 tracking continuity state machine.
///
/// Mature VTuber software separates "tracking data exists" from the user-facing
/// continuity policy used during short stalls, degraded cadence, provider
/// changes and reacquisition. This component owns no avatar transform and no
/// face-part material. It only classifies the currently published tracking
/// stream so the one presentation owner can react without policy flapping.
/// </summary>
[DefaultExecutionOrder(-18000)]
[DisallowMultipleComponent]
public sealed class KiwiTrackingContinuityState : MonoBehaviour
{
    public enum ContinuityState
    {
        Starting = 0,
        Stable = 1,
        Degraded = 2,
        Holding = 3,
        Reacquiring = 4,
        Lost = 5
    }

    private const string RuntimeObjectName =
        "[Kiwi] Tracking Continuity State";

    // v4.7 commercial phase-safe continuity access.
    // Rigid presentation must not rediscover this component every render frame.
    private static KiwiTrackingContinuityState _instance;

    public static bool HasRuntimeInstance =>
        _instance != null;

    public static bool TryGetRuntimeStatus(
        out ContinuityState state,
        out float sourceAgeSeconds,
        out float predictionAllowance,
        out float cadenceJitterRatio)
    {
        if (_instance == null)
        {
            state = ContinuityState.Starting;
            sourceAgeSeconds = float.PositiveInfinity;
            predictionAllowance = 0f;
            cadenceJitterRatio = 1f;
            return false;
        }

        state = _instance.State;
        sourceAgeSeconds = _instance.SourceAgeSeconds;
        predictionAllowance = _instance.PredictionAllowance;
        cadenceJitterRatio = _instance.CadenceJitterRatio;
        return true;
    }

    [Header("Freshness")]
    [Range(1.2f, 5f)]
    public float freshIntervalMultiplier = 2.4f;

    [Range(0.05f, 0.25f)]
    public float minimumFreshAgeSeconds = 0.08f;

    [Range(0.15f, 0.50f)]
    public float maximumFreshAgeSeconds = 0.18f;

    [Range(0.20f, 0.80f)]
    public float degradedMaximumAgeSeconds = 0.24f;

    [Range(0.35f, 1.50f)]
    public float lostAgeSeconds = 0.65f;

    [Header("Pipeline latency classification")]
    [Tooltip("A live stream older than this is Degraded rather than frozen. Source age describes latency, not whether new frames are still arriving.")]
    [Range(0.10f, 0.50f)]
    public float maximumStableSourceAgeSeconds = 0.26f;

    [Tooltip("Absolute usable source-age bound. The Provider Hub normally enforces the same or tighter hard ceiling.")]
    [Range(0.20f, 0.80f)]
    public float maximumUsableSourceAgeSeconds = 0.45f;

    [Tooltip("A brief same-provider Holding gap can return directly to Stable/Degraded without a full reacquisition ceremony.")]
    [Range(0.05f, 0.40f)]
    public float shortHoldResumeSeconds = 0.22f;

    [Header("Quality")]
    [Range(0f, 1f)]
    public float minimumStableGeometryQuality = 0.45f;

    [Range(0f, 1f)]
    public float minimumUsableGeometryQuality = 0.20f;

    [Header("Reacquisition")]
    [Range(1, 5)]
    public int reacquireConfirmationFrames = 2;

    [Range(0.02f, 0.40f)]
    public float reacquireMinimumSeconds = 0.08f;

    [Header("Diagnostics")]
    [SerializeField] private ContinuityState debugState =
        ContinuityState.Starting;
    [SerializeField] private string debugProvider = "-";
    [SerializeField] private float debugArrivalAgeMs;
    [SerializeField] private float debugCadenceHz;
    [SerializeField] private float debugCadenceJitterRatio;
    [SerializeField] private float debugGeometryQuality;
    [SerializeField] private int debugReacquireStreak;
    [SerializeField] private float debugQualityFactor;
    [SerializeField] private float debugPredictionAllowance;
    [SerializeField] private float debugRecommendedMediaPipeHz = 6f;

    private KiwiTrackingProviderHub _hub;
    private FaceLandmarkerRunner _runner;

    private ContinuityState _state =
        ContinuityState.Starting;

    private string _providerId =
        string.Empty;

    private ulong _lastFrameId;
    private long _lastArrivalHostTicks;
    private long _lastSourceHostTicks;
    private float _intervalEma = 1f / 15f;
    private float _intervalDeviationEma;
    private int _reacquireStreak;
    private double _reacquireStartedRealtime;
    private bool _hasEverHadTracking;

    public ContinuityState State =>
        _state;

    public string ProviderId =>
        _providerId;

    // v5.0 separates two clocks: ArrivalAgeSeconds controls stream liveness,
    // while SourceAgeSeconds controls latency quality/prediction. A result can
    // be old at the camera boundary while the provider is still delivering a
    // healthy cadence; that should be Degraded, not repeatedly frozen.
    public float ArrivalAgeSeconds { get; private set; }

    public float SourceAgeSeconds { get; private set; }

    public float CadenceHz =>
        _intervalEma > 0.0001f
            ? 1f / _intervalEma
            : 0f;

    public float CadenceJitterRatio =>
        _intervalEma > 0.0001f
            ? _intervalDeviationEma / _intervalEma
            : 0f;

    public int ReacquireStreak =>
        _reacquireStreak;

    public bool IsStable =>
        _state == ContinuityState.Stable;

    public bool IsHoldingOrLost =>
        _state == ContinuityState.Holding ||
        _state == ContinuityState.Lost;

    public float QualityFactor
    {
        get
        {
            switch (_state)
            {
                case ContinuityState.Stable:
                    return 1f;
                case ContinuityState.Degraded:
                    return 0.68f;
                case ContinuityState.Reacquiring:
                    return Mathf.Lerp(
                        0.45f,
                        0.78f,
                        Mathf.Clamp01(
                            _reacquireStreak /
                            (float)Mathf.Max(1, reacquireConfirmationFrames)));
                case ContinuityState.Holding:
                    return 0.30f;
                case ContinuityState.Lost:
                    return 0f;
                default:
                    return 0.35f;
            }
        }
    }

    public float PredictionAllowance
    {
        get
        {
            switch (_state)
            {
                case ContinuityState.Stable:
                    return 1f;
                case ContinuityState.Degraded:
                    return 0.46f;
                case ContinuityState.Reacquiring:
                    return 0.28f;
                case ContinuityState.Holding:
                    return 0f;
                case ContinuityState.Lost:
                    return 0f;
                default:
                    return 0.25f;
            }
        }
    }

    public float RecommendedMediaPipeRefreshHz
    {
        get
        {
            bool inference =
                !string.IsNullOrEmpty(_providerId) &&
                _providerId.IndexOf(
                    "InferenceEngine",
                    StringComparison.OrdinalIgnoreCase) >= 0;

            switch (_state)
            {
                case ContinuityState.Stable:
                    return inference ? 4.5f : 6f;
                case ContinuityState.Degraded:
                    return 8f;
                case ContinuityState.Reacquiring:
                    return 12f;
                case ContinuityState.Holding:
                    return 10f;
                case ContinuityState.Lost:
                    return 12f;
                default:
                    return 8f;
            }
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiTrackingContinuityState>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiTrackingContinuityState>();
    }

    private void Awake()
    {
        _instance = this;

        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences();
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _hub = null;
        _runner = null;
        _lastFrameId = 0UL;
        _lastArrivalHostTicks = 0L;
        _lastSourceHostTicks = 0L;
        ArrivalAgeSeconds = float.PositiveInfinity;
        SourceAgeSeconds = float.PositiveInfinity;
        _reacquireStreak = 0;
        _reacquireStartedRealtime = 0.0;
        _providerId = string.Empty;
        _state = ContinuityState.Starting;
        _hasEverHadTracking = false;

        RefreshReferences();
    }

    private void Update()
    {
        RefreshReferences();

        FacePrecisionTrackingData data =
            default;

        string provider =
            string.Empty;

        bool hasData = false;

        // The Provider Hub is authoritative whenever it exists. Do not bypass
        // its source-age ceiling with a Runner-direct fallback merely because
        // the newest ML result is stale. Runner-direct is compatibility only
        // for scenes where the Hub itself is not installed.
        if (_hub != null)
        {
            hasData =
                _hub.TryGetLatestFrame(
                    out data,
                    out provider);
        }
        else if (
            _runner != null &&
            _runner.TryGetLatestPrecisionTrackingData(
                out data)
        )
        {
            hasData =
                data.isValid;

            provider =
                data.backend ==
                    KiwiTrackingBackend.InferenceEngine
                    ? "Runner/InferenceEngine"
                    : "Runner/MediaPipe";
        }

        bool valid =
            hasData &&
            data.isValid &&
            data.frameId > 0UL;

        if (!valid)
        {
            // The Hub intentionally stops publishing a rigid frame once its
            // source-age ceiling is exceeded. That must NOT be interpreted as
            // an immediate hard loss: mature realtime mocap holds the last
            // trusted pose through a short processing stall, and returns to
            // neutral only after the continuity loss horizon expires.
            long continuityNowTicks =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();

            if (
                _hasEverHadTracking &&
                _lastSourceHostTicks > 0L
            )
            {
                ArrivalAgeSeconds =
                    _lastArrivalHostTicks > 0L &&
                    continuityNowTicks > _lastArrivalHostTicks
                        ? (float)(
                            (continuityNowTicks - _lastArrivalHostTicks) /
                            (double)System.Diagnostics.Stopwatch.Frequency)
                        : float.PositiveInfinity;

                SourceAgeSeconds =
                    continuityNowTicks > _lastSourceHostTicks
                        ? (float)(
                            (continuityNowTicks - _lastSourceHostTicks) /
                            (double)System.Diagnostics.Stopwatch.Frequency)
                        : 0f;

                // v4.8: once the authoritative Hub refuses to publish a
                // source-age-expired frame, presentation must HOLD the last
                // trusted rigid pose rather than keep classifying the gap as
                // Degraded. Degraded is for a still-adoptable frame with lower
                // quality/cadence; an expired observation must never continue
                // moving the avatar root.
                SetState(
                    ArrivalAgeSeconds <= lostAgeSeconds
                        ? ContinuityState.Holding
                        : ContinuityState.Lost);
            }
            else
            {
                ArrivalAgeSeconds =
                    float.PositiveInfinity;

                SourceAgeSeconds =
                    float.PositiveInfinity;

                SetState(
                    ContinuityState.Starting);
            }

            UpdateDiagnostics(
                data,
                string.IsNullOrEmpty(provider)
                    ? _providerId
                    : provider);

            return;
        }

        long arrivalTicks =
            data.arrivalHostTicks;

        if (arrivalTicks <= 0L)
        {
            arrivalTicks =
                System.Diagnostics.Stopwatch
                    .GetTimestamp();
        }

        long sourceTicks =
            data.hasMatchedSubmissionTiming &&
            data.submissionHostTicks > 0L
                ? data.submissionHostTicks
                : arrivalTicks;

        bool providerChanged =
            !string.IsNullOrEmpty(_providerId) &&
            !string.IsNullOrEmpty(provider) &&
            !string.Equals(
                provider,
                _providerId,
                StringComparison.Ordinal);

        bool newFrame =
            data.frameId !=
                _lastFrameId;

        if (newFrame)
        {
            ObserveNewFrame(
                data,
                provider,
                arrivalTicks,
                sourceTicks,
                providerChanged);
        }

        long nowTicks =
            System.Diagnostics.Stopwatch
                .GetTimestamp();

        ArrivalAgeSeconds =
            nowTicks > arrivalTicks
                ? (float)(
                    (nowTicks - arrivalTicks) /
                    (double)
                    System.Diagnostics.Stopwatch.Frequency)
                : 0f;

        SourceAgeSeconds =
            nowTicks > sourceTicks
                ? (float)(
                    (nowTicks - sourceTicks) /
                    (double)
                    System.Diagnostics.Stopwatch.Frequency)
                : 0f;

        float freshAge =
            Mathf.Clamp(
                _intervalEma *
                    freshIntervalMultiplier,
                minimumFreshAgeSeconds,
                maximumFreshAgeSeconds);

        // KIWI_V5_0_ARRIVAL_CONTINUITY_CLASSIFICATION
        // Arrival age answers "is the tracker still producing frames?".
        // Source age answers "how much end-to-end latency do those frames carry?".
        // Conflating them caused the avatar to freeze/reacquire several times per
        // second even while a 20-30 Hz provider remained active.
        bool arrivalFresh =
            ArrivalAgeSeconds <= freshAge;

        bool arrivalUsable =
            ArrivalAgeSeconds <=
                degradedMaximumAgeSeconds;

        bool sourceStable =
            SourceAgeSeconds <=
                maximumStableSourceAgeSeconds;

        bool sourceUsable =
            SourceAgeSeconds <=
                maximumUsableSourceAgeSeconds;

        if (_state == ContinuityState.Reacquiring)
        {
            double reacquireAge =
                Time.realtimeSinceStartupAsDouble -
                _reacquireStartedRealtime;

            if (
                _reacquireStreak >=
                    Mathf.Max(1, reacquireConfirmationFrames) &&
                reacquireAge >=
                    reacquireMinimumSeconds &&
                arrivalFresh &&
                sourceUsable
            )
            {
                SetState(
                    sourceStable &&
                    data.geometryQuality >=
                        minimumStableGeometryQuality
                        ? ContinuityState.Stable
                        : ContinuityState.Degraded);
            }
        }
        else if (
            arrivalFresh &&
            sourceStable &&
            data.geometryQuality >=
                minimumStableGeometryQuality
        )
        {
            SetState(
                ContinuityState.Stable);
        }
        else if (
            arrivalUsable &&
            sourceUsable &&
            data.geometryQuality >=
                minimumUsableGeometryQuality
        )
        {
            SetState(
                ContinuityState.Degraded);
        }
        else if (
            ArrivalAgeSeconds <=
                lostAgeSeconds
        )
        {
            SetState(
                ContinuityState.Holding);
        }
        else
        {
            SetState(
                ContinuityState.Lost);
        }

        UpdateDiagnostics(
            data,
            provider);
    }

    private void ObserveNewFrame(
        FacePrecisionTrackingData data,
        string provider,
        long arrivalTicks,
        long sourceTicks,
        bool providerChanged)
    {
        if (
            _lastArrivalHostTicks > 0L &&
            arrivalTicks > _lastArrivalHostTicks
        )
        {
            float interval =
                (float)(
                    (arrivalTicks - _lastArrivalHostTicks) /
                    (double)
                    System.Diagnostics.Stopwatch.Frequency);

            if (
                interval > 0.001f &&
                interval < 1f
            )
            {
                float deviation =
                    Mathf.Abs(
                        interval -
                        _intervalEma);

                _intervalDeviationEma =
                    Mathf.Lerp(
                        _intervalDeviationEma,
                        deviation,
                        0.18f);

                _intervalEma =
                    Mathf.Lerp(
                        _intervalEma,
                        interval,
                        0.16f);
            }
        }

        float observedGapSeconds =
            _lastArrivalHostTicks > 0L &&
            arrivalTicks > _lastArrivalHostTicks
                ? (float)(
                    (arrivalTicks - _lastArrivalHostTicks) /
                    (double)System.Diagnostics.Stopwatch.Frequency)
                : float.PositiveInfinity;

        bool shortSameProviderResume =
            _state == ContinuityState.Holding &&
            !providerChanged &&
            observedGapSeconds <=
                shortHoldResumeSeconds;

        bool recovering =
            _state == ContinuityState.Lost ||
            _state == ContinuityState.Starting ||
            providerChanged ||
            (
                _state == ContinuityState.Holding &&
                !shortSameProviderResume
            );

        if (recovering)
        {
            if (_state != ContinuityState.Reacquiring)
            {
                _reacquireStreak = 0;
                _reacquireStartedRealtime =
                    Time.realtimeSinceStartupAsDouble;
            }

            _reacquireStreak++;
            SetState(
                ContinuityState.Reacquiring);
        }
        else if (shortSameProviderResume)
        {
            // One short cadence miss is not a new face acquisition. The main
            // Update classification immediately places this fresh frame into
            // Stable or Degraded, avoiding a visible Reacquiring staircase.
            _reacquireStreak =
                Mathf.Max(
                    _reacquireStreak,
                    1);
        }
        else if (_state == ContinuityState.Reacquiring)
        {
            _reacquireStreak++;
        }
        else
        {
            _reacquireStreak =
                Mathf.Max(
                    _reacquireStreak,
                    1);
        }

        _lastFrameId =
            data.frameId;

        _lastArrivalHostTicks =
            arrivalTicks;

        _lastSourceHostTicks =
            sourceTicks;

        _providerId =
            provider;

        _hasEverHadTracking =
            true;
    }

    private void SetState(
        ContinuityState state)
    {
        if (_state == state)
        {
            return;
        }

        _state =
            state;

        if (
            state == ContinuityState.Lost ||
            state == ContinuityState.Holding
        )
        {
            _reacquireStreak =
                0;
        }
    }

    private void RefreshReferences()
    {
        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }

        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics(
        FacePrecisionTrackingData data,
        string provider)
    {
        debugState =
            _state;

        debugProvider =
            string.IsNullOrEmpty(provider)
                ? _providerId
                : provider;

        debugArrivalAgeMs =
            float.IsInfinity(ArrivalAgeSeconds)
                ? 9999f
                : ArrivalAgeSeconds * 1000f;

        debugCadenceHz =
            CadenceHz;

        debugCadenceJitterRatio =
            CadenceJitterRatio;

        debugGeometryQuality =
            data.isValid
                ? data.geometryQuality
                : 0f;

        debugReacquireStreak =
            _reacquireStreak;

        debugQualityFactor =
            QualityFactor;

        debugPredictionAllowance =
            PredictionAllowance;

        debugRecommendedMediaPipeHz =
            RecommendedMediaPipeRefreshHz;
    }
}
