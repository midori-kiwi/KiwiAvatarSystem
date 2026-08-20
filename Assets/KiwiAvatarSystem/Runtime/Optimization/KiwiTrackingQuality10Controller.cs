using System;
using UnityEngine;
using UnityEngine.SceneManagement;

using Mediapipe.Unity.Sample.FaceLandmarkDetection;

/// <summary>
/// Human-motion presentation layer for KiwiAvatarSystem.
///
/// The tracking core remains MediaPipe + Unity Inference Engine.
/// This component owns ONLY temporal presentation:
///
/// 1. KiwiFaceMotion produces a near-raw pose for each atomic tracking frame.
/// 2. This component detects new frames by FacePrecisionTrackingData.frameId.
/// 3. Sample velocity is estimated only from coherent tracking-frame timestamps.
/// 4. The pose is predicted to render time and followed with a short,
///    critically-damped presentation response.
/// 5. Eyes / mouth keep their own lighter, channel-specific stabilization.
///
/// This avoids the previous "sample -> hold -> jump" look without adding a
/// long motion buffer.
/// </summary>
[DefaultExecutionOrder(30000)]
[DisallowMultipleComponent]
public sealed class KiwiTrackingQuality10Controller : MonoBehaviour
{
    public const string PresetVersion = "2.0.0-human-motion";

    private const string RuntimeObjectName =
        "[Kiwi] Human Motion Presentation";

    private static readonly double HostTickSeconds =
        1.0 / System.Diagnostics.Stopwatch.Frequency;

    [Header("Human Motion v2")]
    [Tooltip("Apply the recommended low-latency / high-continuity preset.")]
    public bool applyRecommendedSettings = true;

    [Tooltip("Apply each component preset once so later Inspector/UI tuning is not continuously overwritten.")]
    public bool applyPresetOnlyOnce = true;

    [Header("Presentation frame rate")]
    public bool requestHighPresentationRate = true;

    [Range(60, 240)]
    public int targetPresentationFrameRate = 120;

    [Header("Prediction")]
    [Tooltip("Small display lead. The main compensation comes from measured sample age.")]
    [Range(0f, 0.030f)]
    public float additionalDisplayLeadSeconds = 0.008f;

    [Tooltip("Prediction strength at normal tracking quality.")]
    [Range(0f, 1.2f)]
    public float predictionStrength = 0.92f;

    [Tooltip("Absolute prediction cap. The effective cap also follows the measured sample interval.")]
    [Range(0.030f, 0.140f)]
    public float maximumPredictionSeconds = 0.105f;

    [Tooltip("When a result is older than the expected cadence, velocity decays smoothly instead of running away.")]
    [Range(2f, 40f)]
    public float staleVelocityDecayResponse = 16f;

    [Header("Position continuity")]
    [Tooltip("Presentation response during intentional movement.")]
    [Range(0.008f, 0.080f)]
    public float movingPositionSmoothTime = 0.020f;

    [Tooltip("Presentation response close to rest.")]
    [Range(0.015f, 0.120f)]
    public float restingPositionSmoothTime = 0.045f;

    [Range(0.005f, 0.30f)]
    public float positionMotionFullSpeed = 0.060f;

    [Range(1f, 100f)]
    public float positionVelocityResponse = 28f;

    [Range(0.05f, 5f)]
    public float maximumPositionSpeed = 1.50f;

    [Range(0f, 0.10f)]
    public float maximumPositionPrediction = 0.045f;

    [Header("Rotation continuity")]
    [Range(10f, 120f)]
    public float movingRotationResponse = 52f;

    [Range(5f, 80f)]
    public float restingRotationResponse = 28f;

    [Range(5f, 180f)]
    public float rotationMotionFullSpeed = 42f;

    [Range(1f, 100f)]
    public float angularVelocityResponse = 32f;

    [Range(90f, 1440f)]
    public float maximumAngularSpeed = 720f;

    [Range(0f, 35f)]
    public float maximumRotationPredictionDegrees = 15f;

    [Header("Depth / scale continuity")]
    [Range(0.008f, 0.100f)]
    public float movingScaleSmoothTime = 0.025f;

    [Range(0.015f, 0.140f)]
    public float restingScaleSmoothTime = 0.055f;

    [Range(0.02f, 2f)]
    public float scaleMotionFullSpeed = 0.25f;

    [Range(1f, 100f)]
    public float scaleVelocityResponse = 24f;

    [Range(0.1f, 8f)]
    public float maximumScaleSpeed = 3f;

