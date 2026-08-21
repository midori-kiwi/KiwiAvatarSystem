using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v3.9 measured latency budget.
///
/// Smoothing, prediction and sample reconciliation are explicit budgets with
/// different trade-offs. The same tracking core can expose low-latency,
/// balanced or stable behavior without forking the algorithm.
/// </summary>
[DefaultExecutionOrder(-16500)]
[DisallowMultipleComponent]
public sealed class KiwiLatencyBudgetController : MonoBehaviour
{
    public enum PolicyProfile
    {
        AdaptiveCommercial = 0,
        UltraLowLatency = 1,
        Balanced = 2,
        Stable = 3
    }

    private const string RuntimeObjectName =
        "[Kiwi] Latency Budget";

    private static KiwiLatencyBudgetController _instance;

    public static bool HasRuntimeInstance =>
        _instance != null;

    public static bool TryGetRuntimeBudget(
        out float sourceAgeSeconds,
        out float predictionBudgetSeconds,
        out float predictionStrengthMultiplier,
        out float cadenceJitterRatio)
    {
        if (_instance == null)
        {
            sourceAgeSeconds = 0f;
            predictionBudgetSeconds = 0f;
            predictionStrengthMultiplier = 1f;
            cadenceJitterRatio = 0f;
            return false;
        }

        sourceAgeSeconds = _instance.SourceAgeSeconds;
        predictionBudgetSeconds = _instance.PredictionBudgetSeconds;
        predictionStrengthMultiplier = _instance.PredictionStrengthMultiplier;
        cadenceJitterRatio = _instance.CadenceJitterRatio;
        return true;
    }

    [Header("Policy")]
    public PolicyProfile profile =
        PolicyProfile.AdaptiveCommercial;

    [Header("Adaptive thresholds")]
    [Range(12f, 60f)]
    public float lowLatencyMinimumCadenceHz = 24f;

    [Range(0.05f, 0.60f)]
    public float lowLatencyMaximumJitterRatio = 0.18f;

    [Range(0.10f, 0.90f)]
    public float stableJitterRatio = 0.38f;

    [Header("Diagnostics")]
    [SerializeField] private PolicyProfile debugResolvedProfile;
    [SerializeField] private float debugSourceAgeMs;
    [SerializeField] private float debugCadenceHz;
    [SerializeField] private float debugJitterRatio;
    [SerializeField] private float debugPredictionBudgetMs;
    [SerializeField] private float debugPositionSmoothMultiplier = 1f;
    [SerializeField] private float debugFacePartResponseMultiplier = 1f;
    [SerializeField] private float debugReconciliationMs;

    private KiwiTrackingContinuityState _continuity;
    private KiwiTrackingProviderHub _hub;

    public PolicyProfile ResolvedProfile { get; private set; }

    public float SourceAgeSeconds { get; private set; }

    public float TrackingCadenceHz { get; private set; }

    public float CadenceJitterRatio { get; private set; }

    public float PredictionBudgetSeconds { get; private set; } =
        0.050f;

    public float PredictionStrengthMultiplier { get; private set; } =
        1f;

    public float PositionSmoothMultiplier { get; private set; } =
        1f;

    public float RotationResponseMultiplier { get; private set; } =
        1f;

    public float FacePartResponseMultiplier { get; private set; } =
        1f;

    public float FacePartPredictionBudgetSeconds { get; private set; } =
        0.045f;

    public float ReconciliationSeconds { get; private set; } =
        0.010f;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiLatencyBudgetController>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiLatencyBudgetController>();
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
        if (object.ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        _continuity = null;
        _hub = null;

        RefreshReferences();
    }

    private void Update()
    {
        RefreshReferences();
        Measure();
        Resolve();
        UpdateDiagnostics();
    }

