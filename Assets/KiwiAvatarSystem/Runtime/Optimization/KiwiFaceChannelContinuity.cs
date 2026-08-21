using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

[DefaultExecutionOrder(-17500)]
[DisallowMultipleComponent]
public sealed class KiwiFaceChannelContinuity : MonoBehaviour
{
    public enum ChannelState
    {
        Unavailable = 0,
        Reacquiring = 1,
        Fresh = 2,
        Aging = 3,
        Stale = 4
    }

    private const string RuntimeObjectName =
        "[Kiwi] Face Channel Continuity";

    [Header("Face geometry")]
    [Range(0.05f, 0.40f)]
    public float geometryFreshAgeSeconds = 0.22f;

    [Range(0.10f, 0.60f)]
    public float geometryStaleAgeSeconds = 0.34f;

    [Range(1, 4)]
    public int geometryReacquireFrames = 1;

    [Header("Expressions")]
    [Range(0.10f, 0.40f)]
    public float expressionFreshAgeSeconds = 0.28f;

    [Range(0.20f, 0.60f)]
    public float expressionStaleAgeSeconds = 0.35f;

    [Range(1, 5)]
    public int expressionReacquireFrames = 2;

    [Header("Eye-source policy")]
    public bool preferInferenceGeometryForBlink = true;

    [Range(0.05f, 0.30f)]
    public float inferenceGeometryBlinkMaximumAge = 0.16f;

    [Header("Diagnostics")]
    [SerializeField] private ChannelState debugGeometryState =
        ChannelState.Unavailable;
    [SerializeField] private ChannelState debugExpressionState =
        ChannelState.Unavailable;
    [SerializeField] private string debugGeometryProvider = "-";
    [SerializeField] private string debugExpressionProvider = "-";
    [SerializeField] private float debugGeometryAgeMs;
    [SerializeField] private float debugExpressionAgeMs;
    [SerializeField] private int debugGeometryReacquireStreak;
    [SerializeField] private int debugExpressionReacquireStreak;
    [SerializeField] private string debugRecommendedEyeSource = "-";

    private KiwiTrackingProviderHub _hub;

    private ChannelState _geometryState =
        ChannelState.Unavailable;

    private ChannelState _expressionState =
        ChannelState.Unavailable;

    private string _geometryProvider =
        string.Empty;

    private string _expressionProvider =
        string.Empty;

    private ulong _lastGeometryFrameId;
    private ulong _lastExpressionFrameId;

    private int _geometryReacquireStreak;
    private int _expressionReacquireStreak;

    private float _geometryAgeSeconds =
        float.PositiveInfinity;

    private float _expressionAgeSeconds =
        float.PositiveInfinity;

    public ChannelState GeometryState =>
        _geometryState;

    public ChannelState ExpressionState =>
        _expressionState;

    public string GeometryProviderId =>
        _geometryProvider;

    public string ExpressionProviderId =>
        _expressionProvider;

    public float GeometryAgeSeconds =>
        _geometryAgeSeconds;

    public float ExpressionAgeSeconds =>
        _expressionAgeSeconds;

    public bool GeometryTrusted =>
        _geometryState ==
            ChannelState.Fresh ||
        _geometryState ==
            ChannelState.Aging;

    public bool ExpressionsTrusted =>
        _expressionState ==
            ChannelState.Fresh ||
        _expressionState ==
            ChannelState.Aging;

    public bool PreferGeometryBlink
    {
        get
        {
            return
                preferInferenceGeometryForBlink &&
                GeometryTrusted &&
                _geometryAgeSeconds <=
                    inferenceGeometryBlinkMaximumAge &&
                !string.IsNullOrEmpty(
                    _geometryProvider) &&
                _geometryProvider.IndexOf(
                    "InferenceEngine",
                    StringComparison.OrdinalIgnoreCase) >=
                    0;
        }
    }

    public string RecommendedEyeSource =>
        PreferGeometryBlink
            ? "InferenceGeometry"
            : ExpressionsTrusted
                ? "Blendshape"
                : GeometryTrusted
                    ? "GeometryFallback"
                    : "Hold";

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiFaceChannelContinuity>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(
                RuntimeObjectName);

        DontDestroyOnLoad(
            host);

        host.AddComponent<
            KiwiFaceChannelContinuity>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(
            gameObject);

        SceneManager.sceneLoaded -=
            HandleSceneLoaded;

        SceneManager.sceneLoaded +=
            HandleSceneLoaded;