    [Range(0f, 0.15f)]
    public float maximumScalePrediction = 0.050f;

    [Header("Rest stability")]
    [Range(0f, 0.005f)]
    public float positionRestError = 0.00035f;

    [Range(0f, 0.5f)]
    public float rotationRestErrorDegrees = 0.10f;

    [Range(0f, 0.010f)]
    public float scaleRestError = 0.00070f;

    [Header("Discontinuity protection")]
    [Range(0.02f, 1f)]
    public float positionResetDistance = 0.30f;

    [Range(20f, 180f)]
    public float rotationResetDegrees = 85f;

    [Range(0.05f, 2f)]
    public float scaleResetDistance = 0.65f;

    [Header("Tracking throughput preset")]
    [Tooltip("MediaPipe is auxiliary in hybrid mode. A smaller auxiliary input reduces DX11 readback pressure without reducing the visible eye/mouth texture resolution.")]
    [Range(320, 640)]
    public int auxiliaryMediaPipeInputWidth = 384;

    [Tooltip("Periodic MediaPipe refresh while Inference Engine is primary.")]
    [Range(4f, 15f)]
    public float auxiliaryMediaPipeRefreshHz = 8f;

    [Header("Diagnostics")]
    [SerializeField] private string debugBackend = "-";
    [SerializeField] private float debugTrackingSampleHz;
    [SerializeField] private float debugTrackingAgeMs;
    [SerializeField] private float debugPredictionHorizonMs;
    [SerializeField] private float debugGeometryQuality;
    [SerializeField] private ulong debugFrameId;

    private KiwiFaceMotion _faceMotion;
    private FaceLandmarkerRunner _runner;
    private FacePartCropper _cropper;
    private FacePartShapeMask[] _shapeMasks =
        Array.Empty<FacePartShapeMask>();
    private KiwiAvatarRuntimeManager _runtimeManager;
    private Transform _motionRoot;

    private int _presetFaceMotionId;
    private int _presetRunnerId;
    private int _presetCropperId;
    private int _presetRuntimeManagerId;
    private int _presetShapeMaskSignature;

    private bool _sampleInitialized;
    private ulong _lastFrameId;
    private KiwiTrackingBackend _lastBackend =
        KiwiTrackingBackend.Unknown;
    private long _lastSampleHostTicks;
    private float _sampleIntervalEma = 1f / 20f;

    private Vector3 _samplePosition;
    private Quaternion _sampleRotation = Quaternion.identity;
    private Vector3 _sampleScale = Vector3.one;

    private Vector3 _previousSamplePosition;
    private Quaternion _previousSampleRotation = Quaternion.identity;
    private Vector3 _previousSampleScale = Vector3.one;

    private Vector3 _samplePositionVelocity;
    private Vector3 _sampleAngularVelocityDeg;
    private Vector3 _sampleScaleVelocity;

    private Vector3 _renderPosition;
    private Quaternion _renderRotation = Quaternion.identity;
    private Vector3 _renderScale = Vector3.one;

    private Vector3 _positionSmoothVelocity;
    private Vector3 _scaleSmoothVelocity;

    private double _lastPresentationRealtime;

    private bool _lastRuntimeBusy;
    private string _lastAvatarName = string.Empty;
    private int _pendingResetFrames;

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoInstall()
    {
        KiwiTrackingQuality10Controller existing =
            FindFirstObjectByType<KiwiTrackingQuality10Controller>(
                FindObjectsInactive.Include);

        if (existing != null)
        {
            return;
        }

        GameObject host = new GameObject(RuntimeObjectName);
        DontDestroyOnLoad(host);
        host.AddComponent<KiwiTrackingQuality10Controller>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        RefreshReferences(true);
        ApplyRecommendedPresetIfNeeded();
    }

    private void OnEnable()
    {
        Application.onBeforeRender -= HandleBeforeRender;
        Application.onBeforeRender += HandleBeforeRender;
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= HandleBeforeRender;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        Application.onBeforeRender -= HandleBeforeRender;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshReferences(true);
        ApplyRecommendedPresetIfNeeded();
        ResetPresentationFromCurrentPose();
    }

    private void Start()
    {
        RefreshReferences(true);
        ApplyRecommendedPresetIfNeeded();
        ResetPresentationFromCurrentPose();
    }

    private void LateUpdate()
    {
        RefreshReferences(false);
        ApplyRecommendedPresetIfNeeded();
        UpdateAvatarSwapState();

        if (_pendingResetFrames > 0)
        {
            _pendingResetFrames--;
            if (_pendingResetFrames == 0)
            {
                ResetPresentationFromCurrentPose();
            }
        }

        CaptureNewTrackingSample();
        PresentAtRenderTime();
    }