    private void RefreshReferences()
    {
        if (_continuity == null)
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
                    FindObjectsInactive.Include);
        }

        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }
    }

    private void Measure()
    {
        TrackingCadenceHz =
            _continuity != null
                ? Mathf.Max(
                    0f,
                    _continuity.CadenceHz)
                : 0f;

        CadenceJitterRatio =
            _continuity != null
                ? Mathf.Max(
                    0f,
                    _continuity.CadenceJitterRatio)
                : 1f;

        SourceAgeSeconds = 0.20f;

        if (
            _hub != null &&
            _hub.TryGetLatestFrame(
                out FacePrecisionTrackingData data,
                out _)
        )
        {
            long sampleHostTicks =
                data.hasMatchedSubmissionTiming &&
                data.submissionHostTicks > 0L
                    ? data.submissionHostTicks
                    : data.arrivalHostTicks;

            if (sampleHostTicks > 0L)
            {
                long now =
                    System.Diagnostics.Stopwatch
                        .GetTimestamp();

                if (now > sampleHostTicks)
                {
                    SourceAgeSeconds =
                        (float)(
                            (
                                now -
                                sampleHostTicks
                            ) /
                            (double)
                            System.Diagnostics.Stopwatch
                                .Frequency);
                }
            }
        }

        SourceAgeSeconds =
            Mathf.Clamp(
                SourceAgeSeconds,
                0f,
                1f);
    }

    private void Resolve()
    {
        ResolvedProfile = profile;

        if (
            profile ==
            PolicyProfile.AdaptiveCommercial
        )
        {
            bool unstable =
                _continuity != null &&
                (
                    _continuity.State ==
                        KiwiTrackingContinuityState.ContinuityState.Degraded ||
                    _continuity.State ==
                        KiwiTrackingContinuityState.ContinuityState.Holding ||
                    _continuity.State ==
                        KiwiTrackingContinuityState.ContinuityState.Lost ||
                    CadenceJitterRatio >=
                        stableJitterRatio
                );

            bool fastAndRegular =
                _continuity != null &&
                _continuity.IsStable &&
                TrackingCadenceHz >=
                    lowLatencyMinimumCadenceHz &&
                CadenceJitterRatio <=
                    lowLatencyMaximumJitterRatio;

            ResolvedProfile =
                unstable
                    ? PolicyProfile.Stable
                    : fastAndRegular
                        ? PolicyProfile.UltraLowLatency
                        : PolicyProfile.Balanced;
        }

        float desiredResidualLatency;
        float maximumPrediction;

        switch (ResolvedProfile)
        {
            case PolicyProfile.UltraLowLatency:
                desiredResidualLatency = 0.040f;
                maximumPrediction = 0.070f;
                PredictionStrengthMultiplier = 1.06f;
                PositionSmoothMultiplier = 0.82f;
                RotationResponseMultiplier = 1.12f;
                FacePartResponseMultiplier = 1.12f;
                FacePartPredictionBudgetSeconds = 0.060f;
                ReconciliationSeconds = 0.006f;
                break;

            case PolicyProfile.Stable:
                desiredResidualLatency = 0.078f;
                maximumPrediction = 0.045f;
                PredictionStrengthMultiplier = 0.78f;
                PositionSmoothMultiplier = 1.34f;
                RotationResponseMultiplier = 0.86f;
                FacePartResponseMultiplier = 0.82f;
                FacePartPredictionBudgetSeconds = 0.036f;
                ReconciliationSeconds = 0.016f;
                break;

            default:
                desiredResidualLatency = 0.055f;
                maximumPrediction = 0.060f;
                PredictionStrengthMultiplier = 0.94f;
                PositionSmoothMultiplier = 1.00f;
                RotationResponseMultiplier = 1.00f;
                FacePartResponseMultiplier = 1.00f;
                FacePartPredictionBudgetSeconds = 0.050f;
                ReconciliationSeconds = 0.010f;
                break;
        }

        float ageCompensationNeed =
            Mathf.Max(
                0f,
                SourceAgeSeconds -
                desiredResidualLatency);

        float continuityAllowance =
            _continuity != null
                ? _continuity.PredictionAllowance
                : 0.75f;

        PredictionBudgetSeconds =
            Mathf.Clamp(
                ageCompensationNeed *
                    0.72f *
                    continuityAllowance,
                0f,
                maximumPrediction);

        // Commercial systems separate interpolation from prediction. Prediction
        // is not a mandatory minimum delay-compensation stage: when the source
        // is fresh enough, or when cadence/source age is unstable, zero lead is
        // preferable to extrapolating noise.
        float sourceAgeRisk =
            Mathf.InverseLerp(
                0.120f,
                0.240f,
                SourceAgeSeconds);

        float cadenceRisk =
            Mathf.InverseLerp(
                0.25f,
                0.75f,
                CadenceJitterRatio);

        float motionRisk =
            Mathf.Max(
                sourceAgeRisk,
                cadenceRisk);

        PredictionStrengthMultiplier *=
            Mathf.Lerp(
                1f,
                0.42f,
                motionRisk);

        if (SourceAgeSeconds >= 0.220f)
        {
            PredictionBudgetSeconds =
                Mathf.Min(
                    PredictionBudgetSeconds,
                    0.012f);

            PredictionStrengthMultiplier *=
                0.20f;
        }

        if (
            _continuity != null &&
            _continuity.IsHoldingOrLost
        )
        {
            PredictionBudgetSeconds = 0f;
            PredictionStrengthMultiplier = 0f;
        }
    }

    private void UpdateDiagnostics()
    {
        debugResolvedProfile = ResolvedProfile;
        debugSourceAgeMs = SourceAgeSeconds * 1000f;
        debugCadenceHz = TrackingCadenceHz;
        debugJitterRatio = CadenceJitterRatio;
        debugPredictionBudgetMs = PredictionBudgetSeconds * 1000f;
        debugPositionSmoothMultiplier = PositionSmoothMultiplier;
        debugFacePartResponseMultiplier = FacePartResponseMultiplier;
        debugReconciliationMs = ReconciliationSeconds * 1000f;
    }
}
