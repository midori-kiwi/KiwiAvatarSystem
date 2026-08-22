using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Phase 16.6 commercial presentation profile.
///
/// This component does not own tracking observations, Root pose, provider
/// arbitration, or semantic adoption. It only configures the already-existing
/// FacePart render-rate consumers for a low-latency commercial profile.
/// Jitter rejection remains in the semantic/canonical guards; this profile
/// therefore does not need a slow global low-pass.
/// </summary>
[DefaultExecutionOrder(520)]
[DisallowMultipleComponent]
public sealed class KiwiCommercialPresentationTuner : MonoBehaviour
{
    // KIWI_V5_1_PHASE16_6_COMMERCIAL_PRESENTATION_PROFILE
    private const string RuntimeObjectName =
        "[Kiwi] Commercial Presentation Tuner";

    // KIWI_V5_1_PHASE16_8_PRESENTATION_HEADROOM_FLOOR
    [Header("Commercial profile")]
    public bool enableCommercialProfile = true;

    [Tooltip("Only use the fastest profile while render headroom remains healthy.")]
    public bool adaptToRenderHeadroom = true;

    [Range(30f, 120f)]
    public float fastProfileMinimumRenderFps = 32f;

    [Header("Diagnostics")]
    [SerializeField] private bool debugFastProfile;
    [SerializeField] private float debugRenderFpsEma;
    [SerializeField] private bool debugCropperConfigured;
    [SerializeField] private int debugMasksConfigured;
    [SerializeField] private bool debugLiveMotionConfigured;

    private FacePartCropper _cropper;
    private KiwiFacePartLiveMotionBridge _liveMotion;
    private FacePartShapeMask[] _masks;
    private float _renderFpsEma = 60f;
    private float _nextRefreshTime;
    private bool _profileApplied;
    private bool _lastAppliedFastProfile;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInstall()
    {
        if (
            FindFirstObjectByType<KiwiCommercialPresentationTuner>(
                FindObjectsInactive.Include) != null
        )
        {
            return;
        }

        GameObject host =
            new GameObject(RuntimeObjectName);

        DontDestroyOnLoad(host);
        host.AddComponent<KiwiCommercialPresentationTuner>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        RefreshReferences(true);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void HandleSceneLoaded(
        Scene scene,
        LoadSceneMode mode)
    {
        RefreshReferences(true);
    }

    private void Update()
    {
        float dt =
            Mathf.Clamp(
                Time.unscaledDeltaTime,
                1f / 500f,
                0.10f);

        float fps =
            1f / Mathf.Max(0.0001f, dt);

        float t =
            1f - Mathf.Exp(-3f * dt);

        _renderFpsEma =
            Mathf.Lerp(
                _renderFpsEma,
                fps,
                t);

        if (Time.unscaledTime >= _nextRefreshTime)
        {
            _nextRefreshTime =
                Time.unscaledTime + 0.50f;

            RefreshReferences(false);
        }

        if (!enableCommercialProfile)
        {
            return;
        }

        float threshold =
            Mathf.Max(
                30f,
                fastProfileMinimumRenderFps);

        // Hysteresis prevents the presentation profile itself from becoming a
        // source of visible cadence changes near the render-headroom boundary.
        float resolvedThreshold =
            _profileApplied && _lastAppliedFastProfile
                ? Mathf.Max(30f, threshold - 4f)
                : threshold + 2f;

        bool fast =
            !adaptToRenderHeadroom ||
            _renderFpsEma >= resolvedThreshold;

        if (
            !_profileApplied ||
            fast != _lastAppliedFastProfile
        )
        {
            ApplyProfile(fast);
        }
    }

    private void ApplyProfile(bool fast)
    {
        debugFastProfile = fast;
        _lastAppliedFastProfile = fast;
        _profileApplied = true;
        debugRenderFpsEma = _renderFpsEma;

        if (_cropper != null)
        {
            // Render-rate interpolation remains enabled. The fast values reduce
            // visible sample-and-hold without changing accepted Landmarks.
            _cropper.strictLandmarkerTracking = false;
            _cropper.sampleIdleResponse = fast ? 135f : 110f;
            _cropper.sampleMovingResponse = fast ? 285f : 220f;
            _cropper.sampleMotionFullSpeed = 0.18f;
            _cropper.microJitterStart = 0.00018f;
            _cropper.microJitterFull = 0.00110f;
            _cropper.microJitterMinimumGain = 0.10f;
            _cropper.eyeSampleSizeResponse = fast ? 95f : 75f;
            _cropper.mouthSampleSizeResponse = fast ? 110f : 85f;
            _cropper.eyeRenderResponse = fast ? 225f : 175f;
            _cropper.mouthRenderResponse = fast ? 245f : 190f;
            _cropper.eyeRenderSizeResponse = fast ? 95f : 72f;
            _cropper.mouthRenderSizeResponse = fast ? 105f : 80f;
            _cropper.velocityResponse = fast ? 145f : 115f;
            _cropper.maxExtrapolationSeconds = 0.060f;
            _cropper.maxPredictionDistance = 0.0035f;
            _cropper.coherentVerticalRenderResponse = fast ? 230f : 185f;
            _cropper.restJitterThreshold = 0.00065f;
            _cropper.restSizeJitterThreshold = 0.00105f;
            debugCropperConfigured = true;
        }
        else
        {
            debugCropperConfigured = false;
        }

        int maskCount = 0;
        if (_masks != null)
        {
            for (int i = 0; i < _masks.Length; i++)
            {
                FacePartShapeMask mask = _masks[i];
                if (mask == null)
                {
                    continue;
                }

                mask.strictLandmarkerTracking = false;
                mask.microJitterDeadZone = 0.00045f;
                mask.contourRenderResponse = fast ? 150f : 115f;
                mask.lockContourToMovingCrop = true;
                mask.cropLocalSafetyMargin = 0.015f;
                mask.eyeHideFadeSeconds = 0.020f;
                mask.eyeShowFadeSeconds = 0.038f;
                mask.mouthHideFadeSeconds = 0.035f;
                mask.mouthShowFadeSeconds = 0.050f;
                maskCount++;
            }
        }

        debugMasksConfigured = maskCount;

        if (_liveMotion != null)
        {
            _liveMotion.acceptedCorrectionResponse = fast ? 135f : 100f;
            _liveMotion.rejectedCorrectionReturnResponse = fast ? 58f : 45f;
            _liveMotion.correctionDeadZoneCropFraction = 0.0045f;
            _liveMotion.maximumCorrectionHoldSeconds = 0.080f;
            debugLiveMotionConfigured = true;
        }
        else
        {
            debugLiveMotionConfigured = false;
        }
    }

    private void RefreshReferences(
        bool force)
    {
        bool rebound = false;

        if (force || _cropper == null)
        {
            _cropper =
                FindFirstObjectByType<FacePartCropper>(
                    FindObjectsInactive.Include);
            rebound = true;
        }

        if (force || _liveMotion == null)
        {
            _liveMotion =
                FindFirstObjectByType<KiwiFacePartLiveMotionBridge>(
                    FindObjectsInactive.Include);
            rebound = true;
        }

        if (
            force ||
            _masks == null ||
            _masks.Length == 0
        )
        {
            _masks =
                FindObjectsByType<FacePartShapeMask>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
            rebound = true;
        }

        if (rebound)
        {
            _profileApplied = false;
        }
    }
}
