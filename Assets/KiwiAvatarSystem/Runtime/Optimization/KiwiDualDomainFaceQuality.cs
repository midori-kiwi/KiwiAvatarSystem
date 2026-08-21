using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// v3.9 dual-domain face quality.
/// Camera-space 2D feature tracking is evaluated separately from rigid 3D
/// head/surface geometry so one weak facial part cannot poison the whole face.
/// </summary>
[DefaultExecutionOrder(-16800)]
[DisallowMultipleComponent]
public sealed class KiwiDualDomainFaceQuality : MonoBehaviour
{
    private const string RuntimeObjectName =
        "[Kiwi] Dual-Domain Face Quality";

    private const int LeftEyeOuter = 362;
    private const int LeftEyeInner = 263;
    private const int LeftEyeUpper = 386;
    private const int LeftEyeLower = 374;

    private const int RightEyeOuter = 33;
    private const int RightEyeInner = 133;
    private const int RightEyeUpper = 159;
    private const int RightEyeLower = 145;

    private const int MouthLeft = 61;
    private const int MouthRight = 291;
    private const int MouthUpper = 13;
    private const int MouthLower = 14;

    private const int FaceTop = 10;
    private const int FaceBottom = 152;
    private const int FaceLeft = 234;
    private const int FaceRight = 454;

    [Header("2D camera-space quality")]
    [Range(0.005f, 0.10f)]
    public float fullEdgeClearance = 0.030f;

    [Range(0.02f, 0.40f)]
    public float eyeCenterCoherenceFullErrorRatio = 0.10f;

    [Range(0.03f, 0.50f)]
    public float mouthCenterCoherenceFullErrorRatio = 0.16f;

    [Range(0.10f, 1.0f)]
    public float stale2dAgeSeconds = 0.34f;

    [Range(1f, 30f)]
    public float qualityResponse = 12f;

    [Header("Diagnostics")]
    [SerializeField] private float debugLeftEyeQuality;
    [SerializeField] private float debugRightEyeQuality;
    [SerializeField] private float debugMouthQuality;
    [SerializeField] private float debugCamera2dQuality;
    [SerializeField] private float debugHead3dQuality;
    [SerializeField] private float debugDualDomainQuality;
    [SerializeField] private float debugLeftEyeOpenRatio;
    [SerializeField] private float debugRightEyeOpenRatio;
    [SerializeField] private float debugMouthOpenRatio;
    [SerializeField] private float debug2dAgeMs;
    [SerializeField] private long debugTimestamp;

    private FaceLandmarkerRunner _runner;
    private KiwiTrackingProviderHub _hub;

    private Vector2[] _landmarks;
    private long _lastTimestamp = long.MinValue;
    private double _lastNew2dRealtime = -1000.0;

    private bool _hasMotionHistory;
    private Vector2 _previousFaceCenter;
    private Vector2 _previousLeftEyeCenter;
    private Vector2 _previousRightEyeCenter;
    private Vector2 _previousMouthCenter;

    private float _leftEyeQuality;
    private float _rightEyeQuality;
    private float _mouthQuality;
    private float _camera2dQuality;
    private float _head3dQuality;
    private float _dualDomainQuality;

    public bool Has2DFace { get; private set; }

    public long FrameTimestamp => _lastTimestamp;

    public float LeftEyeQuality => _leftEyeQuality;
    public float RightEyeQuality => _rightEyeQuality;

    // Use the better eye as the global eye-channel health. The far eye may be
    // legitimately poor during yaw while the near eye is still excellent.
    public float EyeQuality =>
        Mathf.Max(
            _leftEyeQuality,
            _rightEyeQuality);

    public float MouthQuality => _mouthQuality;
    public float Camera2DQuality => _camera2dQuality;
    public float Head3DQuality => _head3dQuality;
    public float DualDomainQuality => _dualDomainQuality;

    public float LeftEyeOpenRatio { get; private set; }
    public float RightEyeOpenRatio { get; private set; }
    public float MouthOpenRatio { get; private set; }
    public float EyeSpan { get; private set; }
    public float FaceWidth { get; private set; }