    private void HandleBeforeRender()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }

        RefreshReferences(false);

        // KiwiFaceMotion subscribes from the scene before this runtime-created
        // controller, so a just-arrived atomic sample can be captured here after
        // KiwiFaceMotion maps it to the avatar transform.
        CaptureNewTrackingSample();
        PresentAtRenderTime();
    }

    private void RefreshReferences(bool force)
    {
        if (force || _faceMotion == null)
        {
            _faceMotion = FindFirstObjectByType<KiwiFaceMotion>(
                FindObjectsInactive.Include);
        }

        if (force || _runner == null)
        {
            _runner = FindFirstObjectByType<FaceLandmarkerRunner>(
                FindObjectsInactive.Include);
        }

        if (force || _cropper == null)
        {
            _cropper = FindFirstObjectByType<FacePartCropper>(
                FindObjectsInactive.Include);
        }

        if (force || _runtimeManager == null)
        {
            _runtimeManager =
                FindFirstObjectByType<KiwiAvatarRuntimeManager>(
                    FindObjectsInactive.Include);
        }

        if (
            force ||
            _shapeMasks == null ||
            _shapeMasks.Length == 0
        )
        {
            _shapeMasks = FindObjectsByType<FacePartShapeMask>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
        }

        Transform nextRoot =
            _faceMotion != null
                ? _faceMotion.kiwiRoot
                : null;

        if (_motionRoot != nextRoot)
        {
            _motionRoot = nextRoot;
            ResetPresentationFromCurrentPose();
        }
    }

    private void ApplyRecommendedPresetIfNeeded()
    {
        if (!applyRecommendedSettings)
        {
            return;
        }

        if (
            requestHighPresentationRate &&
            targetPresentationFrameRate > 0
        )
        {
            Application.targetFrameRate =
                targetPresentationFrameRate;
        }

        if (_faceMotion != null)
        {
            int id = _faceMotion.GetInstanceID();
            if (!applyPresetOnlyOnce || id != _presetFaceMotionId)
            {
                ApplyFaceMotionPreset(_faceMotion);
                _presetFaceMotionId = id;
            }
        }

        if (_runner != null)
        {
            int id = _runner.GetInstanceID();
            if (!applyPresetOnlyOnce || id != _presetRunnerId)
            {
                ApplyRunnerPreset(_runner);
                _presetRunnerId = id;
            }
        }

        if (_cropper != null)
        {
            int id = _cropper.GetInstanceID();
            if (!applyPresetOnlyOnce || id != _presetCropperId)
            {
                ApplyCropperPreset(_cropper);
                _presetCropperId = id;
            }
        }

        if (_runtimeManager != null)
        {
            int id = _runtimeManager.GetInstanceID();
            if (!applyPresetOnlyOnce || id != _presetRuntimeManagerId)
            {
                _runtimeManager.enableSpringBone = true;
                _runtimeManager.enableAdaptiveHeadFit = true;
                _presetRuntimeManagerId = id;
            }
        }

        int maskSignature = CalculateShapeMaskSignature();
        if (
            _shapeMasks != null &&
            _shapeMasks.Length > 0 &&
            (
                !applyPresetOnlyOnce ||
                maskSignature != _presetShapeMaskSignature
            )
        )
        {
            for (int i = 0; i < _shapeMasks.Length; i++)
            {
                FacePartShapeMask mask = _shapeMasks[i];
                if (mask != null)
                {
                    ApplyShapeMaskPreset(mask);
                }
            }

            _presetShapeMaskSignature = maskSignature;
        }
    }

    private static void ApplyFaceMotionPreset(
        KiwiFaceMotion motion)
    {
        // One temporal owner: KiwiFaceMotion supplies near-raw accepted samples;
        // this controller owns render-time continuity and prediction.
        motion.strictLandmarkerTracking = true;
        motion.useBeforeRenderLateLatch = true;
        motion.useScreenSpacePositionMapping = true;
        motion.avatarCentricHorizontalMovement = false;

        motion.landMarkerSpeedMode = true;
        motion.enableUltraLowLatencyTracking = true;
        motion.ultraUseRunnerPositionAnchor = true;
        motion.ultraConsumeLatestSampleBeforeRender = true;
        motion.ultraDisableSecondaryBodyMotion = true;

        // Remove sample-domain holds and secondary temporal filters. They can turn
        // slow real movement into a sequence of releases.
        motion.ultraAdaptiveMicroFilter = false;
        motion.ultraStaticPoseLock = false;

        // No body display interpolation inside KiwiFaceMotion. Keeping temporal
        // presentation in one layer prevents double-filter lag and uneven phase.
        motion.ultraDisplayRateSmoothing = false;
        motion.ultraDirectDisplayDuringMotion = true;
        motion.ultraPredictivePositionResampling = false;

        // This controller performs timing-aware prediction using atomic frame IDs
        // and matched host timestamps, so disable duplicate prediction here.
        motion.ultraPredictionStrength = 0f;
        motion.ultraCompensateFullResultAge = false;
        motion.ultraCompensateCameraCaptureAge = false;
        motion.enableRenderTimeLatePrediction = false;
        motion.predictionStrength = 0f;

        motion.enableHybridPrecisionTracking = true;
        motion.enablePrecisionOutlierGuard = true;
        motion.useBoundedLatestResultCorrection = true;
        motion.usePrecisionDepthFusion = true;

        // Avoid a discrete rotation hold at very slow head movement.
        motion.rotationStaticDeadZone = 0f;
        motion.rotationDeadZoneReleaseSpeed = 0f;
    }

    private void ApplyRunnerPreset(
        FaceLandmarkerRunner runner)
    {
        runner.renderDebugLandmarkAnnotations = false;
        runner.processOnlyFreshWebCamFrames = true;
        runner.latestFrameOnlyLiveStream = true;

        runner.downscaleTrackingInput = true;
        runner.trackingInputMaxWidth =
            auxiliaryMediaPipeInputWidth;

        runner.autoOptimizeCm831 = true;
        runner.cm831TrackingInputWidth =
            auxiliaryMediaPipeInputWidth;

        runner.enableSentisHybridTracking = true;
        runner.sentisMediaPipeRefreshRateHz =
            auxiliaryMediaPipeRefreshHz;

        // Keep acquisition robust while avoiding unnecessary fallback churn.
        runner.sentisMinimumPresence = 0.45f;
    }

    private static void ApplyCropperPreset(
        FacePartCropper cropper)
    {
        cropper.strictLandmarkerTracking = false;

        cropper.request120Fps = true;
        cropper.targetRenderFrameRate = 120;

        // Lower sample-domain response slightly and let continuous render-domain
        // prediction do the work. This avoids visible eye/mouth sample steps.
        cropper.sampleIdleResponse = 72f;
        cropper.sampleMovingResponse = 155f;
        cropper.sampleMotionFullSpeed = 0.16f;

        cropper.microJitterStart = 0.00012f;
        cropper.microJitterFull = 0.00090f;
        cropper.microJitterMinimumGain = 0.20f;

        cropper.eyeSampleSizeResponse = 82f;
        cropper.mouthSampleSizeResponse = 92f;

        cropper.eyeRenderResponse = 92f;
        cropper.mouthRenderResponse = 105f;
        cropper.eyeRenderSizeResponse = 68f;
        cropper.mouthRenderSizeResponse = 78f;

        cropper.velocityResponse = 82f;
        cropper.maxCenterVelocity = 2.5f;

        cropper.enablePrediction = true;
        cropper.compensateMatchedFrameAge = true;

        // Never expose accepted-sample stepping directly during motion.
        cropper.directPositionDuringMotion = false;
        cropper.predictionLeadSeconds = 0.006f;
        cropper.maxExtrapolationSeconds = 0.085f;
        cropper.maxPredictionDistance = 0.0045f;

        cropper.stabilizeCoherentVerticalMotion = true;
        cropper.coherentVerticalMotionMinSpeed = 0.020f;
        cropper.coherentVerticalDeltaTolerance = 0.0025f;
        cropper.phaseLockVerticalPrediction = true;
        cropper.coherentVerticalRenderResponse = 112f;

        cropper.restSpeed = 0.014f;
        cropper.restJitterThreshold = 0.00045f;
        cropper.restSizeJitterThreshold = 0.00085f;

        cropper.lostTrackingResetTime = 0.24f;
        cropper.hidePartsWhenLost = false;

        cropper.rejectIsolatedMouthOutliers = true;
        cropper.mouthOutlierAbsoluteTolerance = 0.040f;
        cropper.mouthOutlierEyeSpanMultiplier = 1.20f;
    }

    private static void ApplyShapeMaskPreset(
        FacePartShapeMask mask)
    {
        mask.strictLandmarkerTracking = false;
        mask.stabilizeSurfaceOcclusion = true;

        mask.useBlendshapeBlink = true;
        mask.useGeometryCloseFallback = true;

        mask.stabilizeEyeVisibility = true;

        // With a 10-30 Hz tracking stream, two confirmation samples can make
        // a normal human blink visibly late. One sample + short fade is faster,
        // while the existing hysteresis/geometry checks still reject noise.
        mask.eyeCloseConfirmationSamples = 1;
        mask.eyeOpenConfirmationSamples = 1;

        mask.feather = 0.040f;
        mask.microJitterDeadZone = 0.00038f;

        mask.contourRenderResponse = 92f;
        mask.lockContourToMovingCrop = true;
        mask.cropLocalSafetyMargin = 0.015f;

        mask.eyeHideFadeSeconds = 0.018f;
        mask.eyeShowFadeSeconds = 0.030f;

        mask.automaticEyeMatching = true;
        mask.lockEyeAssignment = true;
        mask.eyeSwitchHysteresis = 1.35f;

        mask.hideMouthOutsideTexture = true;
        mask.protectMouthDuringBlink = true;
        mask.mouthEdgeHideConfirmationSamples = 2;
        mask.mouthEdgeShowConfirmationSamples = 1;
        mask.mouthEdgeHideGraceSeconds = 0.090f;
        mask.mouthHideFadeSeconds = 0.030f;
        mask.mouthShowFadeSeconds = 0.045f;
    }

    private int CalculateShapeMaskSignature()
    {
        if (_shapeMasks == null || _shapeMasks.Length == 0)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            for (int i = 0; i < _shapeMasks.Length; i++)
            {
                if (_shapeMasks[i] != null)
                {
                    hash =
                        hash * 31 +
                        _shapeMasks[i].GetInstanceID();
                }
            }
            return hash;
        }
    }

    private void UpdateAvatarSwapState()
    {
        if (_runtimeManager == null)
        {
            return;
        }

        bool busy = _runtimeManager.IsBusy;
        string avatarName =
            _runtimeManager.CurrentAvatarName ??
            string.Empty;

        if (_lastRuntimeBusy && !busy)
        {
            _pendingResetFrames = 1;
        }

        if (
            !string.IsNullOrEmpty(_lastAvatarName) &&
            !string.Equals(
                avatarName,
                _lastAvatarName,
                StringComparison.Ordinal)
        )
        {
            _pendingResetFrames = 1;
        }

        _lastRuntimeBusy = busy;
        _lastAvatarName = avatarName;
    }

    private void CaptureNewTrackingSample()
    {
        if (_runner == null || _motionRoot == null)
        {
            return;
        }

        if (
            !_runner.TryGetLatestPrecisionTrackingData(
                out FacePrecisionTrackingData data) ||
            !data.isValid ||
            data.frameId == 0UL ||
            data.frameId == _lastFrameId
        )
        {
            return;
        }

        long sampleHostTicks =
            ResolveSampleHostTicks(data);

        Vector3 rawPosition =
            _motionRoot.localPosition;
        Quaternion rawRotation =
            _motionRoot.localRotation;
        Vector3 rawScale =
            _motionRoot.localScale;

        bool backendChanged =
            _lastBackend != KiwiTrackingBackend.Unknown &&
            data.backend != KiwiTrackingBackend.Unknown &&
            data.backend != _lastBackend;

        float sampleDt =
            _lastSampleHostTicks > 0L &&
            sampleHostTicks > _lastSampleHostTicks
                ? (float)(
                    (sampleHostTicks - _lastSampleHostTicks) *
                    HostTickSeconds)
                : 0f;

        bool timingGap =
            sampleDt <= 0f ||
            sampleDt > 0.250f;

        if (
            !_sampleInitialized ||
            backendChanged ||
            timingGap ||
            IsLargeDiscontinuity(
                rawPosition,
                rawRotation,
                rawScale)
        )
        {
            InitializeSampleState(
                rawPosition,
                rawRotation,
                rawScale,
                data,
                sampleHostTicks);
            return;
        }

        sampleDt =
            Mathf.Clamp(sampleDt, 1f / 240f, 0.200f);

        Vector3 measuredPositionVelocity =
            (rawPosition - _samplePosition) /
            sampleDt;

        measuredPositionVelocity =
            Vector3.ClampMagnitude(
                measuredPositionVelocity,
                maximumPositionSpeed);

        Vector3 measuredAngularVelocity =
            KiwiPrecisionTrackingMath.AngularVelocityDegrees(
                _sampleRotation,
                rawRotation,
                sampleDt);

        measuredAngularVelocity =
            Vector3.ClampMagnitude(
                measuredAngularVelocity,
                maximumAngularSpeed);

        Vector3 measuredScaleVelocity =
            (rawScale - _sampleScale) /
            sampleDt;

        measuredScaleVelocity =
            Vector3.ClampMagnitude(
                measuredScaleVelocity,
                maximumScaleSpeed);

        float quality =
            Mathf.Clamp01(data.geometryQuality);

        float positionMotion =
            Mathf.Clamp01(
                measuredPositionVelocity.magnitude /
                Mathf.Max(
                    0.0001f,
                    positionMotionFullSpeed));

        float rotationMotion =
            Mathf.Clamp01(
                measuredAngularVelocity.magnitude /
                Mathf.Max(
                    0.0001f,
                    rotationMotionFullSpeed));

        float scaleMotion =
            Mathf.Clamp01(
                measuredScaleVelocity.magnitude /
                Mathf.Max(
                    0.0001f,
                    scaleMotionFullSpeed));

        float qualityResponse =
            Mathf.Lerp(0.72f, 1f, quality);

        float posAlpha =
            ExpAlpha(
                positionVelocityResponse *
                Mathf.Lerp(0.72f, 1.15f, positionMotion) *
                qualityResponse,
                sampleDt);

        float rotAlpha =
            ExpAlpha(
                angularVelocityResponse *
                Mathf.Lerp(0.75f, 1.20f, rotationMotion) *
                qualityResponse,
                sampleDt);

        float scaleAlpha =
            ExpAlpha(
                scaleVelocityResponse *
                Mathf.Lerp(0.72f, 1.10f, scaleMotion) *
                qualityResponse,
                sampleDt);

        // Reversals should stop old prediction quickly instead of overshooting.
        if (
            Vector3.Dot(
                _samplePositionVelocity,
                measuredPositionVelocity) < 0f
        )
        {
            posAlpha = Mathf.Max(posAlpha, 0.82f);
        }

        if (
            Vector3.Dot(
                _sampleAngularVelocityDeg,
                measuredAngularVelocity) < 0f
        )
        {
            rotAlpha = Mathf.Max(rotAlpha, 0.85f);
        }

        if (
            Vector3.Dot(
                _sampleScaleVelocity,
                measuredScaleVelocity) < 0f
        )
        {
            scaleAlpha =
                Mathf.Max(scaleAlpha, 0.82f);
        }

        _samplePositionVelocity =
            Vector3.Lerp(
                _samplePositionVelocity,
                measuredPositionVelocity,
                posAlpha);

        _sampleAngularVelocityDeg =
            Vector3.Lerp(
                _sampleAngularVelocityDeg,
                measuredAngularVelocity,
                rotAlpha);

        _sampleScaleVelocity =
            Vector3.Lerp(
                _sampleScaleVelocity,
                measuredScaleVelocity,
                scaleAlpha);

        _previousSamplePosition = _samplePosition;
        _previousSampleRotation = _sampleRotation;
        _previousSampleScale = _sampleScale;

        _samplePosition = rawPosition;
        _sampleRotation = rawRotation;
        _sampleScale = rawScale;

        _sampleIntervalEma =
            Mathf.Lerp(
                _sampleIntervalEma,
                sampleDt,
                0.22f);

        _lastFrameId = data.frameId;
        _lastBackend = data.backend;
        _lastSampleHostTicks = sampleHostTicks;

        UpdateDiagnostics(data, sampleHostTicks);
    }

    private void InitializeSampleState(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        FacePrecisionTrackingData data,
        long sampleHostTicks)
    {
        _sampleInitialized = true;

        _samplePosition = position;
        _previousSamplePosition = position;

        _sampleRotation = rotation;
        _previousSampleRotation = rotation;

        _sampleScale = scale;
        _previousSampleScale = scale;

        _samplePositionVelocity = Vector3.zero;
        _sampleAngularVelocityDeg = Vector3.zero;
        _sampleScaleVelocity = Vector3.zero;

        _renderPosition = position;
        _renderRotation = rotation;
        _renderScale = scale;

        _positionSmoothVelocity = Vector3.zero;
        _scaleSmoothVelocity = Vector3.zero;

        _lastFrameId = data.frameId;
        _lastBackend = data.backend;
        _lastSampleHostTicks = sampleHostTicks;

        _sampleIntervalEma =
            1f /
            Mathf.Max(
                10f,
                _runner != null
                    ? _runner.LatestTrackingResultRateHz
                    : 20f);

        _lastPresentationRealtime =
            Time.realtimeSinceStartupAsDouble;

        UpdateDiagnostics(data, sampleHostTicks);
    }

    private void ResetPresentationFromCurrentPose()
    {
        _sampleInitialized = false;
        _lastFrameId = 0UL;
        _lastBackend = KiwiTrackingBackend.Unknown;
        _lastSampleHostTicks = 0L;

        _samplePositionVelocity = Vector3.zero;
        _sampleAngularVelocityDeg = Vector3.zero;
        _sampleScaleVelocity = Vector3.zero;

        _positionSmoothVelocity = Vector3.zero;
        _scaleSmoothVelocity = Vector3.zero;

        _sampleIntervalEma = 1f / 20f;

        if (_motionRoot != null)
        {
            _samplePosition =
                _motionRoot.localPosition;
            _sampleRotation =
                _motionRoot.localRotation;
            _sampleScale =
                _motionRoot.localScale;

            _previousSamplePosition = _samplePosition;
            _previousSampleRotation = _sampleRotation;
            _previousSampleScale = _sampleScale;

            _renderPosition = _samplePosition;
            _renderRotation = _sampleRotation;
            _renderScale = _sampleScale;
        }

        _lastPresentationRealtime =
            Time.realtimeSinceStartupAsDouble;
    }

    private void PresentAtRenderTime()
    {
        if (
            !_sampleInitialized ||
            _motionRoot == null
        )
        {
            return;
        }

        double nowRealtime =
            Time.realtimeSinceStartupAsDouble;

        float dt =
            (float)Math.Max(
                0.000001,
                nowRealtime -
                _lastPresentationRealtime);

        _lastPresentationRealtime = nowRealtime;

        // onBeforeRender can run very close to LateUpdate. Clamp only the upper
        // bound; a very small dt is still valid for a late-latch correction.
        dt = Mathf.Min(dt, 0.050f);

        long nowHostTicks =
            System.Diagnostics.Stopwatch.GetTimestamp();

        float age =
            _lastSampleHostTicks > 0L
                ? Mathf.Max(
                    0f,
                    (float)(
                        (nowHostTicks -
                         _lastSampleHostTicks) *
                        HostTickSeconds))
                : 0f;

        float expectedInterval =
            Mathf.Clamp(
                _sampleIntervalEma,
                1f / 120f,
                0.120f);

        float cadencePredictionCap =
            Mathf.Clamp(
                expectedInterval * 1.35f,
                0.035f,
                maximumPredictionSeconds);

        float horizon =
            Mathf.Clamp(
                age + additionalDisplayLeadSeconds,
                0f,
                cadencePredictionCap);

        float staleStart =
            expectedInterval * 1.10f;

        float staleSeconds =
            Mathf.Max(
                0f,
                age - staleStart);

        float velocityRetention =
            Mathf.Exp(
                -staleVelocityDecayResponse *
                staleSeconds);

        float qualityFactor =
            Mathf.Lerp(
                0.72f,
                1f,
                Mathf.Clamp01(debugGeometryQuality));

        float effectivePredictionStrength =
            predictionStrength *
            qualityFactor;

        Vector3 predictedPosition =
            _samplePosition +
            Vector3.ClampMagnitude(
                _samplePositionVelocity *
                horizon *
                effectivePredictionStrength *
                velocityRetention,
                maximumPositionPrediction);

        Quaternion predictedRotation =
            KiwiPrecisionTrackingMath.ExtrapolateRotation(
                _sampleRotation,
                _sampleAngularVelocityDeg *
                effectivePredictionStrength *
                velocityRetention,
                horizon,
                maximumRotationPredictionDegrees);

        Vector3 predictedScale =
            _sampleScale +
            Vector3.ClampMagnitude(
                _sampleScaleVelocity *
                horizon *
                effectivePredictionStrength *
                velocityRetention,
                maximumScalePrediction);

        float positionMotion =
            Mathf.Clamp01(
                Mathf.Max(
                    _samplePositionVelocity.magnitude /
                    Mathf.Max(
                        0.0001f,
                        positionMotionFullSpeed),
                    Vector3.Distance(
                        _renderPosition,
                        predictedPosition) /
                    0.006f));

        float rotationMotion =
            Mathf.Clamp01(
                Mathf.Max(
                    _sampleAngularVelocityDeg.magnitude /
                    Mathf.Max(
                        0.0001f,
                        rotationMotionFullSpeed),
                    Quaternion.Angle(
                        _renderRotation,
                        predictedRotation) /
                    4f));

        float scaleMotion =
            Mathf.Clamp01(
                Mathf.Max(
                    _sampleScaleVelocity.magnitude /
                    Mathf.Max(
                        0.0001f,
                        scaleMotionFullSpeed),
                    Vector3.Distance(
                        _renderScale,
                        predictedScale) /
                    0.020f));

        if (
            positionMotion < 0.10f &&
            Vector3.Distance(
                _renderPosition,
                predictedPosition) <
            positionRestError
        )
        {
            predictedPosition = _renderPosition;
            _positionSmoothVelocity =
                Vector3.Lerp(
                    _positionSmoothVelocity,
                    Vector3.zero,
                    ExpAlpha(24f, dt));
        }

        if (
            rotationMotion < 0.10f &&
            Quaternion.Angle(
                _renderRotation,
                predictedRotation) <
            rotationRestErrorDegrees
        )
        {
            predictedRotation = _renderRotation;
        }

        if (
            scaleMotion < 0.10f &&
            Vector3.Distance(
                _renderScale,
                predictedScale) <
            scaleRestError
        )
        {
            predictedScale = _renderScale;
            _scaleSmoothVelocity =
                Vector3.Lerp(
                    _scaleSmoothVelocity,
                    Vector3.zero,
                    ExpAlpha(22f, dt));
        }

        float positionSmoothTime =
            Mathf.Lerp(
                restingPositionSmoothTime,
                movingPositionSmoothTime,
                SmoothMotionWeight(positionMotion));

        float scaleSmoothTime =
            Mathf.Lerp(
                restingScaleSmoothTime,
                movingScaleSmoothTime,
                SmoothMotionWeight(scaleMotion));

        _renderPosition =
            Vector3.SmoothDamp(
                _renderPosition,
                predictedPosition,
                ref _positionSmoothVelocity,
                positionSmoothTime,
                maximumPositionSpeed * 1.5f,
                dt);

        float rotationResponse =
            Mathf.Lerp(
                restingRotationResponse,
                movingRotationResponse,
                SmoothMotionWeight(rotationMotion));

        _renderRotation =
            Quaternion.Slerp(
                _renderRotation,
                predictedRotation,
                ExpAlpha(rotationResponse, dt));

        _renderScale =
            Vector3.SmoothDamp(
                _renderScale,
                predictedScale,
                ref _scaleSmoothVelocity,
                scaleSmoothTime,
                maximumScaleSpeed * 1.5f,
                dt);

        _motionRoot.localPosition =
            _renderPosition;
        _motionRoot.localRotation =
            _renderRotation;
        _motionRoot.localScale =
            _renderScale;

        debugTrackingAgeMs =
            age * 1000f;
        debugPredictionHorizonMs =
            horizon * 1000f;
    }

    private bool IsLargeDiscontinuity(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale)
    {
        if (!_sampleInitialized)
        {
            return false;
        }

        return
            Vector3.Distance(
                _samplePosition,
                position) >
                positionResetDistance ||
            Quaternion.Angle(
                _sampleRotation,
                rotation) >
                rotationResetDegrees ||
            Vector3.Distance(
                _sampleScale,
                scale) >
                scaleResetDistance;
    }

    private void UpdateDiagnostics(
        FacePrecisionTrackingData data,
        long sampleHostTicks)
    {
        debugBackend =
            data.backend.ToString();
        debugGeometryQuality =
            Mathf.Clamp01(data.geometryQuality);
        debugFrameId =
            data.frameId;

        debugTrackingSampleHz =
            _sampleIntervalEma > 0.0001f
                ? 1f / _sampleIntervalEma
                : 0f;

        long now =
            System.Diagnostics.Stopwatch.GetTimestamp();

        debugTrackingAgeMs =
            sampleHostTicks > 0L
                ? Mathf.Max(
                    0f,
                    (float)(
                        (now - sampleHostTicks) *
                        HostTickSeconds *
                        1000.0))
                : 0f;
    }

    private static long ResolveSampleHostTicks(
        FacePrecisionTrackingData data)
    {
        if (
            data.hasMatchedSubmissionTiming &&
            data.submissionHostTicks > 0L
        )
        {
            return data.submissionHostTicks;
        }

        if (data.arrivalHostTicks > 0L)
        {
            return data.arrivalHostTicks;
        }

        return
            System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static float SmoothMotionWeight(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private static float ExpAlpha(
        float response,
        float dt)
    {
        if (response <= 0f)
        {
            return 1f;
        }

        return
            1f -
            Mathf.Exp(
                -response *
                Mathf.Max(0f, dt));
    }
}
