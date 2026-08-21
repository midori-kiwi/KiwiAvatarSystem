using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v4.4 bounded runtime quality governor.
///
/// Commercial tracking applications expose quality/performance levels and keep
/// tracking cadence independent from presentation cadence. This controller only
/// changes expensive AUXILIARY budgets at slow tier boundaries:
///
/// - live 2D tracker resolution/stride/search-rest radius
/// - mature-supervisor auxiliary MediaPipe cadence multiplier
///
/// It never lowers render target FPS and never changes avatar transforms,
/// semantic landmark meaning, calibration, or user motion mapping.
/// </summary>
[DefaultExecutionOrder(-16750)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialQualityGovernor : MonoBehaviour
{
    public enum QualityMode
    {
        Auto = 0,
        Quality = 1,
        Balanced = 2,
        Realtime = 3
    }

    public enum RuntimeTier
    {
        Quality = 0,
        Balanced = 1,
        Realtime = 2
    }

    private const string RuntimeObjectName =
        "[Kiwi] Commercial Quality Governor";

    private const string ModeKey =
        "Kiwi.CommercialQualityGovernor.v1.Mode";

    [Header("Mode")]
    public QualityMode mode =
        QualityMode.Auto;

    public bool persistMode = true;

    [Tooltip("Desired presentation target. On lower-refresh displays the actual display refresh becomes the practical target.")]
    [Range(30f, 240f)]
    public float targetRenderFps = 120f;

    [Header("Auto tier thresholds")]
    [Range(0.40f, 0.95f)]
    public float realtimeEnterFpsRatio = 0.72f;

    [Range(0.50f, 1f)]
    public float balancedEnterFpsRatio = 0.86f;

    [Range(0.60f, 1.10f)]
    public float qualityEnterFpsRatio = 0.96f;

    [Range(0.5f, 8f)]
    public float downgradeHoldSeconds = 1.5f;

    [Range(1f, 15f)]
    public float upgradeHoldSeconds = 5f;

    [Range(1f, 20f)]
    public float minimumTierDwellSeconds = 3f;

    [Header("Quality tier")]
    [Range(384, 768)]
    public int qualityLiveTrackingLongSide = 512;

    [Range(1, 4)]
    public int qualityPatchStride = 2;

    [Range(6, 24)]
    public int qualityRestingSearchRadius = 12;

    [Range(0.70f, 1.30f)]
    public float qualityAuxiliaryCadenceScale = 1.05f;

    [Header("Balanced tier")]
    [Range(320, 768)]
    public int balancedLiveTrackingLongSide = 448;

    [Range(1, 4)]
    public int balancedPatchStride = 3;

    [Range(6, 24)]
    public int balancedRestingSearchRadius = 10;

    [Range(0.70f, 1.30f)]
    public float balancedAuxiliaryCadenceScale = 1f;

    [Header("Realtime tier")]
    [Range(256, 640)]
    public int realtimeLiveTrackingLongSide = 352;

    [Range(1, 4)]
    public int realtimePatchStride = 4;

    [Range(6, 24)]
    public int realtimeRestingSearchRadius = 8;

    [Range(0.70f, 1.30f)]
    public float realtimeAuxiliaryCadenceScale = 0.90f;

    [Header("Diagnostics")]
    [SerializeField] private RuntimeTier debugTier =
        RuntimeTier.Balanced;

    [SerializeField] private float debugRenderFps;
    [SerializeField] private float debugEffectiveTargetFps;
    [SerializeField] private float debugSourceHz;
    [SerializeField] private float debugResultHz;
    [SerializeField] private float debugReadbackMs;
    [SerializeField] private float debugLocalReadbackMs;
    [SerializeField] private string debugReason = "Starting";

    private FaceLandmarkerRunner _runner;
    private KiwiTrackingContinuityState _continuity;
    private KiwiFacePartLiveMotionBridge _liveMotion;
    private KiwiMatureVTuberSupervisor _supervisor;

    private RuntimeTier _tier =
        RuntimeTier.Balanced;

    private float _renderFpsEma;
    private double _candidateSinceRealtime;
    private RuntimeTier _candidateTier =
        RuntimeTier.Balanced;

    private double _lastTierChangeRealtime;
    private double _nextDecisionRealtime;
    private double _nextReferenceRefreshRealtime;

    public RuntimeTier CurrentTier =>
        _tier;

    public string CurrentTierName =>
        _tier.ToString();

    public float RenderFps =>
        _renderFpsEma;

    public string DecisionReason =>
        debugReason;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCommercialQualityGovernor>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiCommercialQualityGovernor>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences(true);

        if (
            persistMode &&
            PlayerPrefs.HasKey(
                ModeKey)
        )
        {
            mode =
                (QualityMode)
                Mathf.Clamp(
                    PlayerPrefs.GetInt(
                        ModeKey,
                        (int)QualityMode.Auto),
                    0,
                    3);
        }

        _renderFpsEma =
            Mathf.Max(
                30f,
                targetRenderFps);

        ApplyTier(
            ResolveManualTier());
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
        _continuity = null;
        _liveMotion = null;
        _supervisor = null;

        _candidateSinceRealtime =
            0.0;

        _lastTierChangeRealtime =
            0.0;

        _nextDecisionRealtime =
            0.0;

        _nextReferenceRefreshRealtime =
            0.0;

        RefreshReferences(true);

        ApplyTier(
            ResolveManualTier());
    }

    private void Update()
    {
        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.10f);

        float instantaneousFps =
            1f /
            Mathf.Max(
                0.0001f,
                dt);

        _renderFpsEma =
            Mathf.Lerp(
                _renderFpsEma,
                instantaneousFps,
                1f -
                Mathf.Exp(
                    -4f *
                    dt));

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

        if (
            now <
            _nextDecisionRealtime
        )
        {
            return;
        }

        _nextDecisionRealtime =
            now + 0.25;

        EvaluateTier(
            now);
    }

    public void SetMode(
        QualityMode newMode)
    {
        mode =
            newMode;

        if (persistMode)
        {
            PlayerPrefs.SetInt(
                ModeKey,
                (int)mode);

            PlayerPrefs.Save();
        }

        _candidateSinceRealtime =
            0.0;

        _candidateTier =
            ResolveManualTier();

        if (
            newMode !=
            QualityMode.Auto
        )
        {
            ApplyTier(
                _candidateTier);
        }
    }

    private void EvaluateTier(
        double now)
    {
        if (
            mode !=
            QualityMode.Auto
        )
        {
            RuntimeTier manual =
                ResolveManualTier();

            if (_tier != manual)
            {
                ApplyTier(manual);
            }

            debugReason =
                "Manual";

            UpdateDiagnostics();
            return;
        }

        float displayRefresh =
            Mathf.Max(
                60f,
                Screen.currentResolution
                    .refreshRate);

        float effectiveTarget =
            Mathf.Clamp(
                Mathf.Min(
                    targetRenderFps,
                    displayRefresh),
                30f,
                240f);

        debugEffectiveTargetFps =
            effectiveTarget;

        float fpsRatio =
            _renderFpsEma /
            Mathf.Max(
                1f,
                effectiveTarget);

        float sourceHz =
            _runner != null
                ? _runner.LatestFreshSourceRateHz
                : 0f;

        float resultHz =
            _runner != null
                ? _runner.LatestTrackingResultRateHz
                : 0f;

        float readbackMs =
            _runner != null
                ? _runner.LatestReadbackLatencyMs
                : 0f;

        float localReadbackMs =
            _liveMotion != null
                ? _liveMotion.ReadbackLatencyMs
                : 0f;

        bool holdingOrLost =
            _continuity != null &&
            _continuity.IsHoldingOrLost;

        bool degraded =
            _continuity != null &&
            (
                _continuity.State ==
                    KiwiTrackingContinuityState
                        .ContinuityState
                        .Degraded ||
                _continuity.State ==
                    KiwiTrackingContinuityState
                        .ContinuityState
                        .Reacquiring
            );

        RuntimeTier desired;

        if (
            fpsRatio <
                realtimeEnterFpsRatio ||
            localReadbackMs >
                18f
        )
        {
            desired =
                RuntimeTier.Realtime;

            debugReason =
                fpsRatio <
                    realtimeEnterFpsRatio
                    ? "RenderBudget"
                    : "Live2DReadback";
        }
        else if (
            fpsRatio <
                balancedEnterFpsRatio ||
            holdingOrLost ||
            degraded
        )
        {
            desired =
                RuntimeTier.Balanced;

            debugReason =
                holdingOrLost
                    ? "TrackingContinuity"
                    : degraded
                        ? "TrackingDegraded"
                        : "RenderHeadroom";
        }
        else if (
            fpsRatio >=
                qualityEnterFpsRatio &&
            sourceHz >=
                24f &&
            !holdingOrLost
        )
        {
            desired =
                RuntimeTier.Quality;

            debugReason =
                "Headroom";
        }
        else
        {
            desired =
                RuntimeTier.Balanced;

            debugReason =
                "Balanced";
        }

        if (desired == _tier)
        {
            _candidateTier =
                desired;

            _candidateSinceRealtime =
                0.0;

            UpdateDiagnostics();
            return;
        }

        if (_candidateTier != desired)
        {
            _candidateTier =
                desired;

            _candidateSinceRealtime =
                now;

            UpdateDiagnostics();
            return;
        }

        if (
            now -
                _lastTierChangeRealtime <
            minimumTierDwellSeconds
        )
        {
            UpdateDiagnostics();
            return;
        }

        bool downgrade =
            (int)desired >
            (int)_tier;

        float requiredHold =
            downgrade
                ? downgradeHoldSeconds
                : upgradeHoldSeconds;

        if (
            now -
                _candidateSinceRealtime <
            requiredHold
        )
        {
            UpdateDiagnostics();
            return;
        }

        ApplyTier(
            desired);

        _candidateSinceRealtime =
            0.0;

        UpdateDiagnostics();
    }

    private RuntimeTier ResolveManualTier()
    {
        switch (mode)
        {
            case QualityMode.Quality:
                return RuntimeTier.Quality;

            case QualityMode.Realtime:
                return RuntimeTier.Realtime;

            default:
                return RuntimeTier.Balanced;
        }
    }

    private void ApplyTier(
        RuntimeTier tier)
    {
        _tier =
            tier;

        _lastTierChangeRealtime =
            Time.realtimeSinceStartupAsDouble;

        RefreshReferences(false);

        if (_liveMotion != null)
        {
            switch (tier)
            {
                case RuntimeTier.Quality:
                    _liveMotion.trackingLongSide =
                        qualityLiveTrackingLongSide;

                    _liveMotion.patchStridePixels =
                        qualityPatchStride;

                    _liveMotion.restingSearchRadiusPixels =
                        qualityRestingSearchRadius;
                    break;

                case RuntimeTier.Realtime:
                    _liveMotion.trackingLongSide =
                        realtimeLiveTrackingLongSide;

                    _liveMotion.patchStridePixels =
                        realtimePatchStride;

                    _liveMotion.restingSearchRadiusPixels =
                        realtimeRestingSearchRadius;
                    break;

                default:
                    _liveMotion.trackingLongSide =
                        balancedLiveTrackingLongSide;

                    _liveMotion.patchStridePixels =
                        balancedPatchStride;

                    _liveMotion.restingSearchRadiusPixels =
                        balancedRestingSearchRadius;
                    break;
            }
        }

        if (_supervisor != null)
        {
            switch (tier)
            {
                case RuntimeTier.Quality:
                    _supervisor.runtimeAuxiliaryCadenceScale =
                        qualityAuxiliaryCadenceScale;
                    break;

                case RuntimeTier.Realtime:
                    _supervisor.runtimeAuxiliaryCadenceScale =
                        realtimeAuxiliaryCadenceScale;
                    break;

                default:
                    _supervisor.runtimeAuxiliaryCadenceScale =
                        balancedAuxiliaryCadenceScale;
                    break;
            }
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
            _continuity == null
        )
        {
            _continuity =
                FindFirstObjectByType<
                    KiwiTrackingContinuityState>(
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

        if (
            force ||
            _supervisor == null
        )
        {
            _supervisor =
                FindFirstObjectByType<
                    KiwiMatureVTuberSupervisor>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugTier =
            _tier;

        debugRenderFps =
            _renderFpsEma;

        debugSourceHz =
            _runner != null
                ? _runner.LatestFreshSourceRateHz
                : 0f;

        debugResultHz =
            _runner != null
                ? _runner.LatestTrackingResultRateHz
                : 0f;

        debugReadbackMs =
            _runner != null
                ? _runner.LatestReadbackLatencyMs
                : 0f;

        debugLocalReadbackMs =
            _liveMotion != null
                ? _liveMotion.ReadbackLatencyMs
                : 0f;
    }
}