    public float Age2DSeconds =>
        Has2DFace
            ? Mathf.Max(
                0f,
                (float)(
                    Time.realtimeSinceStartupAsDouble -
                    _lastNew2dRealtime))
            : float.PositiveInfinity;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiDualDomainFaceQuality>(
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
            KiwiDualDomainFaceQuality>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        RefreshReferences();
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
        _hub = null;
        _landmarks = null;
        _lastTimestamp = long.MinValue;
        _lastNew2dRealtime = -1000.0;
        _hasMotionHistory = false;
        Has2DFace = false;

        RefreshReferences();
    }

    private void Update()
    {
        RefreshReferences();
        UpdateHead3DQuality();
        Consume2DFrame();
        ApplyFreshnessDecay();
        UpdateDiagnostics();
    }

    private void RefreshReferences()
    {
        if (_runner == null)
        {
            _runner =
                FindFirstObjectByType<FaceLandmarkerRunner>(
                    FindObjectsInactive.Include);
        }

        if (_hub == null)
        {
            _hub =
                FindFirstObjectByType<KiwiTrackingProviderHub>(
                    FindObjectsInactive.Include);
        }
    }

    private void UpdateHead3DQuality()
    {
        float target = 0f;

        if (
            _hub != null &&
            _hub.TryGetCapabilityHealth(
                KiwiTrackingProviderHub.TrackingCapability.FaceGeometry,
                out FacePrecisionTrackingData data,
                out KiwiTrackingProviderHub.CapabilityHealth health)
        )
        {
            float ageQuality =
                1f -
                Mathf.InverseLerp(
                    0.08f,
                    0.34f,
                    Mathf.Max(
                        0f,
                        health.ageSeconds));

            target =
                Mathf.Clamp01(
                    data.geometryQuality *
                    Mathf.Lerp(
                        0.55f,
                        1f,
                        ageQuality));
        }

        _head3dQuality =
            Smooth(
                _head3dQuality,
                target,
                qualityResponse);
    }

