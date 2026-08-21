using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v4.4 capture/runtime Pathfinder.
///
/// Faceware exposes setup health feedback rather than leaving users to guess
/// whether the camera, cadence, calibration or animation mapping is the current
/// bottleneck. Kiwi now publishes one prioritized health state and a compact
/// recommendation without changing the tracking result itself.
/// </summary>
[DefaultExecutionOrder(33100)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialPathfinder : MonoBehaviour
{
    public enum HealthState
    {
        Starting = 0,
        Healthy = 1,
        CalibrationNeeded = 2,
        CameraLimited = 3,
        ReadbackLimited = 4,
        TrackingCadenceLow = 5,
        TrackingStale = 6,
        EyeQualityLow = 7,
        MouthQualityLow = 8,
        LocalTrackerLimited = 9,
        Reacquiring = 10
    }

    private const string RuntimeObjectName =
        "[Kiwi] Commercial Pathfinder";

    [Header("Thresholds")]
    [Range(5f, 60f)]
    public float minimumCameraHz = 24f;

    [Range(3f, 40f)]
    public float minimumResultHz = 8f;

    [Range(20f, 250f)]
    public float excessiveMediaPipeReadbackMs = 120f;

    [Range(0.05f, 0.60f)]
    public float staleTrackingAgeSeconds = 0.24f;

    [Range(0f, 1f)]
    public float minimumEyeQuality = 0.38f;

    [Range(0f, 1f)]
    public float minimumMouthQuality = 0.38f;

    [Range(0f, 60f)]
    public float minimumLiveTrackerHz = 18f;

    [Header("Diagnostics")]
    [SerializeField] private HealthState debugState =
        HealthState.Starting;

    [SerializeField] private float debugHealthScore;
    [SerializeField] private string debugRecommendation = "Starting";
    [SerializeField] private float debugCameraHz;
    [SerializeField] private float debugResultHz;
    [SerializeField] private float debugReadbackMs;
    [SerializeField] private float debugTrackingAgeMs;
    [SerializeField] private float debugEyeQuality;
    [SerializeField] private float debugMouthQuality;
    [SerializeField] private float debugLiveTrackerHz;

    private FaceLandmarkerRunner _runner;
    private KiwiTrackingContinuityState _continuity;
    private KiwiDualDomainFaceQuality _dualDomain;
    private KiwiActorFaceCalibration _actorCalibration;
    private KiwiModelPrimaryFacePartConstraint _surfaceConstraint;
    private KiwiFacePartLiveMotionBridge _liveMotion;

    private double _nextEvaluationRealtime;
    private double _nextReferenceRefreshRealtime;

    public HealthState State =>
        debugState;

    public string StateName =>
        debugState.ToString();

    public float HealthScore =>
        debugHealthScore;

    public string Recommendation =>
        debugRecommendation;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<
                KiwiCommercialPathfinder>(
                    FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);

        host.AddComponent<
            KiwiCommercialPathfinder>();
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
        _continuity = null;
        _dualDomain = null;
        _actorCalibration = null;
        _surfaceConstraint = null;
        _liveMotion = null;

        _nextEvaluationRealtime = 0.0;
        _nextReferenceRefreshRealtime = 0.0;

        debugState =
            HealthState.Starting;

        debugRecommendation =
            "Starting";

        RefreshReferences(true);
    }

    private void Update()
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

        if (
            now <
            _nextEvaluationRealtime
        )
        {
            return;
        }

        _nextEvaluationRealtime =
            now + 0.20;

        Evaluate();
    }

    private void Evaluate()
    {
        float cameraHz =
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

        float trackingAge =
            _continuity != null
                ? _continuity.SourceAgeSeconds
                : 1f;

        float eyeQuality =
            _dualDomain != null
                ? _dualDomain.EyeQuality
                : 0f;

        float mouthQuality =
            _dualDomain != null
                ? _dualDomain.MouthQuality
                : 0f;

        float liveTrackerHz =
            _liveMotion != null
                ? _liveMotion.MatchRateHz
                : 0f;

        bool actorCalibrated =
            _actorCalibration != null &&
            _actorCalibration.IsCalibrated;

        bool surfaceCalibrated =
            _surfaceConstraint != null &&
            _surfaceConstraint.IsCalibrated;

        float cameraScore =
            Mathf.InverseLerp(
                minimumCameraHz *
                    0.50f,
                minimumCameraHz *
                    1.35f,
                cameraHz);

        float resultScore =
            Mathf.InverseLerp(
                minimumResultHz *
                    0.50f,
                minimumResultHz *
                    2.50f,
                resultHz);

        float ageScore =
            1f -
            Mathf.InverseLerp(
                staleTrackingAgeSeconds *
                    0.45f,
                staleTrackingAgeSeconds,
                trackingAge);

        float readbackScore =
            1f -
            Mathf.InverseLerp(
                excessiveMediaPipeReadbackMs *
                    0.55f,
                excessiveMediaPipeReadbackMs *
                    1.30f,
                readbackMs);

        float partScore =
            Mathf.Clamp01(
                (
                    eyeQuality +
                    mouthQuality
                ) *
                0.5f);

        float calibrationScore =
            actorCalibrated &&
            surfaceCalibrated
                ? 1f
                : 0.55f;

        debugHealthScore =
            Mathf.Clamp01(
                cameraScore *
                    0.18f +
                resultScore *
                    0.20f +
                ageScore *
                    0.24f +
                readbackScore *
                    0.12f +
                partScore *
                    0.18f +
                calibrationScore *
                    0.08f);

        debugCameraHz =
            cameraHz;

        debugResultHz =
            resultHz;

        debugReadbackMs =
            readbackMs;

        debugTrackingAgeMs =
            trackingAge *
            1000f;

        debugEyeQuality =
            eyeQuality;

        debugMouthQuality =
            mouthQuality;

        debugLiveTrackerHz =
            liveTrackerHz;

        if (
            _continuity != null &&
            (
                _continuity.State ==
                    KiwiTrackingContinuityState
                        .ContinuityState
                        .Reacquiring ||
                _continuity.State ==
                    KiwiTrackingContinuityState
                        .ContinuityState
                        .Starting
            )
        )
        {
            SetState(
                HealthState.Reacquiring,
                "Hold a neutral forward pose briefly while tracking settles.");

            return;
        }

        if (
            _continuity != null &&
            _continuity.IsHoldingOrLost
        )
        {
            SetState(
                HealthState.TrackingStale,
                "Tracking is stale/lost. Check face visibility and camera continuity.");

            return;
        }

        if (
            !actorCalibrated ||
            !surfaceCalibrated
        )
        {
            SetState(
                HealthState.CalibrationNeeded,
                "Face the camera neutrally and run Quick Recalibrate.");

            return;
        }

        if (
            cameraHz >
                0f &&
            cameraHz <
                minimumCameraHz
        )
        {
            SetState(
                HealthState.CameraLimited,
                "Camera update rate is the current bottleneck.");

            return;
        }

        if (
            readbackMs >=
                excessiveMediaPipeReadbackMs &&
            resultHz <
                minimumResultHz *
                    1.30f
        )
        {
            SetState(
                HealthState.ReadbackLimited,
                "MediaPipe readback is expensive; keep the high-rate local/Inference path active.");

            return;
        }

        if (
            resultHz >
                0f &&
            resultHz <
                minimumResultHz
        )
        {
            SetState(
                HealthState.TrackingCadenceLow,
                "Semantic tracking cadence is low; avoid adding more smoothing.");

            return;
        }

        if (
            trackingAge >
                staleTrackingAgeSeconds
        )
        {
            SetState(
                HealthState.TrackingStale,
                "Tracking result age is high; prediction/local tracking should carry only short gaps.");

            return;
        }

        if (
            eyeQuality <
                minimumEyeQuality
        )
        {
            SetState(
                HealthState.EyeQualityLow,
                "Eye observation quality is low; side-view linking/visibility guards should dominate.");

            return;
        }

        if (
            mouthQuality <
                minimumMouthQuality
        )
        {
            SetState(
                HealthState.MouthQualityLow,
                "Mouth observation quality is low; keep mouth topology and size limits active.");

            return;
        }

        if (
            _liveMotion != null &&
            _liveMotion.IsOperational &&
            cameraHz >=
                minimumCameraHz &&
            liveTrackerHz >
                0f &&
            liveTrackerHz <
                minimumLiveTrackerHz
        )
        {
            SetState(
                HealthState.LocalTrackerLimited,
                "Live 2D tracker cadence is low; Quality Governor may reduce its auxiliary cost.");

            return;
        }

        SetState(
            HealthState.Healthy,
            "Tracking pipeline is healthy.");
    }

    private void SetState(
        HealthState state,
        string recommendation)
    {
        debugState =
            state;

        debugRecommendation =
            recommendation;
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
            _dualDomain == null
        )
        {
            _dualDomain =
                FindFirstObjectByType<
                    KiwiDualDomainFaceQuality>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _actorCalibration == null
        )
        {
            _actorCalibration =
                FindFirstObjectByType<
                    KiwiActorFaceCalibration>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _surfaceConstraint == null
        )
        {
            _surfaceConstraint =
                FindFirstObjectByType<
                    KiwiModelPrimaryFacePartConstraint>(
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
}