        RefreshReferences();
        ResetStates();
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
        _hub = null;
        RefreshReferences();
        ResetStates();
    }

    private void Update()
    {
        RefreshReferences();

        if (_hub == null)
        {
            SetGeometryUnavailable();
            SetExpressionsUnavailable();
            UpdateDiagnostics();
            return;
        }

        UpdateGeometryChannel();
        UpdateExpressionChannel();
        UpdateDiagnostics();
    }

    private void UpdateGeometryChannel()
    {
        bool valid =
            _hub.TryGetCapabilityHealth(
                KiwiTrackingProviderHub.TrackingCapability.FaceGeometry,
                out FacePrecisionTrackingData data,
                out KiwiTrackingProviderHub.CapabilityHealth health);

        if (!valid)
        {
            SetGeometryUnavailable();
            return;
        }

        _geometryProvider =
            health.providerId;

        _geometryAgeSeconds =
            Mathf.Max(
                0f,
                health.ageSeconds);

        bool newFrame =
            health.sourceFrameId !=
                0UL &&
            health.sourceFrameId !=
                _lastGeometryFrameId;

        if (newFrame)
        {
            _lastGeometryFrameId =
                health.sourceFrameId;

            if (
                _geometryState ==
                    ChannelState.Unavailable ||
                _geometryState ==
                    ChannelState.Stale ||
                _geometryState ==
                    ChannelState.Reacquiring
            )
            {
                _geometryReacquireStreak++;
            }
            else
            {
                _geometryReacquireStreak =
                    Mathf.Max(
                        _geometryReacquireStreak,
                        1);
            }
        }

        _geometryState =
            ResolveState(
                _geometryAgeSeconds,
                geometryFreshAgeSeconds,
                geometryStaleAgeSeconds,
                _geometryReacquireStreak,
                Mathf.Max(
                    1,
                    geometryReacquireFrames));
    }

    private void UpdateExpressionChannel()
    {
        bool valid =
            _hub.TryGetCapabilityHealth(
                KiwiTrackingProviderHub.TrackingCapability.Expressions,
                out FacePrecisionTrackingData data,
                out KiwiTrackingProviderHub.CapabilityHealth health);

        if (!valid)
        {
            SetExpressionsUnavailable();
            return;
        }

        _expressionProvider =
            health.providerId;

        _expressionAgeSeconds =
            Mathf.Max(
                0f,
                health.ageSeconds);

        bool newFrame =
            health.sourceFrameId !=
                0UL &&
            health.sourceFrameId !=
                _lastExpressionFrameId;

        if (newFrame)
        {
            _lastExpressionFrameId =
                health.sourceFrameId;

            if (
                _expressionState ==
                    ChannelState.Unavailable ||
                _expressionState ==
                    ChannelState.Stale ||
                _expressionState ==
                    ChannelState.Reacquiring
            )
            {
                _expressionReacquireStreak++;
            }
            else
            {
                _expressionReacquireStreak =
                    Mathf.Max(
                        _expressionReacquireStreak,
                        1);
            }
        }

        _expressionState =
            ResolveState(
                _expressionAgeSeconds,
                expressionFreshAgeSeconds,
                expressionStaleAgeSeconds,
                _expressionReacquireStreak,
                Mathf.Max(
                    1,
                    expressionReacquireFrames));
    }

    private static ChannelState ResolveState(
        float age,
        float freshAge,
        float staleAge,
        int reacquireStreak,
        int reacquireFrames)
    {
        if (
            float.IsNaN(age) ||
            float.IsInfinity(age)
        )
        {
            return
                ChannelState.Unavailable;
        }

        if (age > staleAge)
        {
            return
                ChannelState.Stale;
        }

        if (
            reacquireStreak <
                reacquireFrames
        )
        {
            return
                ChannelState.Reacquiring;
        }

        return
            age <= freshAge
                ? ChannelState.Fresh
                : ChannelState.Aging;
    }

    private void SetGeometryUnavailable()
    {
        _geometryState =
            ChannelState.Unavailable;

        _geometryProvider =
            string.Empty;

        _geometryAgeSeconds =
            float.PositiveInfinity;

        _geometryReacquireStreak =
            0;
    }

    private void SetExpressionsUnavailable()
    {
        _expressionState =
            ChannelState.Unavailable;

        _expressionProvider =
            string.Empty;

        _expressionAgeSeconds =
            float.PositiveInfinity;

        _expressionReacquireStreak =
            0;
    }

    private void ResetStates()
    {
        _geometryState =
            ChannelState.Unavailable;

        _expressionState =
            ChannelState.Unavailable;

        _geometryProvider =
            string.Empty;

        _expressionProvider =
            string.Empty;

        _lastGeometryFrameId =
            0UL;

        _lastExpressionFrameId =
            0UL;

        _geometryReacquireStreak =
            0;

        _expressionReacquireStreak =
            0;

        _geometryAgeSeconds =
            float.PositiveInfinity;

        _expressionAgeSeconds =
            float.PositiveInfinity;
    }

    private void RefreshReferences()
    {
        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<
                    KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateDiagnostics()
    {
        debugGeometryState =
            _geometryState;

        debugExpressionState =
            _expressionState;

        debugGeometryProvider =
            string.IsNullOrEmpty(
                _geometryProvider)
                ? "-"
                : _geometryProvider;

        debugExpressionProvider =
            string.IsNullOrEmpty(
                _expressionProvider)
                ? "-"
                : _expressionProvider;

        debugGeometryAgeMs =
            float.IsInfinity(
                _geometryAgeSeconds)
                ? -1f
                : _geometryAgeSeconds *
                    1000f;

        debugExpressionAgeMs =
            float.IsInfinity(
                _expressionAgeSeconds)
                ? -1f
                : _expressionAgeSeconds *
                    1000f;

        debugGeometryReacquireStreak =
            _geometryReacquireStreak;

        debugExpressionReacquireStreak =
            _expressionReacquireStreak;

        debugRecommendedEyeSource =
            RecommendedEyeSource;
    }
}