    private void Consume2DFrame()
    {
        if (_runner == null)
        {
            return;
        }

        bool changed =
            _runner.TryGetLatestLandmarksIfChanged(
                ref _landmarks,
                _lastTimestamp,
                out int count,
                out long timestamp,
                out bool hasFace);

        if (!changed)
        {
            return;
        }

        _lastTimestamp = timestamp;

        if (
            !hasFace ||
            _landmarks == null ||
            count <= FaceRight
        )
        {
            Has2DFace = false;
            _hasMotionHistory = false;
            return;
        }

        Has2DFace = true;
        _lastNew2dRealtime =
            Time.realtimeSinceStartupAsDouble;

        Vector2 leftEyeCenter =
            Average(
                _landmarks[LeftEyeOuter],
                _landmarks[LeftEyeInner]);

        Vector2 rightEyeCenter =
            Average(
                _landmarks[RightEyeOuter],
                _landmarks[RightEyeInner]);

        Vector2 mouthCenter =
            Average(
                _landmarks[MouthLeft],
                _landmarks[MouthRight]);

        Vector2 faceCenter =
            (
                _landmarks[FaceTop] +
                _landmarks[FaceBottom] +
                _landmarks[FaceLeft] +
                _landmarks[FaceRight]
            ) * 0.25f;

        float leftWidth =
            Vector2.Distance(
                _landmarks[LeftEyeOuter],
                _landmarks[LeftEyeInner]);

        float rightWidth =
            Vector2.Distance(
                _landmarks[RightEyeOuter],
                _landmarks[RightEyeInner]);

        float leftHeight =
            Vector2.Distance(
                _landmarks[LeftEyeUpper],
                _landmarks[LeftEyeLower]);

        float rightHeight =
            Vector2.Distance(
                _landmarks[RightEyeUpper],
                _landmarks[RightEyeLower]);

        float mouthWidth =
            Vector2.Distance(
                _landmarks[MouthLeft],
                _landmarks[MouthRight]);

        float mouthHeight =
            Vector2.Distance(
                _landmarks[MouthUpper],
                _landmarks[MouthLower]);

        FaceWidth =
            Vector2.Distance(
                _landmarks[FaceLeft],
                _landmarks[FaceRight]);

        EyeSpan =
            Vector2.Distance(
                leftEyeCenter,
                rightEyeCenter);

        LeftEyeOpenRatio =
            leftHeight /
            Mathf.Max(
                0.0001f,
                leftWidth);

        RightEyeOpenRatio =
            rightHeight /
            Mathf.Max(
                0.0001f,
                rightWidth);

        MouthOpenRatio =
            mouthHeight /
            Mathf.Max(
                0.0001f,
                mouthWidth);

        float leftEdgeQuality =
            FeatureEdgeQuality(
                leftEyeCenter,
                leftWidth,
                leftHeight);

        float rightEdgeQuality =
            FeatureEdgeQuality(
                rightEyeCenter,
                rightWidth,
                rightHeight);

        float mouthEdgeQuality =
            FeatureEdgeQuality(
                mouthCenter,
                mouthWidth,
                Mathf.Max(
                    mouthHeight,
                    mouthWidth * 0.20f));

        float leftSizeQuality =
            EyeSizeQuality(leftWidth);

        float rightSizeQuality =
            EyeSizeQuality(rightWidth);

        float mouthSizeQuality =
            MouthSizeQuality(mouthWidth);

        float leftCoherence = 1f;
        float rightCoherence = 1f;
        float mouthCoherence = 1f;

        if (_hasMotionHistory)
        {
            Vector2 faceDelta =
                faceCenter -
                _previousFaceCenter;

            float scale =
                Mathf.Max(
                    0.01f,
                    FaceWidth);

            leftCoherence =
                CoherenceQuality(
                    (
                        leftEyeCenter -
                        _previousLeftEyeCenter
                    ) -
                    faceDelta,
                    scale,
                    eyeCenterCoherenceFullErrorRatio);

            rightCoherence =
                CoherenceQuality(
                    (
                        rightEyeCenter -
                        _previousRightEyeCenter
                    ) -
                    faceDelta,
                    scale,
                    eyeCenterCoherenceFullErrorRatio);

            mouthCoherence =
                CoherenceQuality(
                    (
                        mouthCenter -
                        _previousMouthCenter
                    ) -
                    faceDelta,
                    scale,
                    mouthCenterCoherenceFullErrorRatio);
        }

        _previousFaceCenter = faceCenter;
        _previousLeftEyeCenter = leftEyeCenter;
        _previousRightEyeCenter = rightEyeCenter;
        _previousMouthCenter = mouthCenter;
        _hasMotionHistory = true;

        float leftTarget =
            Mathf.Clamp01(
                leftEdgeQuality *
                leftSizeQuality *
                Mathf.Lerp(
                    0.55f,
                    1f,
                    leftCoherence));

        float rightTarget =
            Mathf.Clamp01(
                rightEdgeQuality *
                rightSizeQuality *
                Mathf.Lerp(
                    0.55f,
                    1f,
                    rightCoherence));

        float mouthTarget =
            Mathf.Clamp01(
                mouthEdgeQuality *
                mouthSizeQuality *
                Mathf.Lerp(
                    0.58f,
                    1f,
                    mouthCoherence));

        _leftEyeQuality =
            Smooth(
                _leftEyeQuality,
                leftTarget,
                qualityResponse);

        _rightEyeQuality =
            Smooth(
                _rightEyeQuality,
                rightTarget,
                qualityResponse);

        _mouthQuality =
            Smooth(
                _mouthQuality,
                mouthTarget,
                qualityResponse);

        float cameraTarget =
            Mathf.Clamp01(
                Mathf.Max(
                    leftTarget,
                    rightTarget) * 0.45f +
                mouthTarget * 0.35f +
                Mathf.Clamp01(
                    EyeSpan / 0.08f) * 0.20f);

        _camera2dQuality =
            Smooth(
                _camera2dQuality,
                cameraTarget,
                qualityResponse);

        float dualTarget =
            Mathf.Clamp01(
                _camera2dQuality * 0.62f +
                _head3dQuality * 0.38f);

        _dualDomainQuality =
            Smooth(
                _dualDomainQuality,
                dualTarget,
                qualityResponse);
    }

    private void ApplyFreshnessDecay()
    {
        float age = Age2DSeconds;

        if (
            float.IsNaN(age) ||
            float.IsInfinity(age) ||
            age <= stale2dAgeSeconds
        )
        {
            return;
        }

        float decay =
            Mathf.Exp(
                -8f *
                Time.unscaledDeltaTime);

        _leftEyeQuality *= decay;
        _rightEyeQuality *= decay;
        _mouthQuality *= decay;
        _camera2dQuality *= decay;

        _dualDomainQuality =
            Mathf.Clamp01(
                _camera2dQuality * 0.62f +
                _head3dQuality * 0.38f);
    }

    private float FeatureEdgeQuality(
        Vector2 center,
        float width,
        float height)
    {
        float halfW = width * 0.5f;
        float halfH = height * 0.5f;

        float clearance =
            Mathf.Min(
                center.x - halfW,
                1f - (center.x + halfW),
                center.y - halfH,
                1f - (center.y + halfH));

        return
            Mathf.Clamp01(
                clearance /
                Mathf.Max(
                    0.001f,
                    fullEdgeClearance));
    }

    private float EyeSizeQuality(float eyeWidth)
    {
        float relative =
            eyeWidth /
            Mathf.Max(
                0.001f,
                FaceWidth);

        return
            BandQuality(
                relative,
                0.08f,
                0.14f,
                0.34f,
                0.48f);
    }

    private float MouthSizeQuality(float mouthWidth)
    {
        float relative =
            mouthWidth /
            Mathf.Max(
                0.001f,
                FaceWidth);

        return
            BandQuality(
                relative,
                0.12f,
                0.20f,
                0.55f,
                0.72f);
    }

    private static float CoherenceQuality(
        Vector2 residual,
        float faceScale,
        float fullErrorRatio)
    {
        float ratio =
            residual.magnitude /
            Mathf.Max(
                0.001f,
                faceScale);

        return
            1f -
            Mathf.InverseLerp(
                fullErrorRatio * 0.25f,
                fullErrorRatio,
                ratio);
    }

    private static float BandQuality(
        float value,
        float hardMin,
        float softMin,
        float softMax,
        float hardMax)
    {
        if (
            float.IsNaN(value) ||
            float.IsInfinity(value) ||
            value <= hardMin ||
            value >= hardMax
        )
        {
            return 0f;
        }

        if (value < softMin)
        {
            return
                Mathf.InverseLerp(
                    hardMin,
                    softMin,
                    value);
        }

        if (value > softMax)
        {
            return
                1f -
                Mathf.InverseLerp(
                    softMax,
                    hardMax,
                    value);
        }

        return 1f;
    }

    private float Smooth(
        float current,
        float target,
        float response)
    {
        return
            Mathf.Lerp(
                current,
                target,
                1f -
                Mathf.Exp(
                    -Mathf.Max(
                        0f,
                        response) *
                    Mathf.Max(
                        0.000001f,
                        Time.unscaledDeltaTime)));
    }

    private static Vector2 Average(
        Vector2 a,
        Vector2 b)
    {
        return
            (a + b) * 0.5f;
    }

    private void UpdateDiagnostics()
    {
        debugLeftEyeQuality = _leftEyeQuality;
        debugRightEyeQuality = _rightEyeQuality;
        debugMouthQuality = _mouthQuality;
        debugCamera2dQuality = _camera2dQuality;
        debugHead3dQuality = _head3dQuality;
        debugDualDomainQuality = _dualDomainQuality;
        debugLeftEyeOpenRatio = LeftEyeOpenRatio;
        debugRightEyeOpenRatio = RightEyeOpenRatio;
        debugMouthOpenRatio = MouthOpenRatio;

        float age = Age2DSeconds;

        debug2dAgeMs =
            float.IsInfinity(age)
                ? -1f
                : age * 1000f;

        debugTimestamp = _lastTimestamp;
    }
}
